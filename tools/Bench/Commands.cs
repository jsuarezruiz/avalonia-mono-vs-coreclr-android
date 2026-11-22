using System.Globalization;
using Bench;

namespace Bench.Cli;

internal static class Commands
{
    private const string Usage = """
        bench: build the benchmark apps, compare the cold start of Android apps, with statistics and a video

        Usage:
          bench build   [variant...]    [options]  build this repo's apps into artifacts/<track>/<scenario>/<variant>
          bench compare <left> <right>  [options]  measure, record, report and render, end to end
          bench run     [<app>...]      [options]  interleaved cold starts of any number of apps; statistics and report
          bench record  <left> <right>  [options]  record clips of launches and render the video
          bench render  <session-dir>   [options]  rebuild the report and video from a recorded session
          bench frames  [<app>...]      [options]  frame pacing of the sparkle app (idle and touch, four effects)
          bench stats   <csv|dir>...    [options]  summarise startup and frame-pacing CSVs as Markdown
          bench devices                            list attached devices

        An <app> is an .apk file, or a built entry of this repo such as net10/mono-aot-profiled. `run` and `frames`
        with no apps use every built entry of the scenario. Apps are installed side by side, so their package ids
        must differ.

        Options:
          --runs N          measured runs per app (default 30; 5 for frames)
          --warmup N        discarded launches per app first (default 1)
          --takes N         recorded launches per app for the clip (default 7)
          --seconds N       frames: seconds recorded per effect and phase (default 5)
          --cooldown S      seconds to wait before each launch (default 3)
          --serial ID       device to use (required when several are attached)
          --out DIR         session folder (default results/bench/<time>_<device>)
          --scenario NAME   dashboard or sparkle: which apps entries mean (default dashboard; sparkle for frames);
                            for `build`, which to build (default all)
          --track NAME      build: net10, net11 or all (default all)
          --rid RID         build: android-arm64 (default) or android-x64 (also: environment variable RID)
          --left-label T    name on the video, "Name" or "Name|second line" (also --right-label)
          --eyebrow T       small heading above the title (default "ANDROID  ·  COLD START")
          --note T          extra text for the footer, e.g. "Avalonia 12.1.6"
          --device-label T  how the phone is named on the video (default: the name the phone reports, e.g. "POCO F5")
          --stats CSV|DIR   record/render: statistics from a full session (a runs.csv or a folder)
          --no-video        skip the video
          --show-home       do not blur the phone's own home screen in the first frames of the video
          --drop-caches     try to drop the page cache before each launch (needs root)
          --strict          exit with code 2 when the result is not ready to publish
          --baseline E      stats: entry the deltas are relative to (default net10/mono-aot-profiled)
          --include-warm    stats: also count non-cold launches
          --adb PATH        adb executable (default: found automatically)

        Needs adb and ffmpeg (video only). `build` needs the .NET SDKs of the tracks. A session folder holds runs.csv
        (or frames.csv), report.md and, for compare, takes.csv, compare.json and compare.mp4.
        """;

