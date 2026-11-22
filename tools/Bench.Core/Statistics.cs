namespace Bench;

/// <summary>
/// Plain statistics for the reports. No dependencies, and deterministic for a given seed. Percentiles interpolate
/// linearly and the bootstrap takes the 2.5th and 97.5th percentile of the resampled statistic, which matches the
/// common definitions and an independent implementation (see the tests).
/// </summary>
public static class Statistics
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("no values", nameof(values));
        var sorted = values.OrderBy(v => v).ToArray();
        return MedianOfSorted(sorted);
    }

    private static double MedianOfSorted(double[] sorted)
    {
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>Percentile (0..100) with linear interpolation between the closest ranks.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1) return sorted[0];
        var k = (sorted.Length - 1) * p / 100.0;
        var lo = (int)k;
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (k - lo);
    }

    /// <summary>95% bootstrap interval of the median of <paramref name="values"/>.</summary>
    public static (double Low, double High) BootstrapMedianCi(IReadOnlyList<double> values, int iterations = 2000, int seed = 1234)
    {
        if (values.Count == 0) throw new ArgumentException("no values", nameof(values));
        var rng = new Random(seed);
        var medians = new double[iterations];
        var buffer = new double[values.Count];
        for (var i = 0; i < iterations; i++)
            medians[i] = ResampledMedian(values, buffer, rng);
        Array.Sort(medians);
        return (medians[(int)(0.025 * iterations)], medians[Math.Min((int)(0.975 * iterations), iterations - 1)]);
    }

    /// <summary>
    /// 95% bootstrap interval of (median(other) - median(baseline)) / median(baseline), as a fraction. Negative means
    /// <paramref name="other"/> took less time than <paramref name="baseline"/>.
    /// </summary>
    public static (double Low, double High) BootstrapMedianDelta(
        IReadOnlyList<double> baseline, IReadOnlyList<double> other, int iterations = 2000, int seed = 1234)
    {
        if (baseline.Count == 0 || other.Count == 0) throw new ArgumentException("no values");
        var rng = new Random(seed);
        var bufferBase = new double[baseline.Count];
        var bufferOther = new double[other.Count];
        var deltas = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var b = ResampledMedian(baseline, bufferBase, rng);
            var o = ResampledMedian(other, bufferOther, rng);
            if (b != 0) deltas.Add((o - b) / b);
        }

        if (deltas.Count == 0) return (double.NaN, double.NaN);
        deltas.Sort();
        return (deltas[(int)(0.025 * deltas.Count)], deltas[Math.Min((int)(0.975 * deltas.Count), deltas.Count - 1)]);
    }

    private static double ResampledMedian(IReadOnlyList<double> values, double[] buffer, Random rng)
    {
        for (var j = 0; j < buffer.Length; j++)
            buffer[j] = values[rng.Next(values.Count)];
        Array.Sort(buffer);
        return MedianOfSorted(buffer);
    }
}
