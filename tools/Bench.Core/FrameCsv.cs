using System.Globalization;

namespace Bench;

/// <summary>One effect/phase of a frame-pacing run (bench frames).</summary>
public sealed class FrameRow
{
    public string Track { get; init; } = "";
    public string Variant { get; init; } = "";
    public int Run { get; init; }
    public string Device { get; init; } = "";
    public string AndroidApi { get; init; } = "";
    public string Abi { get; init; } = "";
    public bool IsEmulator { get; init; }
    public string RefreshHz { get; init; } = "";
    public string Effect { get; init; } = "";
    public string Phase { get; init; } = "";
    public double? N { get; init; }
    public double? MeanMs { get; init; }
    public double? P50Ms { get; init; }
    public double? P95Ms { get; init; }
    public double? P99Ms { get; init; }
    public double? MaxMs { get; init; }
    public double? Slow { get; init; }
    public double? Gc0 { get; init; }
    public double? Gc1 { get; init; }
    public double? Gc2 { get; init; }
    public double? AllocMb { get; init; }
    public double? HostLoad { get; init; }
    public int HostCpus { get; init; }

    public string Entry => string.IsNullOrEmpty(Track) ? Variant : $"{Track}/{Variant}";
}

public static class FrameCsv
{
    /// <summary>True when the header is a frame-pacing file (it has p95_ms and no total_time_ms).</summary>
    public static bool IsFrameFile(string headerLine)
    {
        var header = StartupCsv.SplitLine(headerLine);
        return header.Contains("p95_ms") && !header.Contains("total_time_ms");
    }

    public static IReadOnlyList<FrameRow> Parse(IEnumerable<string> lines)
    {
        var rows = new List<FrameRow>();
        string[]? header = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = StartupCsv.SplitLine(line);
            if (header is null) { header = f; continue; }
            string Get(string n) { var i = Array.IndexOf(header, n); return i >= 0 && i < f.Length ? f[i] : ""; }
            double? D(string n) => double.TryParse(Get(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            rows.Add(new FrameRow
            {
                Track = Get("track"), Variant = Get("variant"), Device = Get("device"), AndroidApi = Get("android_api"),
                Abi = Get("abi"), IsEmulator = Get("is_emulator") == "1", RefreshHz = Get("refresh_hz"), Effect = Get("effect"),
                Phase = Get("phase"), N = D("n"), MeanMs = D("mean_ms"), P50Ms = D("p50_ms"), P95Ms = D("p95_ms"),
                P99Ms = D("p99_ms"), MaxMs = D("max_ms"), Slow = D("slow"), Gc0 = D("gc0"), Gc1 = D("gc1"), Gc2 = D("gc2"),
                AllocMb = D("alloc_mb"), Run = (int)(D("run") ?? 0),
                HostLoad = D("host_load"), HostCpus = (int)(D("host_cpus") ?? 0),
            });
        }

        return rows;
    }

    public static readonly string[] Columns =
    [
        "track", "variant", "run", "device", "android_api", "abi", "is_emulator", "refresh_hz", "effect", "phase", "n",
        "mean_ms", "p50_ms", "p95_ms", "p99_ms", "max_ms", "slow", "gc0", "gc1", "gc2", "alloc_mb", "host_load", "host_cpus",
    ];

    /// <summary>Writes a frame-pacing CSV.</summary>
    public static void Write(string path, IEnumerable<FrameRow> rows)
    {
        static string F(double? v) => v is { } x ? x.ToString("0.###", CultureInfo.InvariantCulture) : "";
        static string Q(string t) => t.Contains(',') || t.Contains('"') ? "\"" + t.Replace("\"", "\"\"") + "\"" : t;
        var lines = new List<string> { string.Join(',', Columns) };
        lines.AddRange(rows.Select(r => string.Join(',', Q(r.Track), Q(r.Variant), r.Run.ToString(CultureInfo.InvariantCulture),
            Q(r.Device), Q(r.AndroidApi), Q(r.Abi), r.IsEmulator ? "1" : "0", Q(r.RefreshHz), Q(r.Effect), Q(r.Phase), F(r.N),
            F(r.MeanMs), F(r.P50Ms), F(r.P95Ms), F(r.P99Ms), F(r.MaxMs), F(r.Slow), F(r.Gc0), F(r.Gc1), F(r.Gc2), F(r.AllocMb),
            F(r.HostLoad), r.HostCpus.ToString(CultureInfo.InvariantCulture))));
        File.WriteAllLines(path, lines);
    }
}
