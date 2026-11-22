using System.Globalization;
using System.Text;

namespace Bench;

public static class Report
{
    private static string F(double v, string fmt = "0") => Format.Fixed(v, fmt == "0" ? 0 : fmt.Length - 2);

    public static string Markdown(
        DeviceInfo device, Metric metric, string metricReason, EntrySummary left, EntrySummary right, Claim claim,
        string leftLabel, string rightLabel, IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cold-start comparison").AppendLine();
        sb.AppendLine($"- **Device:** {device.DisplayName} ({(device.Name.Length > 0 || !device.IsEmulator ? device.Model + ", " : "")}API {device.AndroidApi}, {device.Abi}, {(device.IsEmulator ? "emulator" : "physical device")})");
        sb.AppendLine($"- **Metric:** {metric.Describe()}; {metricReason}");
        sb.AppendLine($"- **Runs:** {left.N} and {right.N}, cold starts interleaved in a rotating order").AppendLine();

        sb.AppendLine($"| App | n | median ({metric.Short()}, ms) | 95% CI | p95 | TotalTime median | PSS (MB) | APK (MB) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var (s, label) in new[] { (left, leftLabel), (right, rightLabel) })
        {
            sb.AppendLine($"| {label} | {s.N} | {F(s.MedianMs)} | {F(s.CiLowMs)} to {F(s.CiHighMs)} | {F(s.P95Ms)} | " +
                          $"{(s.TotalTimeMedianMs is { } t ? F(t) : "n/a")} | {(s.PssMedianKb is { } p ? F(p / 1024.0) : "n/a")} | " +
                          $"{(s.ApkBytes > 0 ? F(s.ApkBytes / 1048576.0, "0.0") : "n/a")} |");
        }

        sb.AppendLine();
        var (winnerLabel, loserLabel) = claim.Winner == Side.Left ? (leftLabel, rightLabel) : (rightLabel, leftLabel);
        sb.AppendLine(claim.Tie
            ? "**Result:** no clear difference (the 95% interval of the difference reaches zero)."
            : $"**Result:** {winnerLabel} took {F(claim.Percent, "0.0")}% less time than {loserLabel} " +
              $"(median, 95% CI {F(claim.CiLowPercent, "0.0")}% to {F(claim.CiHighPercent, "0.0")}%), " +
              $"{F(claim.DeltaSeconds, "0.00")} s sooner.");
        sb.AppendLine().AppendLine("`less time` is (slower - faster) / slower, on the medians.").AppendLine();

        if (problems.Count > 0)
        {
            sb.AppendLine("## Not ready to publish").AppendLine();
            foreach (var p in problems) sb.AppendLine($"- {p}");
        }
        else
        {
            sb.AppendLine("Checks passed: real device, quiet host, enough runs, the metric agrees with TotalTime.");
        }

        return sb.ToString();
    }
}
