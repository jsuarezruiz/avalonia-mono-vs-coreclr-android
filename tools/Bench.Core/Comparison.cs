namespace Bench;

public enum Side { Left, Right }

/// <summary>What the runs of one app add up to, on one metric.</summary>
public sealed record EntrySummary(
    string Entry, Metric Metric, int N, IReadOnlyList<double> ValuesMs, double MedianMs, double CiLowMs, double CiHighMs,
    double P95Ms, double? TotalTimeMedianMs, double? PssMedianKb, long ApkBytes, int NativeLibTimeouts,
    double? Load, double? Cpus);

/// <summary>Who needed less time, by how much, and how sure the data lets us be.</summary>
public sealed record Claim(
    Side Winner, double Percent, double DeltaSeconds, double CiLowPercent, double CiHighPercent, bool Tie, bool? TotalTimeAgrees)
{
    public Side Loser => Winner == Side.Left ? Side.Right : Side.Left;
}

public static class Summaries
{
    public static EntrySummary Summarize(string entry, IReadOnlyList<RunRow> rows, Metric metric)
    {
        var values = rows.Select(r => r.Value(metric)).Where(v => v is not null).Select(v => v!.Value).ToArray();
        if (values.Length == 0) throw new BenchException($"No {metric.Describe()} values for '{entry}'.");

        var (lo, hi) = Statistics.BootstrapMedianCi(values);
        double? MedianOf(Func<RunRow, double?> pick)
        {
            var v = rows.Select(pick).Where(x => x is not null).Select(x => x!.Value).ToArray();
            return v.Length == 0 ? null : Statistics.Median(v);
        }

        var loads = rows.Where(r => r.HostLoad is not null).Select(r => r.HostLoad!.Value).ToArray();
        var cpus = rows.Where(r => r.HostCpus > 0).Select(r => (double)r.HostCpus).ToArray();
        return new EntrySummary(
            entry, metric, values.Length, values, Statistics.Median(values), lo, hi, Statistics.Percentile(values, 95),
            MedianOf(r => r.TotalTimeMs), MedianOf(r => r.PssKb), rows.Select(r => r.ApkBytes).DefaultIfEmpty(0).Max(),
            rows.Count(r => r.NativeLibTimeouts > 0),
            loads.Length == 0 ? null : Statistics.Median(loads), cpus.Length == 0 ? null : Statistics.Median(cpus));
    }

    /// <summary>The claim: (slower - faster) / slower on the medians, with a bootstrap interval for the difference.</summary>
    public static Claim Compare(EntrySummary left, EntrySummary right)
    {
        var winner = left.MedianMs < right.MedianMs ? Side.Left : Side.Right;
        var (fast, slow) = winner == Side.Left ? (left, right) : (right, left);
        var (lo, hi) = Statistics.BootstrapMedianDelta(slow.ValuesMs, fast.ValuesMs);

        bool? totalAgrees = null;
        if (left.Metric != Metric.TotalTime && left.TotalTimeMedianMs is { } lt && right.TotalTimeMedianMs is { } rt)
            totalAgrees = (lt < rt) == (left.MedianMs < right.MedianMs);

        return new Claim(winner, (slow.MedianMs - fast.MedianMs) / slow.MedianMs * 100.0,
            (slow.MedianMs - fast.MedianMs) / 1000.0, -hi * 100.0, -lo * 100.0, Tie: hi >= 0, totalAgrees);
    }
}

public static class PublishChecks
{
    public const int MinRuns = 30;

    /// <summary>The reasons a comparison should not be published yet. Empty means the mechanical checks passed.</summary>
    public static IReadOnlyList<string> Evaluate(
        DeviceInfo device, EntrySummary left, EntrySummary right, Claim claim, bool clipOrderMatches)
    {
        var problems = new List<string>();
        if (device.IsEmulator) problems.Add("recorded on an emulator; use a real phone");
        foreach (var s in new[] { left, right })
        {
            if (s.N < MinRuns) problems.Add($"{s.Entry}: {s.N} runs; aim for {MinRuns} or more");
            if (s.NativeLibTimeouts > 0) problems.Add($"{s.Entry}: {s.NativeLibTimeouts} runs hit the native-library resolver stall");
            if (s.Load is { } load && s.Cpus is { } cpus && load > cpus)
                problems.Add($"{s.Entry}: busy host (load {load:0} on {cpus:0} CPUs)");
        }

        if (claim.TotalTimeAgrees == false) problems.Add("the metric and am start TotalTime disagree on direction");
        if (claim.Tie) problems.Add("the interval reaches zero: there is no clear winner");
        if (!clipOrderMatches) problems.Add("the clip's finishing order differs from the statistics; record more takes");
        return problems;
    }

    /// <summary>The checks that still apply when there is no full session behind the video.</summary>
    public static IReadOnlyList<string> EvaluateWithoutStatistics(DeviceInfo device)
    {
        var problems = new List<string>();
        if (device.IsEmulator) problems.Add("recorded on an emulator; use a real phone");
        problems.Add($"no full session: the claim rests on the few runs recorded for the clip (run {MinRuns}+ and pass them as statistics)");
        return problems;
    }
}
