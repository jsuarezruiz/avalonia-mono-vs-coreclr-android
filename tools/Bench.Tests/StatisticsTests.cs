namespace Bench.Tests;

public class StatisticsTests
{
    [Fact]
    public void Median_of_odd_and_even_counts()
    {
        Assert.Equal(3, Statistics.Median([5, 1, 3]));
        Assert.Equal(2.5, Statistics.Median([4, 1, 2, 3]));
    }

    [Fact]
    public void Percentile_interpolates_linearly()
    {
        double[] v = [10, 20, 30, 40, 50];
        Assert.Equal(10, Statistics.Percentile(v, 0));
        Assert.Equal(30, Statistics.Percentile(v, 50));
        Assert.Equal(48, Statistics.Percentile(v, 95), 6);   // 40 + (50 - 40) * 0.8
    }

    [Fact]
    public void Bootstrap_is_deterministic_for_a_seed()
    {
        var v = Enumerable.Range(0, 50).Select(i => 50 + 10 * Math.Sin(i * 1.7)).ToArray();   // continuous, so seeds differ
        Assert.Equal(Statistics.BootstrapMedianCi(v), Statistics.BootstrapMedianCi(v));
        Assert.NotEqual(Statistics.BootstrapMedianCi(v, seed: 1), Statistics.BootstrapMedianCi(v, seed: 2));
    }

    [Fact]
    public void Interval_contains_the_median_and_is_ordered()
    {
        double[] v = [9, 11, 10, 12, 8, 10, 11, 9, 10, 13];
        var (lo, hi) = Statistics.BootstrapMedianCi(v);
        var m = Statistics.Median(v);
        Assert.True(lo <= m && m <= hi);
    }

    // Expected values come from an independent implementation (Python's statistics and random modules) run on the
    // same 60 runs: medians 942 ms and 442.5 ms, intervals [933, 985] and [431, 465.5], and -53.0% [-55.1%, -50.6%]
    // for the difference. The random streams differ between implementations, so the intervals are compared with a
    // small tolerance and the medians exactly.
    [Fact]
    public void Matches_an_independent_implementation_on_a_real_session()
    {
        var rows = StartupCsv.Parse(Fixtures.Text("startup-sample.csv").Split('\n'));
        var mono = rows.Where(r => r.Entry == "net10/mono-aot-profiled").Select(r => r.FullyDrawnMs!.Value).ToArray();
        var coreclr = rows.Where(r => r.Entry == "net11/coreclr").Select(r => r.FullyDrawnMs!.Value).ToArray();

        Assert.Equal(30, mono.Length);
        Assert.Equal(30, coreclr.Length);
        Assert.Equal(942, Statistics.Median(mono));
        Assert.Equal(442.5, Statistics.Median(coreclr));

        var (monoLo, monoHi) = Statistics.BootstrapMedianCi(mono);
        Assert.InRange(monoLo, 925, 942);
        Assert.InRange(monoHi, 960, 1000);

        var (lo, hi) = Statistics.BootstrapMedianDelta(mono, coreclr);
        Assert.InRange(lo, -0.565, -0.540);
        Assert.InRange(hi, -0.520, -0.495);
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(442.5, 0, "442")]        // a true tie goes to the even digit, like printf
    [InlineData(443.5, 0, "444")]
    [InlineData(130.65, 1, "130.7")]     // not a tie in binary (130.65 is a hair above), so it rounds up
    [InlineData(0.25, 1, "0.2")]         // exact tie, even digit
    [InlineData(0.75, 1, "0.8")]
    [InlineData(-53.05, 1, "-53.0")]     // 53.05 is a hair below in binary
    [InlineData(1311.0, 0, "1311")]
    [InlineData(21.6, 1, "21.6")]
    public void Formats_like_printf(double value, int digits, string expected) =>
        Assert.Equal(expected, Format.Fixed(value, digits));
}
