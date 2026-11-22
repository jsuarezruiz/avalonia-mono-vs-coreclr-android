using System.Globalization;
using System.Text;

namespace Bench;

/// <summary>
/// Summarises startup and frame-pacing CSVs as Markdown: medians, p95 and a bootstrap interval for the difference
/// from a baseline.
/// </summary>
public static class StatsReport
{
    public const string DefaultBaseline = "net10/mono-aot-profiled";

    public static string Render(IReadOnlyList<string> paths, string baseline = DefaultBaseline, bool includeWarm = false)
    {
        var files = new List<string>();
        foreach (var p in paths)
            files.AddRange(Directory.Exists(p) ? Directory.GetFiles(p, "*.csv").Order(StringComparer.Ordinal) : [p]);

        var startup = new List<RunRow>();
        var frames = new List<FrameRow>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0) continue;
            if (FrameCsv.IsFrameFile(lines[0])) frames.AddRange(FrameCsv.Parse(lines));
            else startup.AddRange(StartupCsv.Parse(lines));
        }

        if (startup.Count == 0 && frames.Count == 0)
            throw new BenchException($"No rows found in: {string.Join(", ", paths)}");

        var sb = new StringBuilder();
        sb.Append($"Sources: {files.Count} file(s), {startup.Count} startup rows, {frames.Count} frame rows.\n\n");
        if (startup.Count > 0) Startup(sb, startup, baseline, includeWarm);
        if (frames.Count > 0) Frames(sb, frames, baseline);
        return sb.ToString();
    }

    // ---- startup --------------------------------------------------------------------------------------------------
    private static void Startup(StringBuilder sb, List<RunRow> rows, string baseline, bool includeWarm)
    {
        var kept = rows.Where(r => includeWarm || r.LaunchState == "COLD").ToList();
        var dropped = rows.Count - kept.Count;

        var groups = Group(kept, r => (r.Scenario, r.Device, r.AndroidApi, r.Abi, r.IsEmulator), r => r.Entry);

        sb.Append("# Startup results\n\n");
        if (dropped > 0) sb.Append($"{dropped} non-cold rows dropped.\n\n");

        foreach (var ((scenario, device, api, abi, emulator), variants) in groups)
        {
            sb.Append($"## {scenario}: {device} (API {api}, {abi})\n\n");
            if (emulator) sb.Append("> **Emulator.** Fine for validating the harness, not for comparing runtimes.\n\n");
            sb.Append(BusyWarning(variants.SelectMany(v => v.Rows).Select(r => r.HostLoad), variants.SelectMany(v => v.Rows).Select(r => (double)r.HostCpus)));

            var (baseName, note) = PickBaseline(variants.Select(v => v.Name).ToList(), baseline);
            if (note is not null) sb.Append(note).Append('\n');

            var hasSystem = variants.Any(v => v.Rows.Any(r => r.SystemFullyDrawnMs is not null));
            var header = new List<string> { "Variant", "n", "TotalTime (am start -W) median", "p95", "first frame median", "p95", "fully drawn median", "p95" };
            if (hasSystem) header.AddRange(["Android fully drawn median", "p95"]);
            header.AddRange([$"fully drawn vs {baseName}", "PSS (MB)", "APK (MB)"]);
            sb.Append("| ").Append(string.Join(" | ", header)).Append(" |\n");
            sb.Append('|').Append(string.Join('|', header.Select(_ => "---"))).Append("|\n");

            var baseFd = Values(variants.FirstOrDefault(v => v.Name == baseName).Rows, r => r.FullyDrawnMs);
            foreach (var (name, vrows) in variants)
            {
                var cells = new List<string> { name + (name == baseName ? " *(baseline)*" : ""), vrows.Count.ToString(CultureInfo.InvariantCulture) };
                void Pair(Func<RunRow, double?> pick)
                {
                    var v = Values(vrows, pick);
                    cells.AddRange(v.Count > 0 ? [Fmt(Statistics.Median(v)), Fmt(Statistics.Percentile(v, 95))] : ["n/a", "n/a"]);
                }

                Pair(r => r.TotalTimeMs);
                Pair(r => r.FirstFrameMs);
                Pair(r => r.FullyDrawnMs);
                if (hasSystem) Pair(r => r.SystemFullyDrawnMs);

                var fd = Values(vrows, r => r.FullyDrawnMs);
                if (name == baseName || fd.Count == 0 || baseFd.Count == 0)
                {
                    cells.Add("-");
                }
                else
                {
                    var delta = (Statistics.Median(fd) - Statistics.Median(baseFd)) / Statistics.Median(baseFd);
                    var (lo, hi) = Statistics.BootstrapMedianDelta(baseFd, fd);
                    cells.Add(string.Create(CultureInfo.InvariantCulture, $"{Signed(delta * 100)}% [{Signed(lo * 100)}%, {Signed(hi * 100)}%]"));
                }

                var pss = Values(vrows, r => r.PssKb);
                cells.Add(pss.Count > 0 ? Fmt(Statistics.Median(pss) / 1024.0) : "n/a");
                var apk = vrows[0].ApkBytes;
                cells.Add(apk > 0 ? Fmt(apk / 1048576.0, 1) : "n/a");
                sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            }

            sb.Append("\n_Times in ms since process start (first frame, fully drawn) or as reported by `am start -W` (TotalTime). " +
                      "Negative delta = faster than baseline; brackets are a 95% bootstrap interval of the median difference._\n\n");

            foreach (var (name, vrows) in variants)
            {
                var stalled = vrows.Count(r => r.NativeLibTimeouts > 0);
                if (stalled > 0)
                    sb.Append($"> **Warning, `{name}`:** {stalled} of {vrows.Count} runs hit the .NET for Android native-library resolver timeout " +
                              "(3 s each, see repros/coreclr-android-pinvoke-stall). Their startup times contain that stall.\n\n");
            }
        }
    }

    // ---- frames ---------------------------------------------------------------------------------------------------
    private static void Frames(StringBuilder sb, List<FrameRow> rows, string baseline)
    {
        sb.Append("# Frame-pacing results (sparkle)\n\n");
        foreach (var ((device, api, abi, emulator, hz), cells) in Group(rows, r => (r.Device, r.AndroidApi, r.Abi, r.IsEmulator, r.RefreshHz),
                     r => $"{r.Effect}\u0001{r.Phase}"))
        {
            sb.Append($"## {device} (API {api}, {abi}, {(hz.Length > 0 ? hz : "?")} Hz)\n\n");
            if (emulator) sb.Append("> **Emulator.** Fine for validating the harness, not for comparing runtimes.\n\n");
            sb.Append(BusyWarning(cells.SelectMany(c => c.Rows).Select(r => r.HostLoad), cells.SelectMany(c => c.Rows).Select(r => (double)r.HostCpus)));

            string[] header = ["Effect", "Phase", "Variant", "runs", "fps", "p50 ms", "p95 ms", "p99 ms", "max ms", "slow %", "GC0", "alloc MB", "p95 vs baseline"];
            sb.Append("| ").Append(string.Join(" | ", header)).Append(" |\n");
            sb.Append('|').Append(string.Join('|', header.Select(_ => "---"))).Append("|\n");

            foreach (var (key, cellRows) in cells)
            {
                var split = key.Split('\u0001');
                var (effect, phase) = (split[0], split[1]);
                var variants = cellRows.GroupInOrder(r => r.Entry);
                var (baseName, _) = PickBaseline(variants.Select(v => v.Name).ToList(), baseline);
                var baseP95 = MedianOrNull(variants.FirstOrDefault(v => v.Name == baseName).Rows, r => r.P95Ms);

                foreach (var (name, vrows) in variants)
                {
                    var fps = vrows.Where(r => r.MeanMs is > 0).Select(r => 1000.0 / r.MeanMs!.Value).ToList();
                    var slow = vrows.Where(r => r.N is > 0 && r.Slow is not null).Select(r => 100.0 * r.Slow!.Value / r.N!.Value).ToList();
                    var p95 = MedianOrNull(vrows, r => r.P95Ms);
                    var delta = name == baseName || p95 is null || baseP95 is null or 0
                        ? "-"
                        : string.Create(CultureInfo.InvariantCulture, $"{Signed((p95.Value - baseP95.Value) / baseP95.Value * 100)}%");

                    sb.Append("| ").Append(string.Join(" | ", new[]
                    {
                        effect, phase, name + (name == baseName ? " *(baseline)*" : ""), vrows.Count.ToString(CultureInfo.InvariantCulture),
                        fps.Count > 0 ? Fmt(Statistics.Median(fps), 1) : "n/a", Fmt(MedianOrNull(vrows, r => r.P50Ms), 1), Fmt(p95, 1),
                        Fmt(MedianOrNull(vrows, r => r.P99Ms), 1), Fmt(MedianOrNull(vrows, r => r.MaxMs), 1),
                        slow.Count > 0 ? Fmt(Statistics.Median(slow), 1) : "n/a", Fmt(MedianOrNull(vrows, r => r.Gc0), 0),
                        Fmt(MedianOrNull(vrows, r => r.AllocMb), 0), delta,
                    })).Append(" |\n");
                }
            }

            sb.Append("\n_Frame interval = time between consecutive rendered frames, so at 60 Hz a healthy app sits near 16.7 ms. " +
                      "\"slow\" counts frames longer than 1.5 refresh intervals. fps, p50..max, slow %, GC0 and alloc are medians across runs " +
                      "(GC0 and alloc per phase). The last column is the relative change of the median p95 against the baseline; " +
                      "with only a handful of runs treat it as indicative._\n\n");
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------
    /// <summary>Groups rows by a key, then by name, both in first-seen order (the order the files list them).</summary>
    private static List<(TKey Key, List<(string Name, List<T> Rows)> Items)> Group<T, TKey>(
        IEnumerable<T> rows, Func<T, TKey> key, Func<T, string> name) where TKey : notnull
    {
        var order = new List<TKey>();
        var map = new Dictionary<TKey, List<T>>();
        foreach (var r in rows)
        {
            var k = key(r);
            if (!map.TryGetValue(k, out var list)) { map[k] = list = []; order.Add(k); }
            list.Add(r);
        }

        return order.Select(k => (k, map[k].GroupInOrder(name))).ToList();
    }

    private static List<(string Name, List<T> Rows)> GroupInOrder<T>(this IEnumerable<T> rows, Func<T, string> name)
    {
        var result = new List<(string Name, List<T> Rows)>();
        foreach (var r in rows)
        {
            var n = name(r);
            var i = result.FindIndex(x => x.Name == n);
            if (i < 0) result.Add((n, [r])); else result[i].Rows.Add(r);
        }

        return result;
    }

    private static (string Name, string? Note) PickBaseline(IReadOnlyList<string> names, string wanted)
    {
        if (names.Contains(wanted)) return (wanted, null);
        var suffix = names.FirstOrDefault(n => n.EndsWith("/" + wanted, StringComparison.Ordinal));
        if (suffix is not null) return (suffix, null);
        return (names[0], $"_Baseline '{wanted}' not present; using '{names[0]}'._");
    }

    private static string BusyWarning(IEnumerable<double?> loads, IEnumerable<double> cpus)
    {
        var l = loads.Where(v => v is not null).Select(v => v!.Value).ToList();
        var c = cpus.Where(v => v > 0).ToList();
        if (l.Count == 0 || c.Count == 0) return "";
        var load = Statistics.Median(l);
        var cpu = Statistics.Median(c);
        return load <= cpu ? "" :
            $"> **Busy host.** Median load average {Fmt(load)} on {Fmt(cpu)} CPUs while these rows were recorded, so timings are " +
            "unreliable. Re-run on a quiet machine before comparing runtimes.\n\n";
    }

    private static List<double> Values<T>(IEnumerable<T> rows, Func<T, double?> pick) =>
        rows.Select(pick).Where(v => v is not null).Select(v => v!.Value).ToList();

    private static double? MedianOrNull<T>(IEnumerable<T> rows, Func<T, double?> pick)
    {
        var v = Values(rows, pick);
        return v.Count == 0 ? null : Statistics.Median(v);
    }

    private static string Fmt(double? value, int digits = 0) =>
        value is not { } v || double.IsNaN(v) ? "n/a" : Format.Fixed(v, digits);

    private static string Signed(double v) => Format.Signed(v);
}
