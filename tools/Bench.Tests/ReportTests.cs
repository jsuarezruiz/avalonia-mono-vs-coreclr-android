namespace Bench.Tests;

public class StatsReportTests
{
    private static string FixturePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bench-fixture-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, Fixtures.Text("startup-sample.csv"));
        return path;
    }

    [Fact]
    public void Summarises_a_real_session()
    {
        var path = FixturePath();
        try
        {
            var md = StatsReport.Render([path]);
            Assert.Contains("Sources: 1 file(s), 60 startup rows, 0 frame rows.", md);
            Assert.Contains("> **Emulator.**", md);
            Assert.Contains("| net10/mono-aot-profiled *(baseline)* | 30 | 964 | 1539 | 924 | 1485 | 942 | 1507 | - | 130 | 15.8 |", md);
            // 442.5 prints as 442 (a true tie goes to the even digit), like printf.
            Assert.Matches(@"\| net11/coreclr \| 30 \| 480 \| 790 \| 426 \| 719 \| 442 \| 744 \| -53\.0% \[-55\.\d%, -50\.6%\] \| 158 \| 21\.6 \|", md);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Frame_pacing_files_are_recognised_and_summarised()
    {
        const string header = "track,variant,run,device,android_api,abi,is_emulator,refresh_hz,effect,phase,n,mean_ms,p50_ms,p95_ms,p99_ms,max_ms,slow,gc0,gc1,gc2,alloc_mb,host_load,host_cpus";
        Assert.True(FrameCsv.IsFrameFile(header));
        var rows = FrameCsv.Parse([header,
            "net10,mono-aot-profiled,1,Pixel,34,arm64-v8a,0,60,waterfall,idle,300,16.7,16.6,18.0,20.0,25.0,3,1,0,0,12,3,8",
            "net11,coreclr,1,Pixel,34,arm64-v8a,0,60,waterfall,idle,300,16.7,16.6,19.8,22.0,30.0,6,1,0,0,12,3,8"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("net11/coreclr", rows[1].Entry);

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, [header,
                "net10,mono-aot-profiled,1,Pixel,34,arm64-v8a,0,60,waterfall,idle,300,16.7,16.6,18.0,20.0,25.0,3,1,0,0,12,3,8",
                "net11,coreclr,1,Pixel,34,arm64-v8a,0,60,waterfall,idle,300,16.7,16.6,19.8,22.0,30.0,6,1,0,0,12,3,8"]);
            var md = StatsReport.Render([path]);
            Assert.Contains("# Frame-pacing results", md);
            Assert.Contains("| waterfall | idle | net11/coreclr | 1 | 59.9 | 16.6 | 19.8 | 22.0 | 30.0 | 2.0 | 1 | 12 | +10.0% |", md);
        }
        finally { File.Delete(path); }
    }
}

public class SessionTests
{
    [Fact]
    public void Repo_entries_get_readable_names()
    {
        Assert.Equal(("Mono", "AOT (profiled) · .NET 10"), RepoLabels.For("net10/mono-aot-profiled"));
        Assert.Equal(("CoreCLR", "ReadyToRun · .NET 11"), RepoLabels.For("net11/coreclr"));
        Assert.Equal(("my-app", ""), RepoLabels.For("my-app"));
    }

    [Fact]
    public void Takes_round_trip_with_clip_paths_relative_to_the_session_folder()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var clip = Path.Combine(dir, "takes", "left-03.mp4");
            SessionFiles.WriteTakes(dir, [new Take("left", "a/b", 3, clip, 950, 1311, 969, 0, 4.5)]);

            var csv = File.ReadAllText(Path.Combine(dir, "takes.csv"));
            Assert.Contains(",takes/left-03.mp4,", csv);                   // relative, so the folder can be moved
            Assert.DoesNotContain(dir, csv);

            var take = Assert.Single(SessionFiles.ReadTakes(dir));
            Assert.Equal((3, 950.0, 1311.0, 969.0), (take.Number, take.AppFullyDrawnMs, take.SystemFullyDrawnMs, take.TotalTimeMs));
            Assert.Equal(clip, take.ClipPath);

            var moved = Directory.CreateTempSubdirectory().FullName;
            File.Copy(Path.Combine(dir, "takes.csv"), Path.Combine(moved, "takes.csv"));
            Assert.Equal(Path.Combine(moved, "takes", "left-03.mp4"), Assert.Single(SessionFiles.ReadTakes(moved)).ClipPath);
            Directory.Delete(moved, recursive: true);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_apps_with_the_same_package_cannot_be_compared()
    {
        Candidate C(string label, string pkg) => new() { Label = label, ApkPath = "x.apk", PackageId = pkg };
        Assert.Throws<BenchException>(() => CandidateResolver.EnsureDistinct(C("a", "com.x"), C("b", "com.x")));
        CandidateResolver.EnsureDistinct(C("a", "com.x"), C("b", "com.y"));
    }
}

public class ClaimTests
{
    private static EntrySummary S(string entry, params double[] values) =>
        Summaries.Summarize(entry, values.Select((v, i) => new RunRow { Variant = entry, Run = i, FullyDrawnMs = v, TotalTimeMs = v + 20, HostCpus = 16, HostLoad = 3 }).ToList(), Metric.AppFullyDrawn);

    [Fact]
    public void Names_the_faster_app_and_sizes_the_difference_with_an_interval()
    {
        var slow = S("slow", Enumerable.Range(0, 30).Select(i => 940.0 + i % 7).ToArray());
        var fast = S("fast", Enumerable.Range(0, 30).Select(i => 440.0 + i % 5).ToArray());
        var claim = Summaries.Compare(slow, fast);
        Assert.Equal(Side.Right, claim.Winner);
        Assert.False(claim.Tie);
        Assert.InRange(claim.Percent, 52, 54);
        Assert.True(claim.CiLowPercent < claim.Percent && claim.Percent < claim.CiHighPercent);
    }

    [Fact]
    public void Overlapping_apps_are_a_tie()
    {
        var a = S("a", Enumerable.Range(0, 30).Select(i => 500.0 + (i * 37 % 100)).ToArray());
        var b = S("b", Enumerable.Range(0, 30).Select(i => 500.0 + (i * 53 % 100)).ToArray());
        Assert.True(Summaries.Compare(a, b).Tie);
    }

    [Fact]
    public void Publish_checks_flag_an_emulator_few_runs_and_a_busy_host()
    {
        var emulator = new DeviceInfo("e", "Pixel", "34", "arm64-v8a", IsEmulator: true);
        var few = S("few", 100, 101, 102);
        var busy = Summaries.Summarize("busy", Enumerable.Range(0, 30).Select(i => new RunRow { Variant = "busy", FullyDrawnMs = 900 + i, HostLoad = 40, HostCpus = 16 }).ToList(), Metric.AppFullyDrawn);
        var claim = Summaries.Compare(few, busy);
        var problems = PublishChecks.Evaluate(emulator, few, busy, claim, clipOrderMatches: true);
        Assert.Contains(problems, p => p.Contains("emulator"));
        Assert.Contains(problems, p => p.Contains("few: 3 runs"));
        Assert.Contains(problems, p => p.Contains("busy host"));

        var real = new DeviceInfo("r", "Pixel", "34", "arm64-v8a", IsEmulator: false);
        var good1 = S("g1", Enumerable.Range(0, 30).Select(i => 900.0 + i).ToArray());
        var good2 = S("g2", Enumerable.Range(0, 30).Select(i => 400.0 + i).ToArray());
        Assert.Empty(PublishChecks.Evaluate(real, good1, good2, Summaries.Compare(good1, good2), true));
    }
}

public class DeviceTests
{
    [Fact]
    public void Display_name_prefers_the_marketing_name_then_the_emulator_label_then_the_model()
    {
        Assert.Equal("POCO F5", new DeviceInfo("s", "23049PCD8G", "14", "arm64-v8a", false, "POCO F5").DisplayName);
        Assert.Equal("23049PCD8G", new DeviceInfo("s", "23049PCD8G", "14", "arm64-v8a", false).DisplayName);
        Assert.Equal("Android emulator", new DeviceInfo("s", "sdk_gphone64_arm64", "34", "arm64-v8a", true).DisplayName);
        Assert.Equal("My label", new DeviceInfo("s", "sdk_gphone64_arm64", "34", "arm64-v8a", true, "My label").DisplayName);
    }

    [Theory]
    [InlineData("  mWakefulness=Awake\n  mWakefulnessChanging=false", "Awake")]
    [InlineData("mWakefulness=Asleep", "Asleep")]
    [InlineData("mWakefulness=Dozing", "Dozing")]
    [InlineData("nothing useful here", null)]
    public void Reads_the_screen_state_from_dumpsys_power(string text, string? expected) =>
        Assert.Equal(expected, AdbParsers.ParseWakefulness(text));

    [Fact]
    public void A_session_keeps_the_phone_name_but_never_its_serial_number()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var device = new DeviceInfo("R5CT1234567", "23049PCD8G", "14", "arm64-v8a", false, "POCO F5");
            var app = new CandidateInfo("a", "A", "", "a.apk", "com.a", 1);
            SessionFiles.WriteInfo(dir, new SessionInfo(device, app, app with { Entry = "b" }, "EYEBROW", "note", DateTimeOffset.Now));

            var text = File.ReadAllText(Path.Combine(dir, "session.json"));
            Assert.DoesNotContain("R5CT1234567", text);
            var read = SessionFiles.ReadInfo(dir).Device;
            Assert.Equal(("POCO F5", "23049PCD8G", ""), (read.Name, read.Model, read.Serial));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
