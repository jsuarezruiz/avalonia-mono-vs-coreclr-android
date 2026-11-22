using SkiaSharp;

namespace Bench;

/// <summary>The statistics printed on one card, in seconds.</summary>
public sealed record SideStats(int N, double MedianSeconds, double CiLowSeconds, double CiHighSeconds);

/// <param name="Clock">Seconds the clock on this phone counts to: the shown run's own measurement.</param>
public sealed record VideoSide(
    string Name, string Sub, SKColor Color, string ClipPath, ClipAnalysis Clip, double Clock, SideStats? Stats, string Spread);

public sealed record VideoVerdict(
    Side Winner, bool Tie, double Percent, double DeltaSeconds, double? CiLowPercent, double? CiHighPercent);

/// <summary>Everything the renderer needs. It never measures anything itself.</summary>
/// <param name="MetricTitle">Upper-case label for the clock, e.g. "TIME TO FULLY DRAWN" or "LAUNCH TIME".</param>
/// <param name="StatsMode">True when the claim comes from a full session (median, interval, n on each card).</param>
/// <param name="DeviceLine">Shown under the title, e.g. "Measured on POCO F5 · Android API 34".</param>
/// <param name="BlurHome">Blur the frames before the app appears, so the phone's own home screen is not published.</param>
public sealed record VideoModel(
    VideoSide Left, VideoSide Right, VideoVerdict Verdict, string Eyebrow, string DeviceLine, string Footer, string MetricTitle,
    bool StatsMode, bool BlurHome = true);

public static class VideoPalette
{
    public static readonly SKColor BgTop = new(14, 45, 34);
    public static readonly SKColor BgBottom = new(6, 17, 13);
    public static readonly SKColor Ink = new(238, 245, 241);
    public static readonly SKColor Muted = new(142, 166, 155);
    public static readonly SKColor Faint = new(98, 124, 112);
    public static readonly SKColor Sun = new(244, 185, 66);
    public static readonly SKColor Teal = new(92, 200, 184);
    public static readonly SKColor Coral = new(232, 124, 100);
    public static readonly SKColor Track = new(36, 62, 53);
    public static readonly SKColor Divider = new(52, 84, 72);
    public static readonly SKColor Dark = new(9, 26, 20);

    /// <summary>Mono is amber and CoreCLR is teal; for anything else the left app is amber and the right teal.</summary>
    public static (SKColor Left, SKColor Right) ForNames(string left, string right)
    {
        static SKColor? ByRuntime(string name) =>
            name.Contains("mono", StringComparison.OrdinalIgnoreCase) ? Sun :
            name.Contains("coreclr", StringComparison.OrdinalIgnoreCase) ? Teal : null;
        var l = ByRuntime(left);
        var r = ByRuntime(right);
        return l is not null && r is not null && l != r ? (l.Value, r.Value) : (Sun, Teal);
    }
}
