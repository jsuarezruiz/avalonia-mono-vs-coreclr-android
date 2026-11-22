namespace Bench;

public static class Ffmpeg
{
    public static string Path => Environment.GetEnvironmentVariable("FFMPEG") is { Length: > 0 } p ? p : "ffmpeg";
    public static string ProbePath => Environment.GetEnvironmentVariable("FFPROBE") is { Length: > 0 } p ? p : "ffprobe";

    public static async Task EnsureAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(Path, ["-version"], TimeSpan.FromSeconds(10), ct);
            if (!r.Ok) throw new BenchException($"ffmpeg did not start: {r.StdErr}");
        }
        catch (ToolNotFoundException)
        {
            throw new BenchException("ffmpeg is needed to analyse the clips and encode the video. Install it " +
                                     "(macOS: brew install ffmpeg) or point FFMPEG at the executable.");
        }
    }

    /// <summary>Pixel size of the first video stream.</summary>
    public static async Task<(int Width, int Height)> ProbeSizeAsync(string file, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(ProbePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", file],
            TimeSpan.FromSeconds(20), ct);
        var parts = r.StdOut.Trim().Split(',');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
            throw new BenchException($"Could not read the video size of {file}.");
        return (w, h);
    }
}
