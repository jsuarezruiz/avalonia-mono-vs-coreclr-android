namespace Bench;

public sealed record FrameOptions
{
    public int Runs { get; init; } = 5;
    /// <summary>Seconds recorded per effect and phase (idle, then touch).</summary>
    public int PhaseSeconds { get; init; } = 5;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The frame-pacing benchmark for the sparkle app (and any app built like it). The app drives itself: launched with
/// <c>--ei bench_seconds N</c> it visits four effects and, per effect, records N seconds with nobody touching the
/// screen and N seconds with a synthetic pointer circling it, logging one summary line per phase. Nothing is injected
/// over adb, so input latency cannot leak into the numbers. Runs are interleaved across the apps in a rotating order.
/// </summary>
public sealed class FrameRunner(Adb adb, DeviceInfo device, FrameOptions options, Action<string> log)
{
    public async Task<IReadOnlyList<FrameRow>> RunAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct = default)
    {
        await DeviceSetup.PrepareAsync(adb, candidates, log, ct);

        // One short warm-up per app absorbs first-launch extraction and caching.
        foreach (var c in candidates)
        {
            log($"warm-up {c.Label}");
            await LaunchAsync(c, run: 0, seconds: 1, ct);
        }

        var rows = new List<FrameRow>();
        var n = candidates.Count;
        for (var round = 1; round <= options.Runs; round++)
        {
            for (var k = 0; k < n; k++)
            {
                var c = candidates[(k + round) % n];             // rotate who goes first
                log($"run {round}/{options.Runs}  {c.Label}");
                rows.AddRange(await LaunchAsync(c, round, options.PhaseSeconds, ct));
            }
        }

        return rows;
    }

    /// <summary>One run: its rows, or none if the app never finished.</summary>
    private async Task<IReadOnlyList<FrameRow>> LaunchAsync(Candidate c, int run, int seconds, CancellationToken ct)
    {
        await adb.ForceStopAsync(c.PackageId, ct);
        await adb.ClearLogcatAsync(ct);
        await Task.Delay(options.Cooldown, ct);
        await adb.StartFrameBenchAsync(c.Component!, seconds, ct);

        // About 4 x (1.5 + 2N) seconds of effects, plus slack for a slow start.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4 * (2 * seconds + 2) + 40);
        string text;
        while (true)
        {
            text = await adb.LogcatAsync(["BENCH:I"], ct);
            if (text.Contains("phase=bench_done", StringComparison.Ordinal)) break;
            if (DateTime.UtcNow > deadline)
            {
                log($"warning: {c.Label} run {run} did not finish; skipped");
                return [];
            }

            await Task.Delay(300, ct);
        }

        var load = HostInfo.LoadAverage();
        return AdbParsers.ParseFrameLines(text).Select(f => new FrameRow
        {
            Track = c.Track, Variant = c.RowVariant, Run = run, Device = device.Model, AndroidApi = device.AndroidApi,
            Abi = device.Abi, IsEmulator = device.IsEmulator, RefreshHz = f.RefreshHz, Effect = f.Effect, Phase = f.Phase,
            N = f.N, MeanMs = f.MeanMs, P50Ms = f.P50Ms, P95Ms = f.P95Ms, P99Ms = f.P99Ms, MaxMs = f.MaxMs, Slow = f.Slow,
            Gc0 = f.Gc0, Gc1 = f.Gc1, Gc2 = f.Gc2, AllocMb = f.AllocMb, HostLoad = load, HostCpus = HostInfo.Cpus,
        }).ToList();
    }
}
