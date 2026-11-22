namespace Bench;

/// <summary>One recorded launch: the clip and what the launch measured.</summary>
public sealed record Take(
    string Side, string Entry, int Number, string ClipPath,
    double? AppFullyDrawnMs, double? SystemFullyDrawnMs, double? TotalTimeMs, int NativeLibTimeouts, double? HostLoad)
{
    public double? Value(Metric metric) => metric switch
    {
        Metric.AppFullyDrawn => AppFullyDrawnMs,
        Metric.SystemFullyDrawn => SystemFullyDrawnMs,
        _ => TotalTimeMs,
    };
}

public static class TakeSelector
{
    /// <summary>
    /// The take to play. With a known median (from a full session) it is the take closest to it; without one it is
    /// the median take (the lower of the two middle takes for an even count). Never the best take.
    /// </summary>
    public static Take Choose(IReadOnlyList<Take> takes, Metric metric, double? target)
    {
        var usable = takes.Where(t => t.Value(metric) is not null).OrderBy(t => t.Value(metric)).ThenBy(t => t.Number).ToList();
        if (usable.Count == 0) throw new BenchException("None of the recorded takes has the measurement needed to pick one.");
        if (target is { } median)
            return usable.OrderBy(t => Math.Abs(t.Value(metric)!.Value - median)).ThenBy(t => t.Number).First();
        return usable[(usable.Count - 1) / 2];
    }
}
