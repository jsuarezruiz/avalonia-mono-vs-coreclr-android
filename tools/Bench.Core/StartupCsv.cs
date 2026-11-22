using System.Globalization;
using System.Text;

namespace Bench;

/// <summary>
/// Reads and writes the startup CSV. A missing column is simply empty.
/// </summary>
public static class StartupCsv
{
    public static readonly string[] Columns =
    [
        "scenario", "track", "variant", "run", "device", "android_api", "abi", "is_emulator", "launch_state",
        "total_time_ms", "wait_time_ms", "application_ctor_ms", "framework_init_ms", "view_loaded_ms",
        "first_frame_ms", "fully_drawn_ms", "pss_kb", "apk_bytes", "native_lib_timeouts", "host_load", "host_cpus",
        "system_fully_drawn_ms",
    ];

    public static void Write(string path, IEnumerable<RunRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns));
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',',
                Quote(r.Scenario), Quote(r.Track), Quote(r.Variant), r.Run.ToString(CultureInfo.InvariantCulture),
                Quote(r.Device), Quote(r.AndroidApi), Quote(r.Abi), r.IsEmulator ? "1" : "0", Quote(r.LaunchState),
                F(r.TotalTimeMs), F(r.WaitTimeMs), F(r.ApplicationCtorMs), F(r.FrameworkInitMs), F(r.ViewLoadedMs),
                F(r.FirstFrameMs), F(r.FullyDrawnMs), F(r.PssKb), r.ApkBytes.ToString(CultureInfo.InvariantCulture),
                r.NativeLibTimeouts.ToString(CultureInfo.InvariantCulture), F(r.HostLoad),
                r.HostCpus.ToString(CultureInfo.InvariantCulture), F(r.SystemFullyDrawnMs)));
        }

        File.WriteAllText(path, sb.ToString());
    }

    public static IReadOnlyList<RunRow> Read(string path) => Parse(File.ReadAllLines(path));

    public static IReadOnlyList<RunRow> Parse(IEnumerable<string> lines)
    {
        var rows = new List<RunRow>();
        string[]? header = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitLine(line);
            if (header is null)
            {
                header = fields;
                continue;
            }

            string Get(string name)
            {
                var i = Array.IndexOf(header, name);
                return i >= 0 && i < fields.Length ? fields[i] : "";
            }

            double? D(string name) =>
                double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            int I(string name) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

            // Frame-pacing files share the folder; they have no total_time_ms and are not startup rows.
            if (Array.IndexOf(header, "total_time_ms") < 0) return Array.Empty<RunRow>();

            rows.Add(new RunRow
            {
                Scenario = Get("scenario") is { Length: > 0 } sc ? sc : "dashboard",
                Track = Get("track"), Variant = Get("variant"), Run = I("run"), Device = Get("device"),
                AndroidApi = Get("android_api"), Abi = Get("abi"), IsEmulator = Get("is_emulator") == "1",
                LaunchState = Get("launch_state") is { Length: > 0 } ls ? ls : "COLD",
                TotalTimeMs = D("total_time_ms"), WaitTimeMs = D("wait_time_ms"),
                ApplicationCtorMs = D("application_ctor_ms"), FrameworkInitMs = D("framework_init_ms"),
                ViewLoadedMs = D("view_loaded_ms"), FirstFrameMs = D("first_frame_ms"), FullyDrawnMs = D("fully_drawn_ms"),
                PssKb = D("pss_kb"), ApkBytes = (long)(D("apk_bytes") ?? 0), NativeLibTimeouts = I("native_lib_timeouts"),
                HostLoad = D("host_load"), HostCpus = I("host_cpus"), SystemFullyDrawnMs = D("system_fully_drawn_ms"),
            });
        }

        return rows;
    }

    private static string F(double? v) => v is { } x ? x.ToString("0.###", CultureInfo.InvariantCulture) : "";

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Splits one CSV line, honouring double quotes.</summary>
    public static string[] SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }

        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
