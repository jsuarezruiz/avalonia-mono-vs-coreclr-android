namespace Bench;

/// <summary>Where, in a screen recording of a launch, the app starts and where its picture settles.</summary>
public sealed record ClipAnalysis(int T0, int Finish, int Frames)
{
    /// <summary>Launch to the frame after which nothing changes. Includes the system's splash-exit animation.</summary>
    public double VideoSeconds => (Finish - T0) / (double)ClipAnalyzer.Fps;
}

public sealed class ClipException(string message) : Exception(message);

public static class ClipAnalyzer
{
    public const int Fps = 30;

    /// <summary>A tiny grayscale copy of every frame at <see cref="Fps"/>, enough to tell what changed.</summary>
    public static async Task<IReadOnlyList<byte[]>> ProbeFramesAsync(string clip, CancellationToken ct = default, int width = 72, int height = 160)
    {
        using var process = ProcessRunner.Start(Ffmpeg.Path,
            ["-v", "error", "-i", clip, "-vf", $"fps={Fps},scale={width}:{height}", "-pix_fmt", "gray", "-f", "rawvideo", "-"],
            redirectStdout: true);
        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        await process.WaitForExitAsync(ct);

        var all = buffer.ToArray();
        var size = width * height;
        var frames = new List<byte[]>(all.Length / size);
        for (var i = 0; i + size <= all.Length; i += size)
            frames.Add(all.AsSpan(i, size).ToArray());
        return frames;
    }

    public static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    /// <summary>
    /// t0 is where the launch first shows on screen: the first BIG change away from the home screen (the splash
    /// covers it), stepped back to where that change began. Small changes before it, such as the launcher settling,
    /// do not count. Finish is where the picture stops changing for good.
    /// </summary>
    public static ClipAnalysis Analyze(IReadOnlyList<byte[]> frames, double bigChange = 30.0, double quiet = 2.0, double finishThreshold = 0.6)
    {
        if (frames.Count < 20)
            throw new ClipException($"only {frames.Count} frames; was the recording cut short?");

        var home = frames[3];
        var diffs = frames.Select(f => MeanAbsDiff(f, home)).ToArray();

        var big = -1;
        for (var k = 4; k < frames.Count - 5; k++)
        {
            if (diffs[k] > bigChange && diffs[k + 1] > bigChange && diffs[k + 2] > bigChange && diffs[k + 3] > bigChange)
            {
                big = k;
                break;
            }
        }

        if (big < 0) throw new ClipException("the screen never reacted to the launch (is the home screen showing?)");

        var t0 = big;
        while (t0 > 4 && diffs[t0 - 1] > quiet) t0--;

        var last = frames[^1];
        var finish = frames.Count - 1;
        while (finish > t0 && MeanAbsDiff(frames[finish - 1], last) < finishThreshold) finish--;
        if (finish <= t0 + 2) throw new ClipException("the picture finished changing right after the launch; nothing to time");

        return new ClipAnalysis(t0, finish, frames.Count);
    }

    public static async Task<ClipAnalysis> AnalyzeFileAsync(string clip, CancellationToken ct = default) =>
        Analyze(await ProbeFramesAsync(clip, ct));
}
