namespace Bench;

public sealed record RunnerOptions
{
    public int Runs { get; init; } = 30;
    public int Warmup { get; init; } = 1;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(20);
    public bool DropCaches { get; init; }
}

/// <summary>Everything one cold start reported.</summary>
public sealed record LaunchMeasurement(
    AmStartResult Start, IReadOnlyDictionary<string, double> Phases, double? SystemFullyDrawnMs,
    double? PssKb, int NativeLibTimeouts, double? HostLoad);

/// <summary>
/// Installs the apps side by side and measures cold starts, interleaved in a rotating order so slow drift
/// (thermals, background work) lands on every app alike. One launch is: force-stop, clear the log, cool down,
/// <c>am start -W</c>, wait until the app says it is drawn, then read memory.
/// </summary>
public sealed class ColdStartRunner(Adb adb, DeviceInfo device, RunnerOptions options, Action<string> log)
{
    public Adb Adb => adb;
    public DeviceInfo Device => device;
    public RunnerOptions Options => options;

    private static readonly string[] LogFilters = ["BENCH:I", "ActivityTaskManager:I", "monodroid-assembly:W"];

    public Task PrepareAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct = default) =>
        DeviceSetup.PrepareAsync(adb, candidates, log, ct);

    /// <summary>One cold start. <paramref name="beforeLaunch"/> and <paramref name="afterSettled"/> let a recorder wrap it.</summary>
    public async Task<LaunchMeasurement?> LaunchAsync(
        Candidate c, bool pressHome = false, Func<Task>? beforeLaunch = null, Func<Task>? afterSettled = null,
        CancellationToken ct = default)
    {
        var component = c.Component ?? throw new InvalidOperationException("Call PrepareAsync first.");
        await adb.ForceStopAsync(c.PackageId, ct);
        if (pressHome) await adb.HomeAsync(ct);
        if (options.DropCaches) await adb.ShellAsync("sync; echo 3 > /proc/sys/vm/drop_caches", ct: ct);
        await adb.ClearLogcatAsync(ct);
        await Task.Delay(options.Cooldown, ct);

        if (beforeLaunch is not null) await beforeLaunch();
        var start = await adb.StartAndWaitAsync(component, ct);
        if (start.Status != "ok" || start.TotalTimeMs is null)
        {
            log($"warning: {c.Label} did not start cleanly (status '{start.Status}'); skipped");
            return null;
        }

        await WaitUntilDrawnAsync(c, ct);
        if (afterSettled is not null) await afterSettled();

        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var logText = await adb.LogcatAsync(LogFilters, ct);
        var pss = await adb.PssKbAsync(c.PackageId, ct);
        return new LaunchMeasurement(
            start, AdbParsers.ParseBenchPhases(logText), AdbParsers.ParseSystemFullyDrawn(logText, c.PackageId), pss,
            AdbParsers.CountResolverTimeouts(logText), HostInfo.LoadAverage());
    }

    /// <summary>Polls the log until the app reports it is drawn, by whichever signal it is known to give.</summary>
    private async Task WaitUntilDrawnAsync(Candidate c, CancellationToken ct)
    {
        if (c.Marks == MarkSource.None)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
            return;
        }

        var deadline = DateTime.UtcNow + options.Settle;
        while (DateTime.UtcNow < deadline)
        {
            var text = await adb.LogcatAsync(LogFilters, ct);
            var bench = AdbParsers.ParseBenchPhases(text).ContainsKey("fully_drawn");
            var system = AdbParsers.ParseSystemFullyDrawn(text, c.PackageId) is not null;
            var done = c.Marks switch
            {
                MarkSource.Bench => bench,
                MarkSource.System => system,
                _ => bench || system,           // first launch: whichever shows up
            };
            if (done) return;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>Works out, from the warm-up launch, which signal this app gives.</summary>
    private static void LearnMarks(Candidate c, LaunchMeasurement m) =>
        c.Marks = m.Phases.ContainsKey("fully_drawn") ? MarkSource.Bench
                  : m.SystemFullyDrawnMs is not null ? MarkSource.System
                  : MarkSource.None;

    public RunRow ToRow(Candidate c, int run, LaunchMeasurement m)
    {
        double? Phase(string name) => m.Phases.TryGetValue(name, out var v) ? v : null;
        return new RunRow
        {
            Scenario = c.Scenario, Track = c.Track, Variant = c.RowVariant, Run = run, Device = device.Model, AndroidApi = device.AndroidApi,
            Abi = device.Abi, IsEmulator = device.IsEmulator, LaunchState = m.Start.LaunchState,
            TotalTimeMs = m.Start.TotalTimeMs, WaitTimeMs = m.Start.WaitTimeMs,
            ApplicationCtorMs = Phase("application_ctor"), FrameworkInitMs = Phase("framework_init"),
            ViewLoadedMs = Phase("view_loaded"), FirstFrameMs = Phase("first_frame"), FullyDrawnMs = Phase("fully_drawn"),
            PssKb = m.PssKb, ApkBytes = c.ApkBytes, NativeLibTimeouts = m.NativeLibTimeouts, HostLoad = m.HostLoad,
            HostCpus = HostInfo.Cpus, SystemFullyDrawnMs = m.SystemFullyDrawnMs,
        };
    }

    /// <summary>
    /// Warm-up launches, discarded. The first one also teaches the runner which signal each app gives for "fully
    /// drawn", so it must happen before any measured or recorded launch.
    /// </summary>
    public async Task WarmUpAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct = default)
    {
        foreach (var c in candidates)
        {
            var count = Math.Max(options.Warmup, 1);
            for (var w = 0; w < count; w++)
            {
                log($"warm-up {c.Label} ({w + 1}/{count})");
                if (await LaunchAsync(c, ct: ct) is { } m && w == 0) LearnMarks(c, m);
            }

            log($"{c.Label}: marks from {c.Marks switch { MarkSource.Bench => "the app's BENCH probe", MarkSource.System => "Android's fully-drawn log", _ => "nothing (launch time only)" }}");
        }
    }

    /// <summary><see cref="RunnerOptions.Runs"/> interleaved rounds, rotating who goes first.</summary>
    public async Task<IReadOnlyList<RunRow>> RunAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct = default)
    {
        await WarmUpAsync(candidates, ct);

        var rows = new List<RunRow>();
        var n = candidates.Count;
        for (var round = 1; round <= options.Runs; round++)
        {
            for (var k = 0; k < n; k++)
            {
                var c = candidates[(k + round) % n];          // rotate who goes first
                log($"run {round}/{options.Runs}  {c.Label}");
                if (await LaunchAsync(c, ct: ct) is { } m) rows.Add(ToRow(c, round, m));
            }
        }

        return rows;
    }
}
