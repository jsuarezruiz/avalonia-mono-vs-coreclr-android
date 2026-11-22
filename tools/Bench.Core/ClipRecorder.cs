using System.Diagnostics;

namespace Bench;

/// <summary>
/// Records one launch of an app with <c>screenrecord</c>. It reuses the runner's launch, so a recorded take is
/// measured exactly like a plain run: force-stop, home screen, start recording, launch, wait until drawn, stop.
/// </summary>
public sealed class ClipRecorder(ColdStartRunner runner, Action<string> log)
{
    private const string Remote = "/sdcard/bench_take.mp4";
    public TimeSpan Tail { get; init; } = TimeSpan.FromSeconds(1.5);

    public async Task<Take?> RecordAsync(string side, Candidate c, int number, string clipPath, CancellationToken ct = default)
    {
        var adb = runner.Adb;
        var (w, h) = await adb.ScreenSizeAsync(ct);
        var size = RecordSize(w, h);
        Process? recording = null;
        await adb.ShellAsync($"rm -f {Remote}", ct: ct);

        LaunchMeasurement? m;
        try
        {
            m = await runner.LaunchAsync(c, pressHome: true,
                beforeLaunch: async () =>
                {
                    recording = adb.StartScreenRecord(size, Remote);
                    await Task.Delay(TimeSpan.FromSeconds(1.5), ct);       // the recorder needs a moment, and the home screen should be on tape
                },
                afterSettled: async () =>
                {
                    await Task.Delay(Tail, ct);                              // keep the final picture on tape
                    await StopAsync(adb, recording, ct);
                },
                ct: ct);
        }
        finally
        {
            if (recording is { HasExited: false }) await StopAsync(adb, recording, ct);
            recording?.Dispose();
        }

        if (m is null) return null;
        await Task.Delay(500, ct);
        var pull = await adb.RunAsync(["pull", Remote, clipPath], ct: ct);
        await adb.ShellAsync($"rm -f {Remote}", ct: ct);
        if (!pull.Ok || !File.Exists(clipPath))
        {
            log($"warning: could not pull the recording of {c.Label} take {number}; skipped");
            return null;
        }

        return new Take(side, c.Entry, number, clipPath, m.Phases.TryGetValue("fully_drawn", out var app) ? app : null,
            m.SystemFullyDrawnMs, m.Start.TotalTimeMs, m.NativeLibTimeouts, m.HostLoad);
    }

    /// <summary>SIGINT lets screenrecord finish the mp4 properly.</summary>
    private static async Task StopAsync(Adb adb, Process? recording, CancellationToken ct)
    {
        await adb.ShellAsync("pkill -2 screenrecord", ct: ct);
        if (recording is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await recording.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { recording.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>At most 1200 px tall (smaller clips are quicker to analyse), even numbers, same aspect as the screen.</summary>
    public static string RecordSize(int width, int height)
    {
        var scale = Math.Min(1.0, 1200.0 / height);
        var w = (int)(width * scale) / 2 * 2;
        var h = (int)(height * scale) / 2 * 2;
        return $"{w}x{h}";
    }
}
