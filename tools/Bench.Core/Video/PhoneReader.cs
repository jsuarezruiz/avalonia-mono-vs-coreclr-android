using System.Diagnostics;

namespace Bench;

/// <summary>Sequential raw BGRA frames of a clip at <see cref="ClipAnalyzer.Fps"/>, scaled to the phone size.</summary>
internal sealed class PhoneReader : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stream;
    private readonly byte[] _frame;
    private readonly byte[] _scratch;
    private bool _ended;

    public PhoneReader(string clip, int width, int height, int skipFrames)
    {
        _frame = new byte[width * height * 4];
        _scratch = new byte[_frame.Length];
        _process = ProcessRunner.Start(Ffmpeg.Path,
            ["-v", "error", "-i", clip, "-vf", $"fps={ClipAnalyzer.Fps},scale={width}:{height}:flags=lanczos",
             "-pix_fmt", "bgra", "-f", "rawvideo", "-"], redirectStdout: true);
        _stream = _process.StandardOutput.BaseStream;
        for (var i = 0; i < skipFrames; i++) Next();
    }

    /// <summary>The next frame; once the clip has ended, the last frame again.</summary>
    public byte[] Next()
    {
        if (_ended) return _frame;
        var read = 0;
        while (read < _scratch.Length)
        {
            var n = _stream.Read(_scratch, read, _scratch.Length - read);
            if (n == 0) { _ended = true; return _frame; }
            read += n;
        }

        Buffer.BlockCopy(_scratch, 0, _frame, 0, _frame.Length);
        return _frame;
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _process.Dispose();
    }
}
