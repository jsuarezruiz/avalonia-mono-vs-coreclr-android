namespace Bench;

/// <summary>Which number a comparison is made on. It must be the same one for both apps.</summary>
public enum Metric
{
    /// <summary>The app's own fully_drawn mark, in ms since its process started (needs the BENCH probe).</summary>
    AppFullyDrawn,
    /// <summary>Android's "Fully drawn" time for apps that call ReportFullyDrawn, in ms since the launch request.</summary>
    SystemFullyDrawn,
    /// <summary>The system's own <c>am start -W</c> TotalTime; available for every app.</summary>
    TotalTime,
}

public static class Metrics
{
    public static double? Value(this RunRow row, Metric metric) => metric switch
    {
        Metric.AppFullyDrawn => row.FullyDrawnMs,
        Metric.SystemFullyDrawn => row.SystemFullyDrawnMs,
        _ => row.TotalTimeMs,
    };

    public static string Describe(this Metric metric) => metric switch
    {
        Metric.AppFullyDrawn => "the app's own fully-drawn mark",
        Metric.SystemFullyDrawn => "Android's fully-drawn time (ReportFullyDrawn)",
        _ => "the system's TotalTime (am start -W)",
    };

    /// <summary>Short phrase for the video: "time to fully drawn" or "launch time".</summary>
    public static string Short(this Metric metric) => metric == Metric.TotalTime ? "launch time" : "fully drawn";

    /// <summary>
    /// The best metric every row of every entry has: the app's mark, else Android's fully-drawn time, else TotalTime.
    /// Never mixes sources between the two apps, since that would compare different things.
    /// </summary>
    public static (Metric Metric, string Reason) Choose(IEnumerable<IReadOnlyList<RunRow>> entries)
    {
        var all = entries.SelectMany(e => e).ToList();
        if (all.Count > 0 && all.All(r => r.FullyDrawnMs is not null))
            return (Metric.AppFullyDrawn, "both apps log the BENCH fully_drawn mark");
        if (all.Count > 0 && all.All(r => r.SystemFullyDrawnMs is not null))
            return (Metric.SystemFullyDrawn, "both apps call ReportFullyDrawn, but not both log the BENCH marks");
        return (Metric.TotalTime, "not both apps report when they are fully drawn, so the comparison uses TotalTime");
    }
}
