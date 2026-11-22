using System.Text.RegularExpressions;

namespace Bench;

/// <summary>Where this repository keeps its apps: tracks, scenarios, variants and the APKs they build to.</summary>
public static partial class RepoLayout
{
    public static readonly string[] Tracks = ["net10", "net11"];
    public static readonly string[] Scenarios = ["dashboard", "sparkle"];
    public static readonly string[] AllVariants = ["mono-jit", "mono-aot-profiled", "mono-aot-full", "coreclr"];

    /// <summary>.NET 11 dropped Mono on Android (NETSDK1242), so its track only has CoreCLR.</summary>
    public static string[] VariantsFor(string track) => track switch
    {
        "net10" => AllVariants,
        "net11" => ["coreclr"],
        _ => throw new BenchException($"Unknown track '{track}' (valid: {string.Join(", ", Tracks)})."),
    };

    public static string ProjectFor(string scenario) => scenario switch
    {
        "dashboard" => "src/BenchApp.Android/BenchApp.Android.csproj",
        "sparkle" => "src/BenchApp.Sparkle.Android/BenchApp.Sparkle.Android.csproj",
        _ => throw new BenchException($"Unknown scenario '{scenario}' (valid: {string.Join(", ", Scenarios)})."),
    };

    public static string ArtifactDir(string root, string track, string scenario, string variant) =>
        Path.Combine(root, "artifacts", track, scenario, variant);

    public static string? FindSignedApk(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*-Signed.apk", SearchOption.AllDirectories).FirstOrDefault() : null;

    /// <summary>Every <c>track/variant</c> that has a signed APK for the scenario, in a stable order.</summary>
    public static IReadOnlyList<string> BuiltEntries(string root, string scenario)
    {
        var entries = new List<string>();
        foreach (var track in Tracks)
        {
            var scenarioDir = Path.Combine(root, "artifacts", track, scenario);
            if (!Directory.Exists(scenarioDir)) continue;
            foreach (var dir in Directory.GetDirectories(scenarioDir).Order(StringComparer.Ordinal))
                if (FindSignedApk(dir) is not null) entries.Add($"{track}/{Path.GetFileName(dir)}");
        }

        return entries;
    }

    /// <summary>"net10/mono-jit" to ("net10", "mono-jit"); null for anything else.</summary>
    public static (string Track, string Variant)? SplitEntry(string entry)
    {
        var m = EntryRegex().Match(entry);
        return m.Success ? (m.Groups["track"].Value, m.Groups["variant"].Value) : null;
    }

    [GeneratedRegex(@"^(?<track>net\d+)/(?<variant>[A-Za-z0-9-]+)$")]
    private static partial Regex EntryRegex();
}