    public static async Task<int> RunAsync(string[] argv, ConsoleLog log, CancellationToken ct)
    {
        if (argv.Length == 0 || argv[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine(Usage);
            return argv.Length == 0 ? 1 : 0;
        }

        var command = argv[0];
        var args = Args.Parse(argv.Skip(1));
        if (args.Flag("--help")) { Console.WriteLine(Usage); return 0; }

        return command switch
        {
            "build" => await BuildAsync(args, log, ct),
            "compare" => await MeasureAndRecordAsync(args, log, measure: true, record: true, ct),
            "run" when args.Positional.Count == 2 => await MeasureAndRecordAsync(args, log, measure: true, record: false, ct),
            "run" => await RunManyAsync(args, log, ct),
            "record" => await MeasureAndRecordAsync(args, log, measure: false, record: true, ct),
            "frames" => await FramesAsync(args, log, ct),
            "render" => await RenderAsync(args, log, ct),
            "stats" => Stats(args),
            "devices" => await DevicesAsync(args, ct),
            _ => throw new BenchException($"Unknown command '{command}'. Run `bench --help`."),
        };
    }

    // ---- compare / run / record -----------------------------------------------------------------------------------
    private static async Task<int> MeasureAndRecordAsync(Args a, ConsoleLog log, bool measure, bool record, CancellationToken ct)
    {
        if (a.Positional.Count != 2) throw new BenchException("Give exactly two apps: <left> <right>. Run `bench --help`.");
        var scenario = a.Get("--scenario") ?? "dashboard";
        var left = CandidateResolver.Resolve(a.Positional[0], a.Get("--left-label"), scenario);
        var right = CandidateResolver.Resolve(a.Positional[1], a.Get("--right-label"), scenario);
        CandidateResolver.EnsureDistinct(left, right);

        var video = record && !a.Flag("--no-video");
        if (video || record) await Ffmpeg.EnsureAvailableAsync(ct);

        var adb = await Adb.ConnectAsync(a.Get("--serial"), a.Get("--adb"), ct);
        var device = await adb.DescribeDeviceAsync(ct);
        if (a.Get("--device-label") is { Length: > 0 } label) device = device with { Name = label };
        log.Write($"device: {device.DisplayName} [{device.Model}] (API {device.AndroidApi}, {device.Abi}, {(device.IsEmulator ? "emulator" : "physical")})");
        await PrepareRealDeviceAsync(adb, device, log, ct);
        log.Write($"left:   {left.Label} {left.Sub}  ({left.PackageId})");
        log.Write($"right:  {right.Label} {right.Sub}  ({right.PackageId})");
        if (device.IsEmulator) log.Write("warning: emulator timings are fine for trying the tool, not for publishing a comparison.");
        if (HostInfo.LoadAverage() is { } load && load > HostInfo.Cpus)
            log.Write($"warning: the host is busy (load {load:0} on {HostInfo.Cpus} CPUs); every timing will be unreliable.");

        var options = new CompareOptions
        {
            Runs = a.Int("--runs", 30), Warmup = a.Int("--warmup", 1), Takes = a.Int("--takes", 7),
            Cooldown = TimeSpan.FromSeconds(a.Seconds("--cooldown", 3)), DropCaches = a.Flag("--drop-caches"),
            Video = video, Eyebrow = a.Get("--eyebrow") ?? "ANDROID  ·  COLD START", Note = a.Get("--note") ?? "",
        };
        if (options.Runs < 1 && measure) throw new BenchException("--runs must be at least 1.");

        var dir = a.Get("--out") ?? DefaultOutDir(device);
        Directory.CreateDirectory(dir);
        SessionFiles.WriteInfo(dir, new SessionInfo(device, Info(left), Info(right), options.Eyebrow, options.Note, DateTimeOffset.Now));
        log.Write($"session: {dir}");

        var runner = new ColdStartRunner(adb, device, options.ToRunnerOptions(), log.Write);
        await runner.PrepareAsync([left, right], ct);

        var pipeline = new ComparePipeline(log.Write);
        IReadOnlyList<RunRow>? stats = null;
        if (measure) stats = await pipeline.MeasureAsync(runner, [left, right], dir, ct);
        if (record) await pipeline.RecordAsync(runner, left, right, options.Takes, dir, ct);
        stats ??= ReadStatsOption(a);

        var result = await pipeline.BuildAsync(dir, stats, video, ct, blurHome: !a.Flag("--show-home"));
        log.Done();
        return Print(result, a.Flag("--strict"));
    }

    // ---- build ----------------------------------------------------------------------------------------------------
    private static async Task<int> BuildAsync(Args a, ConsoleLog log, CancellationToken ct)
    {
        var root = CandidateResolver.FindRepoRoot()
                   ?? throw new BenchException("`bench build` builds this repository's apps; run it from inside the repository.");

        static string[] Pick(string? value, string[] all, string what)
        {
            if (value is null or "all") return all;
            return all.Contains(value) ? [value] : throw new BenchException($"Unknown {what} '{value}' (valid: {string.Join(", ", all)}, all).");
        }

        var tracks = Pick(a.Get("--track"), RepoLayout.Tracks, "track");
        var scenarios = Pick(a.Get("--scenario"), RepoLayout.Scenarios, "scenario");
        var rid = a.Get("--rid") ?? Environment.GetEnvironmentVariable("RID") ?? "android-arm64";
        foreach (var v in a.Positional)
            if (!RepoLayout.AllVariants.Contains(v)) throw new BenchException($"Unknown variant '{v}' (valid: {string.Join(", ", RepoLayout.AllVariants)}).");

        var built = await new ApkBuilder(log.Write).BuildAsync(new BuildOptions(root, tracks, scenarios, a.Positional, rid), ct);
        log.Done();
        Console.WriteLine($"\nbuilt {built.Count} APK(s). Next: bench compare net10/mono-aot-profiled net11/coreclr");
        return 0;
    }

    // ---- run for any number of apps -------------------------------------------------------------------------------
    private static async Task<int> RunManyAsync(Args a, ConsoleLog log, CancellationToken ct)
    {
        var scenario = a.Get("--scenario") ?? "dashboard";
        var apps = a.Positional.Count == 0
            ? CandidateResolver.ResolveBuilt(scenario)
            : a.Positional.Select(p => CandidateResolver.Resolve(p, null, scenario)).ToList();
        CandidateResolver.EnsureAllDistinct(apps);

        var (adb, device) = await ConnectAsync(a, log, ct);
        var options = new CompareOptions
        {
            Runs = a.Int("--runs", 30), Warmup = a.Int("--warmup", 1), Cooldown = TimeSpan.FromSeconds(a.Seconds("--cooldown", 3)),
            DropCaches = a.Flag("--drop-caches"),
        };
        if (options.Runs < 1) throw new BenchException("--runs must be at least 1.");

        var dir = a.Get("--out") ?? DefaultOutDir(device);
        Directory.CreateDirectory(dir);
        log.Write($"apps:    {string.Join(", ", apps.Select(x => x.Entry))}");
        log.Write($"session: {dir}");

        var runner = new ColdStartRunner(adb, device, options.ToRunnerOptions(), log.Write);
        await runner.PrepareAsync(apps, ct);
        await new ComparePipeline(log.Write).MeasureAsync(runner, apps, dir, ct);
        log.Done();
        return Summarise(dir, SessionFiles.RunsFile, a);
    }

    // ---- frames ---------------------------------------------------------------------------------------------------
    private static async Task<int> FramesAsync(Args a, ConsoleLog log, CancellationToken ct)
    {
        var scenario = a.Get("--scenario") ?? "sparkle";
        var apps = a.Positional.Count == 0
            ? CandidateResolver.ResolveBuilt(scenario)
            : a.Positional.Select(p => CandidateResolver.Resolve(p, null, scenario)).ToList();
        CandidateResolver.EnsureAllDistinct(apps);

        var (adb, device) = await ConnectAsync(a, log, ct);
        var options = new FrameOptions
        {
            Runs = a.Int("--runs", 5), PhaseSeconds = a.Int("--seconds", 5), Cooldown = TimeSpan.FromSeconds(a.Seconds("--cooldown", 3)),
        };
        if (options.Runs < 1 || options.PhaseSeconds < 1) throw new BenchException("--runs and --seconds must be at least 1.");

        var dir = a.Get("--out") ?? DefaultOutDir(device);
        Directory.CreateDirectory(dir);
        log.Write($"apps:    {string.Join(", ", apps.Select(x => x.Entry))}");
        log.Write($"runs:    {options.Runs}, {options.PhaseSeconds}s per phase (about {4 * (2 * options.PhaseSeconds + 2)}s per run)");
        log.Write($"session: {dir}");
        if (device.IsEmulator) log.Write("warning: emulator frame timings are fine for checking the harness, not for publishing comparisons.");

        var rows = await new FrameRunner(adb, device, options, log.Write).RunAsync(apps, ct);
        FrameCsv.Write(Path.Combine(dir, "frames.csv"), rows);
        log.Done();
        return Summarise(dir, "frames.csv", a);
    }

    /// <summary>Writes report.md next to a session's CSV and prints it.</summary>
    private static int Summarise(string dir, string csv, Args a)
    {
        var markdown = StatsReport.Render([Path.Combine(dir, csv)], a.Get("--baseline") ?? StatsReport.DefaultBaseline);
        File.WriteAllText(Path.Combine(dir, "report.md"), markdown);
        Console.WriteLine();
        Console.Write(markdown);
        Console.WriteLine($"csv:    {Path.Combine(dir, csv)}");
        return 0;
    }

    /// <summary>A phone must be awake (and unlocked, which only its owner can do) to launch and record apps.</summary>
    private static async Task PrepareRealDeviceAsync(Adb adb, DeviceInfo device, ConsoleLog log, CancellationToken ct)
    {
        if (device.IsEmulator) return;

        // Waking the screen with a key press is a courtesy. Some phones refuse adb input; then the screen simply has to
        // be on already, which is checked below.
        var wake = await adb.WakeAsync(ct);
        var inputBlocked = AdbParsers.InputWasRefused(wake.StdOut + wake.StdErr);

        if (!await adb.StaysAwakeWhilePluggedAsync(ct))
            throw new BenchException(
                "\"Stay awake\" is off, so the screen would switch itself off during the run and spoil it.\n" +
                "Turn it on in Developer options (\"Permanecer activo\" in Spanish). On phones that allow it, this does the same: " +
                $"adb -s {device.Serial} shell svc power stayon usb (Xiaomi ignores it, so use the switch).");

        await Task.Delay(500, ct);
        if (!await adb.IsAwakeAsync(ct))
            throw new BenchException("The phone's screen is off. Turn it on and unlock it.");
        if (inputBlocked)
            log.Write("note: this phone blocks adb input, so the screen cannot be woken remotely; it must stay on (Stay awake).");
        log.Write("phone: keep it unlocked and the screen on while this runs, with airplane mode and a fixed brightness.");
    }

    private static async Task<(Adb Adb, DeviceInfo Device)> ConnectAsync(Args a, ConsoleLog log, CancellationToken ct)
    {
        var adb = await Adb.ConnectAsync(a.Get("--serial"), a.Get("--adb"), ct);
        var device = await adb.DescribeDeviceAsync(ct);
        if (a.Get("--device-label") is { Length: > 0 } label) device = device with { Name = label };
        log.Write($"device: {device.DisplayName} [{device.Model}] (API {device.AndroidApi}, {device.Abi}, {(device.IsEmulator ? "emulator" : "physical")})");
        await PrepareRealDeviceAsync(adb, device, log, ct);
        if (HostInfo.LoadAverage() is { } load && load > HostInfo.Cpus)
            log.Write($"warning: the host is busy (load {load:0} on {HostInfo.Cpus} CPUs); every timing will be unreliable.");
        return (adb, device);
    }

    // ---- render ---------------------------------------------------------------------------------------------------
    private static async Task<int> RenderAsync(Args a, ConsoleLog log, CancellationToken ct)
    {
        if (a.Positional.Count != 1) throw new BenchException("Give the session folder: bench render <session-dir>.");
        var dir = a.Positional[0];
        if (!Directory.Exists(dir)) throw new BenchException($"{dir} is not a folder.");
        if (!a.Flag("--no-video")) await Ffmpeg.EnsureAvailableAsync(ct);

        var eyebrow = a.Get("--eyebrow");
        var note = a.Get("--note");
        var deviceLabel = a.Get("--device-label");
        if (eyebrow is not null || note is not null || deviceLabel is not null)
        {
            var session = SessionFiles.ReadInfo(dir);
            SessionFiles.WriteInfo(dir, session with
            {
                Eyebrow = eyebrow ?? session.Eyebrow, Note = note ?? session.Note,
                Device = deviceLabel is null ? session.Device : session.Device with { Name = deviceLabel },
            });
        }

        var result = await new ComparePipeline(log.Write).BuildAsync(dir, ReadStatsOption(a), !a.Flag("--no-video"), ct,
            blurHome: !a.Flag("--show-home"));
        log.Done();
        return Print(result, a.Flag("--strict"));
    }

    // ---- stats ----------------------------------------------------------------------------------------------------
    private static int Stats(Args a)
    {
        var paths = a.Positional.Count > 0 ? a.Positional.ToList() : DefaultStatsPaths();
        Console.Write(StatsReport.Render(paths, a.Get("--baseline") ?? StatsReport.DefaultBaseline, a.Flag("--include-warm")));
        return 0;
    }

    private static List<string> DefaultStatsPaths()
    {
        var results = Path.Combine(CandidateResolver.FindRepoRoot() ?? ".", "results");
        var paths = new List<string>();
        var raw = Path.Combine(results, "raw");
        if (Directory.Exists(raw) && Directory.GetFiles(raw, "*.csv").Length > 0) paths.Add(raw);
        var sessions = Path.Combine(results, "bench");
        if (Directory.Exists(sessions))
            foreach (var dir in Directory.GetDirectories(sessions).Order(StringComparer.Ordinal))
                paths.AddRange(new[] { SessionFiles.RunsFile, "frames.csv" }.Select(f => Path.Combine(dir, f)).Where(File.Exists));
        return paths.Count > 0 ? paths : [raw];
    }

    // ---- devices --------------------------------------------------------------------------------------------------
    private static async Task<int> DevicesAsync(Args a, CancellationToken ct)
    {
        var adbPath = Adb.Locate(a.Get("--adb"));
        var listing = await ProcessRunner.RunAsync(adbPath, ["devices", "-l"], TimeSpan.FromSeconds(20), ct);
        var devices = AdbParsers.ParseDevices(listing.StdOut);
        if (devices.Count == 0) { Console.WriteLine("No devices attached."); return 1; }
        foreach (var d in devices)
        {
            var extra = "";
            if (d.State == "device" && d.Serial.StartsWith("emulator-", StringComparison.Ordinal))
            {
                var r = await ProcessRunner.RunAsync(adbPath, ["-s", d.Serial, "emu", "avd", "name"], TimeSpan.FromSeconds(5), ct);
                var name = r.StdOut.Split('\n').FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(name)) extra = $"  emulator, AVD {name}";
            }

            Console.WriteLine($"{d.Serial,-22} {d.State,-13} {d.Model}{extra}");
        }

        return 0;
    }

