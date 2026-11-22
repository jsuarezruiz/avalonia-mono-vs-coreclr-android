namespace Bench.Tests;

public class ClipAnalyzerTests
{
    private const int Size = 72 * 160;

    private static byte[] Flat(int value) => Enumerable.Repeat((byte)value, Size).ToArray();

    /// <summary>
    /// home screen, a small launcher animation (must not count as the launch), the splash appearing, a still
    /// splash, a reveal that changes every frame, and a final picture that stays.
    /// </summary>
    private static List<byte[]> Launch()
    {
        var frames = new List<byte[]>();
        for (var i = 0; i < 6; i++) frames.Add(Flat(20));                 // 0-5   home
        for (var i = 0; i < 8; i++) frames.Add(Flat(32));                 // 6-13  launcher settling: diff 12
        for (var i = 14; i < 38; i++) frames.Add(Flat(20));               // 14-37 home again
        frames.Add(Flat(25));                                             // 38    first hint of the splash (diff 5)
        frames.AddRange([Flat(100), Flat(200), Flat(240)]);               // 39-41 splash fading in
        for (var i = 42; i < 81; i++) frames.Add(Flat(240));              // 42-80 splash, still
        for (var i = 0; i < 9; i++) frames.Add(Flat(230 - i * 15));       // 81-89 reveal, every frame different
        for (var i = 90; i < 111; i++) frames.Add(Flat(100));             // 90-110 final picture
        return frames;
    }

    [Fact]
    public void Finds_the_launch_and_the_end_of_the_reveal()
    {
        var result = ClipAnalyzer.Analyze(Launch());
        Assert.Equal(38, result.T0);        // stepped back to where the splash began, not at the launcher animation
        Assert.Equal(90, result.Finish);
        Assert.Equal(52 / 30.0, result.VideoSeconds, 6);
    }

    [Fact]
    public void A_small_launcher_animation_alone_is_not_a_launch()
    {
        var frames = new List<byte[]>();
        for (var i = 0; i < 6; i++) frames.Add(Flat(20));
        for (var i = 0; i < 40; i++) frames.Add(Flat(i % 2 == 0 ? 32 : 20));
        var ex = Assert.Throws<ClipException>(() => ClipAnalyzer.Analyze(frames));
        Assert.Contains("never reacted", ex.Message);
    }

    [Fact]
    public void Rejects_a_recording_that_is_too_short() =>
        Assert.Throws<ClipException>(() => ClipAnalyzer.Analyze(Enumerable.Range(0, 10).Select(_ => Flat(20)).ToList()));

    [Fact]
    public void Rejects_a_picture_that_never_changes_after_the_launch()
    {
        var frames = new List<byte[]>();
        for (var i = 0; i < 6; i++) frames.Add(Flat(20));
        for (var i = 0; i < 30; i++) frames.Add(Flat(240));
        Assert.Throws<ClipException>(() => ClipAnalyzer.Analyze(frames));
    }

    [Theory]
    [InlineData(1080, 2400, "540x1200")]
    [InlineData(1080, 1920, "674x1200")]
    [InlineData(720, 1280, "674x1200")]
    [InlineData(480, 800, "480x800")]       // already small: not scaled up
    public void Records_at_a_sensible_size(int w, int h, string expected) =>
        Assert.Equal(expected, ClipRecorder.RecordSize(w, h));
}

public class TakeSelectorTests
{
    private static Take T(int n, double? app, double total = 0) =>
        new("left", "a", n, $"t{n}.mp4", app, null, total, 0, null);

    [Fact]
    public void Picks_the_take_closest_to_the_known_median()
    {
        var takes = new[] { T(1, 970), T(2, 973), T(3, 959), T(4, 949), T(5, 955) };
        Assert.Equal(4, TakeSelector.Choose(takes, Metric.AppFullyDrawn, 942).Number);
    }

    [Fact]
    public void Ties_go_to_the_lower_take_number()
    {
        var takes = new[] { T(2, 950), T(1, 950), T(3, 900) };
        Assert.Equal(1, TakeSelector.Choose(takes, Metric.AppFullyDrawn, 950).Number);
    }

    [Fact]
    public void Without_a_target_it_takes_the_median_take_never_the_best()
    {
        var takes = new[] { T(1, 900), T(2, 1000), T(3, 950) };
        Assert.Equal(3, TakeSelector.Choose(takes, Metric.AppFullyDrawn, null).Number);
        var even = new[] { T(1, 900), T(2, 1000), T(3, 950), T(4, 980) };
        Assert.Equal(3, TakeSelector.Choose(even, Metric.AppFullyDrawn, null).Number);   // lower of 950 / 980
    }

    [Fact]
    public void Takes_without_the_metric_are_ignored()
    {
        var takes = new[] { T(1, null), T(2, 100) };
        Assert.Equal(2, TakeSelector.Choose(takes, Metric.AppFullyDrawn, null).Number);
        Assert.Throws<BenchException>(() => TakeSelector.Choose([T(1, null)], Metric.AppFullyDrawn, null));
    }
}

public class MetricsTests
{
    private static RunRow Row(double? app, double? system, double total = 100) =>
        new() { FullyDrawnMs = app, SystemFullyDrawnMs = system, TotalTimeMs = total };

    [Fact]
    public void Prefers_the_apps_own_mark_when_both_have_it() =>
        Assert.Equal(Metric.AppFullyDrawn, Metrics.Choose([[Row(1, 2)], [Row(3, 4)]]).Metric);

    [Fact]
    public void Falls_back_to_androids_fully_drawn_time_then_to_total_time()
    {
        Assert.Equal(Metric.SystemFullyDrawn, Metrics.Choose([[Row(1, 2)], [Row(null, 4)]]).Metric);
        Assert.Equal(Metric.TotalTime, Metrics.Choose([[Row(1, 2)], [Row(null, null)]]).Metric);
    }

    [Fact]
    public void Never_mixes_sources_between_the_two_apps() =>
        Assert.Equal(Metric.TotalTime, Metrics.Choose([[Row(1, null)], [Row(null, 4)]]).Metric);
}
