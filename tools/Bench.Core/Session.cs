using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bench;

/// <summary>An app in a session: <see cref="Entry"/> keys its rows, <see cref="Name"/> and <see cref="Sub"/> are what the video shows.</summary>
public sealed record CandidateInfo(string Entry, string Name, string Sub, string ApkPath, string PackageId, long ApkBytes);

public sealed record SessionInfo(DeviceInfo Device, CandidateInfo Left, CandidateInfo Right, string Eyebrow, string Note, DateTimeOffset Created);

/// <summary>The files of a session folder.</summary>
public static class SessionFiles
{
    public const string InfoFile = "session.json", RunsFile = "runs.csv", TakesFile = "takes.csv", TakesDir = "takes";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep "·" and apostrophes readable
    };

    /// <summary>Writes session.json. The device serial number identifies a person's hardware, so it is never saved.</summary>
    public static void WriteInfo(string dir, SessionInfo info) =>
        File.WriteAllText(Path.Combine(dir, InfoFile), JsonSerializer.Serialize(info with { Device = info.Device with { Serial = "" } }, Json));

    public static SessionInfo ReadInfo(string dir)
    {
        var json = Path.Combine(dir, InfoFile);
        if (!File.Exists(json)) throw new BenchException($"{dir} is not a session folder (no {InfoFile}).");
        return JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(json), Json)!;
    }

    /// <summary>Writes takes.csv with clip paths relative to the folder, so a session can be moved or zipped.</summary>
    public static void WriteTakes(string dir, IEnumerable<Take> takes)
    {
        string F(double? v) => v is { } x ? x.ToString("0.###", CultureInfo.InvariantCulture) : "";
        var lines = new List<string> { "side,entry,take,clip,fully_drawn_ms,system_fully_drawn_ms,total_time_ms,native_lib_timeouts,host_load" };
        lines.AddRange(takes.Select(t => string.Join(',', t.Side, StartupCsvQuote(t.Entry), t.Number,
            StartupCsvQuote(Path.GetRelativePath(dir, t.ClipPath).Replace('\\', '/')),
            F(t.AppFullyDrawnMs), F(t.SystemFullyDrawnMs), F(t.TotalTimeMs), t.NativeLibTimeouts, F(t.HostLoad))));
        File.WriteAllLines(Path.Combine(dir, TakesFile), lines);
    }

    /// <summary>Reads takes.csv; clip paths are relative to the folder.</summary>
    public static IReadOnlyList<Take> ReadTakes(string dir)
    {
        var path = Path.Combine(dir, TakesFile);
        if (!File.Exists(path)) throw new BenchException($"{dir} has no {TakesFile}; record takes first.");
        string[]? header = null;
        var takes = new List<Take>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = StartupCsv.SplitLine(line);
            if (header is null) { header = f; continue; }
            string Get(string n) { var i = Array.IndexOf(header, n); return i >= 0 && i < f.Length ? f[i] : ""; }
            double? D(string n) => double.TryParse(Get(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

            takes.Add(new Take(Get("side"), Get("entry"), int.Parse(Get("take"), CultureInfo.InvariantCulture),
                Path.Combine(dir, Get("clip")), D("fully_drawn_ms"), D("system_fully_drawn_ms"), D("total_time_ms"),
                (int)(D("native_lib_timeouts") ?? 0), D("host_load")));
        }

        return takes;
    }

    private static string StartupCsvQuote(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>Display names for this repo's <c>track/variant</c> entries, e.g. net10/mono-aot-profiled.</summary>
public static class RepoLabels
{
    public static (string Name, string Sub) For(string entry)
    {
        var parts = entry.Split('/');
        if (parts.Length != 2 || !parts[0].StartsWith("net", StringComparison.Ordinal)) return (entry, "");
        var net = ".NET " + parts[0][3..];
        return parts[1] == "coreclr"
            ? ("CoreCLR", "ReadyToRun · " + net)
            : ("Mono", parts[1] switch
            {
                "mono-jit" => "JIT",
                "mono-aot-profiled" => "AOT (profiled)",
                "mono-aot-full" => "full AOT",
                var other => other,
            } + " · " + net);
    }
}
