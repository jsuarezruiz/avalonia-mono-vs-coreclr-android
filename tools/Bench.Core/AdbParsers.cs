using System.Globalization;
using System.Text.RegularExpressions;

namespace Bench;

public sealed record AmStartResult(string Status, string LaunchState, double? TotalTimeMs, double? WaitTimeMs);

public sealed record AdbDevice(string Serial, string State, string Model);

/// <summary>One "frames effect=... phase=..." summary the frame-pacing app logs per effect and phase.</summary>
public sealed record FrameSample(
    string Effect, string Phase, double? N, double? MeanMs, double? P50Ms, double? P95Ms, double? P99Ms, double? MaxMs,
    double? Slow, string RefreshHz, double? Gc0, double? Gc1, double? Gc2, double? AllocMb);

/// <summary>Parsers for the text adb prints. Kept apart from the process code so they can be tested on samples.</summary>
public static partial class AdbParsers
{
    /// <summary>Output of <c>am start -W</c>: Status, LaunchState, TotalTime, WaitTime.</summary>
    public static AmStartResult ParseAmStart(string output)
    {
        string? status = null, state = null;
        double? total = null, wait = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "Status": status = value; break;
                case "LaunchState": state = value; break;
                case "TotalTime": total = ParseNumber(value); break;
                case "WaitTime": wait = ParseNumber(value); break;
            }
        }

        return new AmStartResult(status ?? "", state ?? "", total, wait);
    }

    /// <summary>The app's own marks from the BENCH log tag: "phase=fully_drawn ms=1220". First occurrence wins.</summary>
    public static Dictionary<string, double> ParseBenchPhases(string log)
    {
        var phases = new Dictionary<string, double>();
        foreach (Match m in BenchPhaseRegex().Matches(log))
            phases.TryAdd(m.Groups["name"].Value, double.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture));
        return phases;
    }

    /// <summary>
    /// Android's own "Fully drawn" line for <paramref name="packageId"/>, in ms since the launch request. Present for
    /// any app that calls Activity.ReportFullyDrawn. Example: "Fully drawn pkg/.Main: +1s311ms".
    /// </summary>
    public static double? ParseSystemFullyDrawn(string log, string packageId)
    {
        foreach (Match m in SystemFullyDrawnRegex().Matches(log))
        {
            if (!m.Groups["component"].Value.StartsWith(packageId + "/", StringComparison.Ordinal)) continue;
            return ParseDuration(m.Groups["t"].Value);
        }

        return null;
    }

    /// <summary>"+1m2s311ms" style durations, in milliseconds.</summary>
    public static double? ParseDuration(string text)
    {
        var matches = DurationPartRegex().Matches(text);
        if (matches.Count == 0) return null;
        double ms = 0;
        foreach (Match m in matches)
        {
            var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            ms += m.Groups["unit"].Value switch
            {
                "h" => n * 3_600_000,
                "m" => n * 60_000,
                "s" => n * 1000,
                _ => n,
            };
        }

        return ms;
    }

    /// <summary>Total PSS in KB from <c>dumpsys meminfo &lt;pkg&gt;</c>.</summary>
    public static double? ParsePss(string meminfo)
    {
        var m = PssRegex().Match(meminfo);
        if (m.Success) return double.Parse(m.Groups["kb"].Value, CultureInfo.InvariantCulture);
        m = TotalRowRegex().Match(meminfo);
        return m.Success ? double.Parse(m.Groups["kb"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary><c>pm resolve-activity --brief</c> prints the component on its last line: "pkg/activity".</summary>
    public static string? ParseResolvedComponent(string output)
    {
        string? found = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && line.Contains('/') && !line.Contains(' ') && !line.Contains('=')) found = line;
        }

        return found;
    }

    /// <summary>How many times the .NET for Android native-library resolver gave up waiting (3 s each).</summary>
    public static int CountResolverTimeouts(string log) =>
        log.Split('\n').Count(l => l.Contains("Timeout while waiting for shared library", StringComparison.Ordinal));

    /// <summary>
    /// The frame-pacing summaries in a BENCH log: "frames effect=waterfall phase=idle n=41 mean_ms=16.7 ...". A phase
    /// with no frames ("n=0") is kept, with its numbers empty.
    /// </summary>
    public static IReadOnlyList<FrameSample> ParseFrameLines(string log)
    {
        var samples = new List<FrameSample>();
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains(" frames effect=", StringComparison.Ordinal)) continue;
            string? Get(string key)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"\s" + System.Text.RegularExpressions.Regex.Escape(key) + @"=(?<v>\S+)");
                return m.Success ? m.Groups["v"].Value : null;
            }

            double? D(string key) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            samples.Add(new FrameSample(Get("effect") ?? "", Get("phase") ?? "", D("n"), D("mean_ms"), D("p50_ms"), D("p95_ms"),
                D("p99_ms"), D("max_ms"), D("slow"), Get("refresh_hz") ?? "", D("gc0"), D("gc1"), D("gc2"), D("alloc_mb")));
        }

        return samples;
    }

    /// <summary>True when adb's input command was refused for lack of the INJECT_EVENTS permission.</summary>
    public static bool InputWasRefused(string output) =>
        output.Contains("INJECT_EVENTS", StringComparison.Ordinal) || output.Contains("SecurityException", StringComparison.Ordinal);

    /// <summary>"Awake", "Asleep" or "Dozing" from <c>dumpsys power</c> (mWakefulness=...); null when not found.</summary>
    public static string? ParseWakefulness(string dumpsysPower)
    {
        var m = System.Text.RegularExpressions.Regex.Match(dumpsysPower, @"mWakefulness=(?<w>\w+)");
        return m.Success ? m.Groups["w"].Value : null;
    }

    /// <summary>Output of <c>adb devices -l</c>.</summary>
    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var raw in output.Split('\n').Skip(1))
        {
            var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || raw.StartsWith('*')) continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?[6..] ?? "";
            devices.Add(new AdbDevice(parts[0], parts[1], model));
        }

        return devices;
    }

    private static double? ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@"BENCH\s*:\s*phase=(?<name>\w+)\s+ms=(?<ms>\d+)")]
    private static partial Regex BenchPhaseRegex();

    [GeneratedRegex(@"Fully drawn (?<component>[^\s:]+): \+(?<t>[0-9hms]+)")]
    private static partial Regex SystemFullyDrawnRegex();

    [GeneratedRegex(@"(?<n>\d+)(?<unit>ms|h|m|s)")]
    private static partial Regex DurationPartRegex();

    [GeneratedRegex(@"TOTAL PSS:\s+(?<kb>\d+)")]
    private static partial Regex PssRegex();

    [GeneratedRegex(@"^\s*TOTAL\s+(?<kb>\d+)", RegexOptions.Multiline)]
    private static partial Regex TotalRowRegex();
}