    // ---- shared ---------------------------------------------------------------------------------------------------
    private static CandidateInfo Info(Candidate c) => new(c.Entry, c.Label, c.Sub, c.ApkPath, c.PackageId, c.ApkBytes);

    private static string DefaultOutDir(DeviceInfo device)
    {
        var root = CandidateResolver.FindRepoRoot();
        var baseDir = root is null ? Path.Combine(Directory.GetCurrentDirectory(), "bench-results") : Path.Combine(root, "results", "bench");
        return Path.Combine(baseDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{device.Slug}");
    }

    /// <summary>--stats: a runs.csv, a startup CSV, or a folder of them (a session folder uses its runs.csv).</summary>
    private static IReadOnlyList<RunRow>? ReadStatsOption(Args a)
    {
        if (a.Get("--stats") is not { } path) return null;
        var files = new List<string>();
        if (Directory.Exists(path))
        {
            var runs = Path.Combine(path, SessionFiles.RunsFile);
            files.AddRange(File.Exists(runs) ? [runs] : Directory.GetFiles(path, "*.csv"));
        }
        else if (File.Exists(path)) files.Add(path);
        else throw new BenchException($"--stats {path} does not exist.");

        var rows = files.SelectMany(StartupCsv.Read).ToList();
        if (rows.Count == 0) throw new BenchException($"No startup rows found in {path}.");
        return rows;
    }

    private static int Print(CompareResult r, bool strict)
    {
        Console.WriteLine();
        if (r is { Claim: { } claim, Left: { } left, Right: { } right })
        {
            var session = SessionFiles.ReadInfo(r.Directory);
            var (win, lose) = claim.Winner == Side.Left ? (session.Left.Name, session.Right.Name) : (session.Right.Name, session.Left.Name);
            Console.WriteLine(claim.Tie
                ? "no clear difference (the 95% interval of the difference reaches zero)"
                : $"{win} took {Format.Fixed(claim.Percent, 1)}% less time than {lose} (median, 95% CI {Format.Fixed(claim.CiLowPercent, 1)}% to {Format.Fixed(claim.CiHighPercent, 1)}%)");
            Console.WriteLine($"  {session.Left.Name}: {Format.Fixed(left.MedianMs)} ms [{Format.Fixed(left.CiLowMs)}, {Format.Fixed(left.CiHighMs)}] n={left.N}    " +
                              $"{session.Right.Name}: {Format.Fixed(right.MedianMs)} ms [{Format.Fixed(right.CiLowMs)}, {Format.Fixed(right.CiHighMs)}] n={right.N}    ({left.Metric.Describe()})");
        }

        if (r.VideoPath is not null) Console.WriteLine($"video:  {r.VideoPath}");
        if (r.ReportPath is not null) Console.WriteLine($"report: {r.ReportPath}");
        if (r.Problems.Count > 0)
        {
            Console.Error.WriteLine("\nNOT READY TO PUBLISH:");
            foreach (var p in r.Problems) Console.Error.WriteLine($"  - {p}");
        }
        else Console.WriteLine("\nchecks passed: real device, quiet host, enough runs, the metric agrees with TotalTime.");

        return strict && r.Problems.Count > 0 ? 2 : 0;
    }
}
