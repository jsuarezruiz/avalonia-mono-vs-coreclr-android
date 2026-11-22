using System.IO.Compression;

namespace Bench.Tests;

public class FrameParsingTests
{
    private const string Log =
        "11-14 10:13:37.091  4166  4166 I BENCH   : frames_config seconds=2 refresh_hz=60.0\n" +
        "11-14 10:13:40.001  4166  4166 I BENCH   : frames effect=waterfall phase=idle n=41 mean_ms=74.91 p50_ms=73.32 p95_ms=91.70 p99_ms=98.21 max_ms=98.21 slow=41 refresh_hz=60.0 gc0=0 gc1=1 gc2=2 alloc_mb=7.6\n" +
        "11-14 10:13:43.001  4166  4166 I BENCH   : frames effect=fireworks phase=touch n=0\n" +
        "11-14 10:13:44.001  4166  4166 I BENCH   : phase=bench_done ms=9000\n";

    [Fact]
    public void Reads_the_summaries_and_ignores_other_lines()
    {
        var samples = AdbParsers.ParseFrameLines(Log);
        Assert.Equal(2, samples.Count);

        var first = samples[0];
        Assert.Equal(("waterfall", "idle"), (first.Effect, first.Phase));
        Assert.Equal((41.0, 74.91, 73.32, 91.7, 98.21, 98.21, 41.0), (first.N, first.MeanMs, first.P50Ms, first.P95Ms, first.P99Ms, first.MaxMs, first.Slow));
        Assert.Equal(("60.0", 0.0, 1.0, 2.0, 7.6), (first.RefreshHz, first.Gc0, first.Gc1, first.Gc2, first.AllocMb));
    }

    [Fact]
    public void A_phase_without_frames_is_kept_with_empty_numbers()
    {
        var empty = AdbParsers.ParseFrameLines(Log)[1];
        Assert.Equal(("fireworks", "touch", 0.0), (empty.Effect, empty.Phase, empty.N));
        Assert.Null(empty.MeanMs);
        Assert.Null(empty.P95Ms);
    }

    [Fact]
    public void Frame_csv_keeps_the_columns_the_shell_runner_wrote_and_round_trips()
    {
        Assert.Equal("track,variant,run,device,android_api,abi,is_emulator,refresh_hz,effect,phase,n,mean_ms,p50_ms,p95_ms,p99_ms,max_ms,slow,gc0,gc1,gc2,alloc_mb,host_load,host_cpus",
            string.Join(',', FrameCsv.Columns));

        var path = Path.GetTempFileName();
        try
        {
            var row = AdbParsers.ParseFrameLines(Log)[0];
            FrameCsv.Write(path, [new FrameRow
            {
                Track = "net10", Variant = "coreclr", Run = 2, Device = "Pixel", AndroidApi = "34", Abi = "arm64-v8a", IsEmulator = true,
                RefreshHz = row.RefreshHz, Effect = row.Effect, Phase = row.Phase, N = row.N, MeanMs = row.MeanMs, P95Ms = row.P95Ms,
                Gc1 = row.Gc1, AllocMb = row.AllocMb, HostCpus = 8,
            }]);
            var read = Assert.Single(FrameCsv.Parse(File.ReadAllLines(path)));
            Assert.Equal(("net10/coreclr", 2, "waterfall", 74.91, 1.0, 8), (read.Entry, read.Run, read.Effect, read.MeanMs, read.Gc1, read.HostCpus));
        }
        finally { File.Delete(path); }
    }
}

public class RepoLayoutTests
{
    [Fact]
    public void Net11_only_has_coreclr_because_mono_is_gone()
    {
        Assert.Equal(["coreclr"], RepoLayout.VariantsFor("net11"));
        Assert.Contains("mono-aot-profiled", RepoLayout.VariantsFor("net10"));
        Assert.Throws<BenchException>(() => RepoLayout.VariantsFor("net9"));
    }

    [Fact]
    public void Splits_entries_and_rejects_anything_else()
    {
        Assert.Equal(("net10", "mono-jit"), RepoLayout.SplitEntry("net10/mono-jit"));
        Assert.Null(RepoLayout.SplitEntry("my-app"));
        Assert.Null(RepoLayout.SplitEntry("a/b/c"));
    }

