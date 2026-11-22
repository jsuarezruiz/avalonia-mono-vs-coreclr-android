using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bench;

public sealed record CompareOptions
{
    public int Runs { get; init; } = 30;
    public int Warmup { get; init; } = 1;
    public int Takes { get; init; } = 7;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(3);
    public bool DropCaches { get; init; }
    public bool Video { get; init; } = true;
    public string Eyebrow { get; init; } = "ANDROID  ·  COLD START";
    public string Note { get; init; } = "";

    public RunnerOptions ToRunnerOptions() => new() { Runs = Runs, Warmup = Warmup, Cooldown = Cooldown, DropCaches = DropCaches };
}

public sealed record CompareResult(
    string Directory, EntrySummary? Left, EntrySummary? Right, Claim? Claim, IReadOnlyList<string> Problems,
    string? VideoPath, string? ReportPath);

/// <summary>
/// The whole job in three phases that share one session folder: measure (interleaved cold starts), record (clips of
/// launches) and build (statistics, report and video). The command line runs them together or one at a time.
/// </summary>
public sealed class ComparePipeline(Action<string> log)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep "·" and apostrophes readable
    };

    // ---- measure --------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<RunRow>> MeasureAsync(ColdStartRunner runner, IReadOnlyList<Candidate> apps, string dir, CancellationToken ct)
    {
        var rows = await runner.RunAsync(apps, ct);
        StartupCsv.Write(Path.Combine(dir, SessionFiles.RunsFile), rows);
        return rows;
    }

    // ---- record ---------------------------------------------------------------------------------------------------
    /// <summary>Records <paramref name="takes"/> launches per side, alternating, after a warm-up if the apps' signals are not known yet.</summary>
    public async Task<IReadOnlyList<Take>> RecordAsync(ColdStartRunner runner, Candidate left, Candidate right, int takes, string dir, CancellationToken ct)
    {
        if (left.Marks == MarkSource.Unknown || right.Marks == MarkSource.Unknown)
            await runner.WarmUpAsync([left, right], ct);

        var clips = Path.Combine(dir, SessionFiles.TakesDir);
        System.IO.Directory.CreateDirectory(clips);
        var recorder = new ClipRecorder(runner, log);
        var result = new List<Take>();
        for (var n = 1; n <= takes; n++)
        {
            // Alternate, and swap who goes first every round, so drift lands on both.
            var order = n % 2 == 1 ? new[] { (Side.Left, left), (Side.Right, right) } : [(Side.Right, right), (Side.Left, left)];
            foreach (var (side, c) in order)
            {
                var name = side == Side.Left ? "left" : "right";
                log($"recording {name} take {n}/{takes}  {c.Label}");
                var take = await recorder.RecordAsync(name, c, n, Path.Combine(clips, $"{name}-{n:00}.mp4"), ct);
                if (take is not null) result.Add(take);
            }
        }

        SessionFiles.WriteTakes(dir, result);
        return result;
    }

    // ---- build ----------------------------------------------------------------------------------------------------
    /// <summary>
    /// Statistics, report and (when there are clips) the video, from the files in <paramref name="dir"/> and,
    /// optionally, startup rows from a full session recorded elsewhere.
    /// </summary>
    public async Task<CompareResult> BuildAsync(string dir, IReadOnlyList<RunRow>? statsRows, bool renderVideo, CancellationToken ct,
        bool blurHome = true)
    {
        var session = SessionFiles.ReadInfo(dir);
        var runsPath = Path.Combine(dir, SessionFiles.RunsFile);
        statsRows ??= File.Exists(runsPath) ? StartupCsv.Read(runsPath) : null;
        var takesPath = Path.Combine(dir, SessionFiles.TakesFile);
        var takes = File.Exists(takesPath) ? SessionFiles.ReadTakes(dir) : [];

        List<RunRow>? leftRows = null, rightRows = null;
        if (statsRows is not null)
        {
            leftRows = SelectRows(statsRows, session.Left.Entry, session);
            rightRows = SelectRows(statsRows, session.Right.Entry, session);
        }

        // The metric: whatever both apps can report, never mixed. Without a full session, whatever the takes carry.
        var (metric, reason) = leftRows is not null
            ? Metrics.Choose([leftRows, rightRows!])
            : ChooseFromTakes(takes);

        EntrySummary? left = null, right = null;
        Claim? claim = null;
        if (leftRows is not null)
        {
            left = Summaries.Summarize(session.Left.Entry, leftRows, metric);
            right = Summaries.Summarize(session.Right.Entry, rightRows!, metric);
            claim = Summaries.Compare(left, right);
            log($"{session.Left.Name}: {Format.Fixed(left.MedianMs)} ms [{Format.Fixed(left.CiLowMs)}, {Format.Fixed(left.CiHighMs)}] n={left.N}   " +
                $"{session.Right.Name}: {Format.Fixed(right.MedianMs)} ms [{Format.Fixed(right.CiLowMs)}, {Format.Fixed(right.CiHighMs)}] n={right.N}   ({metric.Describe()})");
        }

        string? videoPath = null;
        IReadOnlyList<string> problems;
        var clipOrderOk = true;
        if (renderVideo && takes.Count > 0)
        {
            (videoPath, clipOrderOk) = await BuildVideoAsync(dir, session, takes, metric, left, right, claim, blurHome, ct);
        }

        problems = left is not null && right is not null && claim is not null
            ? PublishChecks.Evaluate(session.Device, left, right, claim, clipOrderOk)
            : PublishChecks.EvaluateWithoutStatistics(session.Device);

        string? reportPath = null;
        if (left is not null && right is not null && claim is not null)
        {
            reportPath = Path.Combine(dir, "report.md");
            File.WriteAllText(reportPath, Report.Markdown(session.Device, metric, reason, left, right, claim,
                session.Left.Name, session.Right.Name, problems));
        }

        WriteJson(dir, session, metric, reason, left, right, claim, problems, videoPath);
        return new CompareResult(dir, left, right, claim, problems, videoPath, reportPath);
    }

    private static List<RunRow> SelectRows(IReadOnlyList<RunRow> all, string entry, SessionInfo session)
    {
        var rows = all.Where(r => r.Entry == entry && r.LaunchState == "COLD").ToList();
        if (rows.Count == 0)
            throw new BenchException($"No cold runs for '{entry}' in the statistics. It has: {string.Join(", ", all.Select(r => r.Entry).Distinct())}.");
        if (session.Device.Model.Length > 0 && rows.Any(r => r.Device != session.Device.Model))
            throw new BenchException($"The statistics for '{entry}' come from a different device than the clips ({session.Device.Model}).");
        return rows;
    }

    private static (Metric, string) ChooseFromTakes(IReadOnlyList<Take> takes)
    {
        if (takes.Count > 0 && takes.All(t => t.AppFullyDrawnMs is not null)) return (Metric.AppFullyDrawn, "the takes carry the app's own mark");
        if (takes.Count > 0 && takes.All(t => t.SystemFullyDrawnMs is not null)) return (Metric.SystemFullyDrawn, "the takes carry Android's fully-drawn time");
        return (Metric.TotalTime, "the takes only carry TotalTime");
    }

    private async Task<(string Path, bool OrderOk)> BuildVideoAsync(
        string dir, SessionInfo session, IReadOnlyList<Take> takes, Metric metric,
        EntrySummary? left, EntrySummary? right, Claim? claim, bool blurHome, CancellationToken ct)
    {
        var chosen = new Dictionary<Side, (Take Take, ClipAnalysis Clip, List<Take> All)>();
        foreach (var (side, name, summary) in new[] { (Side.Left, "left", left), (Side.Right, "right", right) })
        {
            var analysed = new List<(Take, ClipAnalysis)>();
            var sideTakes = takes.Where(t => t.Side == name).ToList();
            foreach (var t in sideTakes)
            {
                try { analysed.Add((t, await ClipAnalyzer.AnalyzeFileAsync(t.ClipPath, ct))); }
                catch (ClipException ex) { log($"warning: {name} take {t.Number} skipped: {ex.Message}"); }
            }

            if (analysed.Count < 3) throw new BenchException($"Only {analysed.Count} usable takes on the {name} side; record again.");
            var pick = TakeSelector.Choose(analysed.Select(a => a.Item1).ToList(), metric, summary?.MedianMs);
            foreach (var (t, clip) in analysed.OrderBy(a => a.Item1.Value(metric)))
                log($"  {name} take {t.Number}: {metric.Short()} {t.Value(metric) / 1000.0:0.00}s | video {clip.VideoSeconds:0.00}s (incl. splash exit){(t == pick ? "   <- shown" : "")}");
            chosen[side] = (pick, analysed.First(a => a.Item1 == pick).Item2, sideTakes);
            File.Copy(pick.ClipPath, Path.Combine(dir, $"{name}.mp4"), overwrite: true);
        }

        var statsMode = left is not null && right is not null && claim is not null;
        var (lc, rc) = VideoPalette.ForNames(session.Left.Name, session.Right.Name);

        VideoSide Side_(Side side, CandidateInfo info, SKColorHolder color, EntrySummary? summary)
        {
            var (take, clip, all) = chosen[side];
            var values = all.Select(t => t.Value(metric)).Where(v => v is not null).Select(v => v!.Value / 1000.0).ToList();
            return new VideoSide(info.Name, info.Sub, color.Color, take.ClipPath, clip, take.Value(metric)!.Value / 1000.0,
                summary is null ? null : new SideStats(summary.N, summary.MedianMs / 1000.0, summary.CiLowMs / 1000.0, summary.CiHighMs / 1000.0),
                string.Create(CultureInfo.InvariantCulture, $"median of {values.Count} runs, range {values.Min():0.00}-{values.Max():0.00} s"));
        }

        var leftSide = Side_(Side.Left, session.Left, new SKColorHolder(lc), left);
        var rightSide = Side_(Side.Right, session.Right, new SKColorHolder(rc), right);

        VideoVerdict verdict;
        if (statsMode)
        {
            verdict = new VideoVerdict(claim!.Winner, claim.Tie, claim.Percent, claim.DeltaSeconds, claim.CiLowPercent, claim.CiHighPercent);
        }
        else
        {
            var winner = leftSide.Clock < rightSide.Clock ? Side.Left : Side.Right;
            var (fast, slow) = (Math.Min(leftSide.Clock, rightSide.Clock), Math.Max(leftSide.Clock, rightSide.Clock));
            var pct = (slow - fast) / slow * 100.0;
            verdict = new VideoVerdict(winner, pct < 5.0, pct, slow - fast, null, null);
        }

        var clipWinner = leftSide.Clock < rightSide.Clock ? Side.Left : Side.Right;
        var orderOk = !statsMode || verdict.Tie || clipWinner == verdict.Winner;

        var deviceLine = session.Device.IsEmulator && session.Device.Name.Length == 0
            ? $"Measured on an Android emulator · API {session.Device.AndroidApi}"
            : $"Measured on {session.Device.DisplayName} · Android API {session.Device.AndroidApi}";
        var footer = string.Join(" · ", new[] { session.Device.Abi, session.Note }.Where(p => !string.IsNullOrWhiteSpace(p)))
            + $" · all times: {metric.Describe()}"
            + (statsMode ? " · clip: the recorded run closest to each median" : "");
        if (statsMode && new[] { left!, right! }.Any(s => s.Load is { } l && s.Cpus is { } c && l > c)) footer += " · busy host";

        var model = new VideoModel(leftSide, rightSide, verdict, session.Eyebrow, deviceLine, footer,
            metric == Metric.TotalTime ? "LAUNCH TIME" : "FULLY DRAWN", statsMode, blurHome);
        var path = Path.Combine(dir, "compare.mp4");
        var last = -1;
        await VideoRenderer.RenderAsync(model, path, new Progress<double>(p =>
        {
            var pctDone = (int)(p * 100);
            if (pctDone / 10 != last / 10) { last = pctDone; log($"rendering video {pctDone}%"); }
        }), ct);
        return (path, orderOk);
    }

    // SKColor is a struct from SkiaSharp; this wrapper keeps the call sites above readable.
    private readonly record struct SKColorHolder(SkiaSharp.SKColor Color);

    private static void WriteJson(string dir, SessionInfo session, Metric metric, string reason, EntrySummary? left,
        EntrySummary? right, Claim? claim, IReadOnlyList<string> problems, string? video)
    {
        object? Summary(EntrySummary? s) => s is null ? null : new
        {
            s.Entry, s.N, medianMs = Math.Round(s.MedianMs, 1), ciMs = new[] { Math.Round(s.CiLowMs, 1), Math.Round(s.CiHighMs, 1) },
            p95Ms = Math.Round(s.P95Ms, 1), totalTimeMedianMs = s.TotalTimeMedianMs, pssMedianKb = s.PssMedianKb,
            s.ApkBytes, s.NativeLibTimeouts, hostLoad = s.Load, hostCpus = s.Cpus,
        };

        var doc = new
        {
            device = session.Device, left = session.Left, right = session.Right, note = session.Note,
            metric = new { name = metric.ToString(), description = metric.Describe(), reason },
            statistics = new { left = Summary(left), right = Summary(right) },
            claim = claim is null ? null : new
            {
                fasterSide = claim.Tie ? null : claim.Winner.ToString(), percentLessTime = claim.Tie ? (double?)null : Math.Round(claim.Percent, 2),
                percentCi95 = new[] { Math.Round(claim.CiLowPercent, 2), Math.Round(claim.CiHighPercent, 2) },
                secondsSooner = claim.Tie ? (double?)null : Math.Round(claim.DeltaSeconds, 3), claim.Tie, claim.TotalTimeAgrees,
            },
            notReadyToPublish = problems,
            video,
            method = "claim = median of the chosen metric over all runs, 95% bootstrap interval, percentage = (slower - faster) / slower; " +
                     "the clock on each phone counts from the first screen reaction to the launch and stops at that run's own measurement",
        };
        File.WriteAllText(Path.Combine(dir, "compare.json"), JsonSerializer.Serialize(doc, Json));
    }
}
