using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Bench;

public sealed record BuildOptions(
    string Root, IReadOnlyList<string> Tracks, IReadOnlyList<string> Scenarios, IReadOnlyList<string> Variants,
    string Rid = "android-arm64");

public sealed record BuiltApk(string Track, string Scenario, string Variant, string ApkPath, long Bytes, string Runtime);

/// <summary>
/// Builds the benchmark apps into artifacts/&lt;track&gt;/&lt;scenario&gt;/&lt;variant&gt;/. Each track builds with its own SDK
/// (chosen by the global.json in eng/tracks/&lt;track&gt;, or the isolated .dotnet11 install when there is one), always
/// from a clean state, and every APK is opened afterwards to prove it contains the runtime its variant claims, so a
/// build-property typo can never silently benchmark Mono against Mono.
/// </summary>
public sealed partial class ApkBuilder(Action<string> log)
{
    public async Task<IReadOnlyList<BuiltApk>> BuildAsync(BuildOptions o, CancellationToken ct = default)
    {
        var built = new List<BuiltApk>();
        foreach (var track in o.Tracks)
        {
            var supported = RepoLayout.VariantsFor(track);
            var variants = o.Variants.Count > 0 ? o.Variants : supported;
            foreach (var scenario in o.Scenarios)
            {
                var project = RepoLayout.ProjectFor(scenario);
                foreach (var variant in variants)
                {
                    if (!supported.Contains(variant))
                    {
                        log($"==> Skipping {track} / {scenario} / {variant} (not supported on {track})");
                        continue;
                    }

                    log($"==> Building {track} / {scenario} / {variant} ({o.Rid})");
                    built.Add(await BuildOneAsync(o, track, scenario, variant, project, ct));
                }
            }
        }

        return built;
    }

    private async Task<BuiltApk> BuildOneAsync(BuildOptions o, string track, string scenario, string variant, string project, CancellationToken ct)
    {
        var projectPath = Path.Combine(o.Root, project);
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var output = RepoLayout.ArtifactDir(o.Root, track, scenario, variant);

        // Android's incremental state is not variant-aware, so always start clean.
        foreach (var dir in new[] { Path.Combine(projectDir, "obj"), Path.Combine(projectDir, "bin"), output })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        var (dotnet, environment) = ToolchainFor(o.Root, track);
        var tail = new Queue<string>();
        var exit = await ProcessRunner.RunStreamingAsync(dotnet,
            ["publish", projectPath, "-c", "Release", "-r", o.Rid, $"-p:Variant={variant}", $"-p:BenchTrack={track}", "-o", output, "--nologo", "-v:minimal"],
            Path.Combine(o.Root, "eng", "tracks", track),     // dotnet picks its SDK from the working directory
            environment,
            line =>
            {
                log("    " + line);
                tail.Enqueue(line);
                if (tail.Count > 25) tail.Dequeue();
            }, ct);
        if (exit != 0)
            throw new BenchException($"dotnet publish failed for {track} / {scenario} / {variant} (exit code {exit}). Last lines:\n{string.Join('\n', tail)}");

        var apk = RepoLayout.FindSignedApk(output)
                  ?? throw new BenchException($"No signed APK found in {output}.");
        var runtime = VerifyRuntime(apk, variant);
        var bytes = new FileInfo(apk).Length;
        log($"    {apk}  ({bytes} bytes, runtime: {runtime})");
        return new BuiltApk(track, scenario, variant, apk, bytes, runtime);
    }

    /// <summary>The dotnet to run and the environment for it. net11 uses the isolated install when the repo has one.</summary>
    public static (string Dotnet, IReadOnlyDictionary<string, string?>? Environment) ToolchainFor(string root, string track)
    {
        var isolated = Path.Combine(root, ".dotnet11");
        var exe = Path.Combine(isolated, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (track != "net11" || !File.Exists(exe)) return ("dotnet", null);

        return (exe, new Dictionary<string, string?>
        {
            ["DOTNET_ROOT"] = isolated,
            ["DOTNET_CLI_HOME"] = Path.Combine(isolated, "home"),
            ["PATH"] = isolated + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        });
    }

    /// <summary>The runtime libraries an APK ships, e.g. ["libcoreclr.so"].</summary>
    public static IReadOnlyList<string> RuntimeLibraries(string apkPath)
    {
        using var zip = ZipFile.OpenRead(apkPath);
        return RuntimeLibraries(zip.Entries.Select(e => e.FullName));
    }

    public static IReadOnlyList<string> RuntimeLibraries(IEnumerable<string> entryNames) =>
        entryNames.Select(n => RuntimeLibRegex().Match(n)).Where(m => m.Success)
            .Select(m => m.Groups["lib"].Value + ".so").Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Throws unless the APK contains exactly the runtime the variant claims; returns that library's name.</summary>
    public static string VerifyRuntime(string apkPath, string variant)
    {
        var want = variant == "coreclr" ? "libcoreclr.so" : "libmonosgen-2.0.so";
        var found = RuntimeLibraries(apkPath);
        if (found.Count != 1 || found[0] != want)
            throw new BenchException($"Variant '{variant}' should contain only {want} but the APK contains: {(found.Count == 0 ? "<none>" : string.Join(' ', found))}");
        return want;
    }

    [GeneratedRegex(@"^lib/[^/]+/(?<lib>libmonosgen-2\.0|libcoreclr)\.so$")]
    private static partial Regex RuntimeLibRegex();
}