    [Fact]
    public void Finds_built_entries_in_a_stable_order()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var entry in new[] { "net11/coreclr", "net10/mono-jit", "net10/coreclr" })
            {
                var (track, variant) = RepoLayout.SplitEntry(entry)!.Value;
                var dir = RepoLayout.ArtifactDir(root, track, "dashboard", variant);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"x-{variant}-Signed.apk"), "");
            }

            Directory.CreateDirectory(RepoLayout.ArtifactDir(root, "net10", "dashboard", "mono-aot-full"));   // no APK: not built

            Assert.Equal(["net10/coreclr", "net10/mono-jit", "net11/coreclr"], RepoLayout.BuiltEntries(root, "dashboard"));
            Assert.Empty(RepoLayout.BuiltEntries(root, "sparkle"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public class ApkBuilderTests
{
    private static string ApkWith(params string[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.apk");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in entries) zip.CreateEntry(e);
        return path;
    }

    [Fact]
    public void Lists_the_runtime_libraries_an_apk_ships()
    {
        Assert.Equal(["libcoreclr.so", "libmonosgen-2.0.so"], ApkBuilder.RuntimeLibraries(
            ["lib/arm64-v8a/libcoreclr.so", "lib/arm64-v8a/libmonosgen-2.0.so", "lib/arm64-v8a/libother.so", "classes.dex"]));
    }

    [Theory]
    [InlineData("mono-jit", "lib/arm64-v8a/libmonosgen-2.0.so", "libmonosgen-2.0.so")]
    [InlineData("mono-aot-full", "lib/arm64-v8a/libmonosgen-2.0.so", "libmonosgen-2.0.so")]
    [InlineData("coreclr", "lib/arm64-v8a/libcoreclr.so", "libcoreclr.so")]
    public void Accepts_an_apk_with_the_runtime_its_variant_claims(string variant, string entry, string expected)
    {
        var apk = ApkWith(entry);
        try { Assert.Equal(expected, ApkBuilder.VerifyRuntime(apk, variant)); }
        finally { File.Delete(apk); }
    }

    [Theory]
    [InlineData("coreclr", "lib/arm64-v8a/libmonosgen-2.0.so")]      // a build-property typo: Mono labelled as CoreCLR
    [InlineData("mono-jit", "lib/arm64-v8a/libcoreclr.so")]
    public void Refuses_a_mislabelled_apk(string variant, string entry)
    {
        var apk = ApkWith(entry);
        try { Assert.Contains("should contain only", Assert.Throws<BenchException>(() => ApkBuilder.VerifyRuntime(apk, variant)).Message); }
        finally { File.Delete(apk); }
    }

    [Fact]
    public void Refuses_an_apk_with_both_runtimes_or_none()
    {
        var both = ApkWith("lib/arm64-v8a/libcoreclr.so", "lib/arm64-v8a/libmonosgen-2.0.so");
        var none = ApkWith("classes.dex");
        try
        {
            Assert.Throws<BenchException>(() => ApkBuilder.VerifyRuntime(both, "coreclr"));
            Assert.Contains("<none>", Assert.Throws<BenchException>(() => ApkBuilder.VerifyRuntime(none, "coreclr")).Message);
        }
        finally { File.Delete(both); File.Delete(none); }
    }

    [Fact]
    public void Net11_uses_the_isolated_sdk_when_the_repo_has_one_and_net10_never_does()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Equal(("dotnet", null), ApkBuilder.ToolchainFor(root, "net11"));          // no .dotnet11 folder yet

            Directory.CreateDirectory(Path.Combine(root, ".dotnet11"));
            File.WriteAllText(Path.Combine(root, ".dotnet11", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "");
            var (dotnet, env) = ApkBuilder.ToolchainFor(root, "net11");
            Assert.EndsWith(Path.Combine(".dotnet11", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), dotnet);
            Assert.Equal(Path.Combine(root, ".dotnet11"), env!["DOTNET_ROOT"]);
            Assert.Equal(Path.Combine(root, ".dotnet11", "home"), env["DOTNET_CLI_HOME"]);
            Assert.StartsWith(Path.Combine(root, ".dotnet11") + Path.PathSeparator, env["PATH"]);

            Assert.Equal(("dotnet", null), ApkBuilder.ToolchainFor(root, "net10"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public class CandidateTests
{
    private static Candidate C(string entry, string scenario = "app", string pkg = "com.x") =>
        new() { Label = entry, Entry = entry, Scenario = scenario, ApkPath = "x.apk", PackageId = pkg };

    [Fact]
    public void Repo_entries_keep_the_track_and_scenario_columns_of_the_old_csv()
    {
        var runner = new ColdStartRunner(null!, new DeviceInfo("s", "Pixel", "34", "arm64-v8a", false), new RunnerOptions(), _ => { });
        var row = runner.ToRow(C("net10/mono-jit", "dashboard"), 3, new LaunchMeasurement(
            new AmStartResult("ok", "COLD", 900, 910), new Dictionary<string, double> { ["fully_drawn"] = 880 }, 950, 120_000, 0, 4.0));
        Assert.Equal(("dashboard", "net10", "mono-jit", "net10/mono-jit", 3), (row.Scenario, row.Track, row.Variant, row.Entry, row.Run));
        Assert.Equal((900.0, 880.0, 950.0), (row.TotalTimeMs, row.FullyDrawnMs, row.SystemFullyDrawnMs));

        var other = runner.ToRow(C("my app"), 1, new LaunchMeasurement(new AmStartResult("ok", "COLD", 1, 1), new Dictionary<string, double>(), null, null, 0, null));
        Assert.Equal(("app", "", "my app", "my app"), (other.Scenario, other.Track, other.Variant, other.Entry));
    }

    [Fact]
    public void Many_apps_must_have_distinct_package_ids_and_names()
    {
        CandidateResolver.EnsureAllDistinct([C("a", pkg: "com.a"), C("b", pkg: "com.b"), C("c", pkg: "com.c")]);
        Assert.Contains("package id", Assert.Throws<BenchException>(() => CandidateResolver.EnsureAllDistinct([C("a", pkg: "com.x"), C("b", pkg: "com.x")])).Message);
        Assert.Contains("name", Assert.Throws<BenchException>(() => CandidateResolver.EnsureAllDistinct([C("a", pkg: "com.a"), C("a", pkg: "com.b")])).Message);
    }
}
