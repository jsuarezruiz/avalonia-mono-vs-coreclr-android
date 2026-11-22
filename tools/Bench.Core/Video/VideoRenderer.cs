using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace Bench;

/// <summary>
/// Draws the side-by-side comparison, frame by frame, into ffmpeg: a card per app with a clock and a progress lane,
/// the two phones between them lined up on the launch, a glow on the faster one, and the verdict once both are done.
/// 1920x1080, 30 fps, H.264.
/// </summary>
public static class VideoRenderer
{
    public const int Width = 1920, Height = 1080, Fps = ClipAnalyzer.Fps;
    private const int PanelW = 458, PanelH = 840, PanelY = 150, Pad = 36, LeadFrames = 15;
    private const double HoldSeconds = 3.8, VerdictDelaySeconds = 0.5, TieWithinPercent = 5.0;
    private static readonly int[] PanelX = [60, 1402];

    private sealed record PhoneBox(int W, int H, int Y, int[] X);

    private sealed record VerdictLine(string Kind, string Text, SKColor Color);

    public static async Task RenderAsync(VideoModel model, string outPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await Ffmpeg.EnsureAvailableAsync(ct);
        var (cw, ch) = await Ffmpeg.ProbeSizeAsync(model.Left.ClipPath, ct);
        var box = Phone(cw / (double)ch);

        var lead = Math.Min(LeadFrames, Math.Min(model.Left.Clip.T0, model.Right.Clip.T0));
        int[] done = [lead + (int)Math.Round(model.Left.Clock * Fps), lead + (int)Math.Round(model.Right.Clock * Fps)];
        var pictureDone = Math.Max(lead + model.Left.Clip.Finish - model.Left.Clip.T0, lead + model.Right.Clip.Finish - model.Right.Clip.T0);
        var verdictStart = Math.Max(Math.Max(done[0], done[1]), pictureDone) + (int)(VerdictDelaySeconds * Fps);
        var total = verdictStart + (int)(HoldSeconds * Fps);
        var slowest = Math.Max(model.Left.Clock, model.Right.Clock);

        using var type = new Typography();
        var scene = new Scene(model, box, type);
        using var background = scene.BuildBackground();

        var stderr = new StringBuilder();
        using var encoder = ProcessRunner.Start(Ffmpeg.Path,
            ["-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Width}x{Height}", "-r", Fps.ToString(),
             "-i", "-", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", outPath],
            redirectStdin: true, onError: line => stderr.AppendLine(line));
        var sink = encoder.StandardInput.BaseStream;

        using var left = new PhoneReader(model.Left.ClipPath, box.W, box.H, model.Left.Clip.T0 - lead);
        using var right = new PhoneReader(model.Right.ClipPath, box.W, box.H, model.Right.Clip.T0 - lead);
        PhoneReader[] readers = [left, right];
        using var phones = new[]
        {
            new SKBitmap(new SKImageInfo(box.W, box.H, SKColorType.Bgra8888, SKAlphaType.Opaque)),
            new SKBitmap(new SKImageInfo(box.W, box.H, SKColorType.Bgra8888, SKAlphaType.Opaque)),
        }.AsDisposable();
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        for (var g = 0; g < total; g++)
        {
            ct.ThrowIfCancellationRequested();
            canvas.DrawBitmap(background, 0, 0);
            var verdictK = (float)Math.Clamp((g - verdictStart) / (0.4 * Fps), 0, 1);

            for (var s = 0; s < 2; s++)
            {
                var bytes = readers[s].Next();
                Marshal.Copy(bytes, 0, phones.Items[s].GetPixels(), bytes.Length);
                if (verdictK > 0 && !model.Verdict.Tie && (int)model.Verdict.Winner == s)
                    scene.DrawGlow(canvas, s, verdictK);
                // Until the splash has fully covered the screen (the app-open animation scales it up from the icon, so the
                // home screen shows at the edges for a moment) the phone shows its own home screen. That is personal, so
                // it is blurred unless asked otherwise.
                scene.DrawPhone(canvas, phones.Items[s], s, blur: model.BlurHome && g < lead + 12);
            }

            for (var s = 0; s < 2; s++)
            {
                scene.DrawClock(canvas, s, g, lead, done[s], slowest);
                if (verdictK > 0) scene.DrawVerdict(canvas, s, verdictK);
            }

            using var pixels = surface.PeekPixels();
            sink.Write(pixels.GetPixelSpan());
            if (g % 15 == 0) progress?.Report((double)g / total);
        }

        sink.Close();
        await encoder.WaitForExitAsync(ct);
        if (encoder.ExitCode != 0) throw new BenchException($"ffmpeg could not encode {outPath}:\n{stderr}");
        progress?.Report(1);
    }

    private static PhoneBox Phone(double aspect)
    {
        var h = 840;
        var w = (int)Math.Round(h * aspect);
        if (w > 400) { w = 400; h = (int)Math.Round(w / aspect); }
        return new PhoneBox(w, h, PanelY + (PanelH - h) / 2, [Width / 2 - 24 - w, Width / 2 + 24]);
    }

    private sealed class DisposableList(SKBitmap[] items) : IDisposable
    {
        public SKBitmap[] Items { get; } = items;
        public void Dispose() { foreach (var i in Items) i.Dispose(); }
    }

    private static DisposableList AsDisposable(this SKBitmap[] items) => new(items);

    // ---- the scene ----------------------------------------------------------------------------------------------
    private sealed class Scene(VideoModel model, PhoneBox box, Typography type)
    {
        private readonly VideoSide[] _sides = [model.Left, model.Right];
        private readonly Dictionary<int, VerdictLine[]> _verdicts = BuildVerdicts(model);

        private static SKColor A(SKColor c, int alpha) => c.WithAlpha((byte)alpha);

        public SKBitmap BuildBackground()
        {
            var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var c = new SKCanvas(bitmap);

            using (var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Height),
                       [VideoPalette.BgTop, VideoPalette.BgBottom], SKShaderTileMode.Clamp) })
                c.DrawRect(0, 0, Width, Height, paint);

            using (var paint = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(Width / 2f, 540), 750,
                       [new SKColor(22, 96, 74, 51), new SKColor(22, 96, 74, 0)], SKShaderTileMode.Clamp) })
                c.DrawRect(0, 0, Width, Height, paint);

            using var fill = new SKPaint { Color = A(SKColors.White, 16), IsAntialias = true };
            using var stroke = new SKPaint { Color = A(SKColors.White, 34), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            using var bezel = new SKPaint { Color = new SKColor(7, 21, 16), IsAntialias = true };
            using var bezelEdge = new SKPaint { Color = A(SKColors.White, 40), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            for (var s = 0; s < 2; s++)
            {
                var panel = new SKRoundRect(SKRect.Create(PanelX[s], PanelY, PanelW, PanelH), 30);
                c.DrawRoundRect(panel, fill);
                c.DrawRoundRect(panel, stroke);
                var frame = new SKRoundRect(SKRect.Create(box.X[s] - 9, box.Y - 9, box.W + 18, box.H + 18), 46);
                c.DrawRoundRect(frame, bezel);
                c.DrawRoundRect(frame, bezelEdge);
            }

            using (var badge = new SKPaint { Color = VideoPalette.Dark, IsAntialias = true })
                c.DrawCircle(Width / 2f, 570, 31, badge);
            using (var badgeEdge = new SKPaint { Color = A(SKColors.White, 46), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
                c.DrawCircle(Width / 2f, 570, 31, badgeEdge);
            Text(c, "VS", Width / 2f, Middle(570, type.Sans(700, 19)), type.Sans(700, 19), VideoPalette.Muted, SKTextAlign.Center);

            Tracked(c, model.Eyebrow, Width / 2f, 38, type.Sans(600, 21), VideoPalette.Muted, 3.2f, center: true);
            var headlineFont = type.Sans(900, 50);
            string[] names = [DisplayName(0), DisplayName(1)];
            var parts = new[] { (names[0], VideoPalette.Ink), ("   vs   ", VideoPalette.Faint), (names[1], VideoPalette.Ink) };
            var x = (Width - parts.Sum(p => headlineFont.MeasureText(p.Item1))) / 2;
            foreach (var (text, color) in parts)
            {
                Text(c, text, x, 92, headlineFont, color);
                x += headlineFont.MeasureText(text);
            }

            Text(c, model.DeviceLine, Width / 2f, 124, Fit(model.DeviceLine, 500, 22, 900), VideoPalette.Muted, SKTextAlign.Center);

            for (var s = 0; s < 2; s++) DrawCardStatic(c, s);

            Text(c, model.Footer, Width / 2f, Height - 24, Fit(model.Footer, 500, 20, Width - 120), VideoPalette.Faint, SKTextAlign.Center);
            return bitmap;
        }

        private void DrawCardStatic(SKCanvas c, int s)
        {
            var side = _sides[s];
            var px = PanelX[s] + Pad;
            var right = PanelX[s] + PanelW - Pad;
            Text(c, side.Name, px, PanelY + 78, type.Sans(900, 64), side.Color);
            Text(c, side.Sub, px, PanelY + 124, type.Sans(500, 26), VideoPalette.Muted);
            Line(c, px, PanelY + 158, right);

            if (!model.StatsMode)
            {
                Tracked(c, $"TIME TO {model.MetricTitle}", px, PanelY + 204, type.Sans(600, 18), VideoPalette.Faint, 2.6f);
                Text(c, side.Spread, px, PanelY + 800, type.Sans(500, 21), VideoPalette.Faint);
                return;
            }

            var st = side.Stats ?? throw new InvalidOperationException("Statistics mode needs statistics for both sides.");
            Tracked(c, $"THIS RUN · {model.MetricTitle}", px, PanelY + 204, type.Sans(600, 18), VideoPalette.Faint, 2.6f);
            Line(c, px, PanelY + 444, right);
            Tracked(c, $"MEDIAN OF {st.N} RUNS", px, PanelY + 484, type.Sans(600, 18), VideoPalette.Faint, 2.6f);
            var median = st.MedianSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var endX = Tabular(c, median, px, PanelY + 564, type.Numeric(76), VideoPalette.Ink);
            Text(c, "s", endX + 10, PanelY + 564, type.Sans(700, 34), VideoPalette.Muted);
            Text(c, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"95% CI {st.CiLowSeconds:0.00} to {st.CiHighSeconds:0.00} s"),
                px, PanelY + 600, type.Sans(500, 24), VideoPalette.Muted);
            Text(c, "same measure, all runs", px, PanelY + 630, type.Sans(500, 19), VideoPalette.Faint);
        }

        public void DrawPhone(SKCanvas c, SKBitmap frame, int s, bool blur = false)
        {
            c.Save();
            c.ClipRoundRect(new SKRoundRect(SKRect.Create(box.X[s], box.Y, box.W, box.H), 38), antialias: true);
            if (blur)
            {
                using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(34, 34, SKShaderTileMode.Clamp) };
                c.DrawBitmap(frame, box.X[s], box.Y, paint);
            }
            else
            {
                c.DrawBitmap(frame, box.X[s], box.Y);
            }

            c.Restore();
        }

        public void DrawGlow(SKCanvas c, int s, float k)
        {
            var color = _sides[s].Color;
            var rect = new SKRoundRect(SKRect.Create(box.X[s] - 9, box.Y - 9, box.W + 18, box.H + 18), 46);
            using var halo = new SKPaint
            {
                Color = A(color, (int)(255 * k)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 16,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 20),
            };
            using var ring = new SKPaint { Color = A(color, (int)(255 * k)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
            c.DrawRoundRect(rect, halo);
            c.DrawRoundRect(rect, ring);
        }

        public void DrawClock(SKCanvas c, int s, int g, int lead, int doneFrame, double slowest)
        {
            var side = _sides[s];
            var ox = PanelX[s];
            var finished = g >= doneFrame;
            // running: counted in video frames; finished: exactly the run's own measurement, not rounded to a frame
            var elapsed = finished ? side.Clock : Math.Max(0, (g - lead) / (double)Fps);

            var text = elapsed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var endX = Tabular(c, text, ox + Pad, PanelY + 350, type.Numeric(150), finished ? side.Color : VideoPalette.Ink);
            Text(c, "s", endX + 12, PanelY + 350, type.Sans(700, 54), VideoPalette.Muted);

            var x0 = ox + Pad;
            var x1 = ox + PanelW - Pad;
            var y = PanelY + 396;
            using var track = new SKPaint { Color = VideoPalette.Track, IsAntialias = true };
            c.DrawRoundRect(SKRect.Create(x0, y - 8, x1 - x0, 16), 8, 8, track);
            var fraction = Math.Min(1.0, elapsed / slowest);
            if (fraction <= 0) return;

            var end = x0 + (float)Math.Max(16, (x1 - x0) * fraction);
            using var fill = new SKPaint { Color = side.Color, IsAntialias = true };
            c.DrawRoundRect(SKRect.Create(x0, y - 8, end - x0, 16), 8, 8, fill);
            if (!finished) return;

            c.DrawCircle(end - 0, y, 15, fill);
            using var tick = new SKPaint
            {
                Color = VideoPalette.Dark, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4,
                StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
            };
            using var path = new SKPath();
            path.MoveTo(end - 7, y);
            path.LineTo(end - 2, y + 6);
            path.LineTo(end + 8, y - 6);
            c.DrawPath(path, tick);
        }

        public void DrawVerdict(SKCanvas c, int s, float k)
        {
            if (!_verdicts.TryGetValue(s, out var lines)) return;
            var layout = model.StatsMode ? Layout.Stats : Layout.Runs;

            c.Save();
            c.Translate(PanelX[s], PanelY);
            using var fade = new SKPaint { Color = A(SKColors.White, (int)(255 * k)) };
            c.SaveLayer(fade);
            foreach (var line in lines)
            {
                switch (line.Kind)
                {
                    case "chip":
                    case "outline":
                    {
                        var font = type.Sans(700, layout.ChipSize);
                        var w = font.MeasureText(line.Text) + 44;
                        var rect = SKRect.Create(Pad, layout.ChipTop, w, layout.ChipBottom - layout.ChipTop);
                        var radius = (layout.ChipBottom - layout.ChipTop) / 2f;
                        if (line.Kind == "chip")
                        {
                            using var paint = new SKPaint { Color = line.Color, IsAntialias = true };
                            c.DrawRoundRect(rect, radius, radius, paint);
                            Text(c, line.Text, rect.MidX, Middle(rect.MidY, font), font, VideoPalette.Dark, SKTextAlign.Center);
                        }
                        else
                        {
                            using var paint = new SKPaint { Color = line.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
                            c.DrawRoundRect(rect, radius, radius, paint);
                            Text(c, line.Text, rect.MidX, Middle(rect.MidY, font), font, line.Color, SKTextAlign.Center);
                        }

                        break;
                    }
                    case "big":
                        Text(c, line.Text, Pad, layout.BigBaseline, type.Sans(900, layout.BigSize), line.Color);
                        break;
                    case "caption":
                        Text(c, line.Text, Pad, layout.CaptionBaseline, Fit(line.Text, 500, layout.CaptionSize, PanelW - 2 * Pad), line.Color);
                        break;
                }
            }

            c.Restore();
            c.Restore();
        }

        private string DisplayName(int s)
        {
            if (_sides[0].Name != _sides[1].Name) return _sides[s].Name;
            var tail = _sides[s].Sub.Split(" · ").Last();
            return string.IsNullOrEmpty(tail) ? _sides[s].Name : $"{_sides[s].Name} {tail}";
        }

        private static Dictionary<int, VerdictLine[]> BuildVerdicts(VideoModel m)
        {
            var v = m.Verdict;
            var result = new Dictionary<int, VerdictLine[]>();
            string Name(int s) => (s == 0 ? m.Left : m.Right).Name;
            string Full(int s)
            {
                var (l, r) = (m.Left, m.Right);
                if (l.Name != r.Name) return Name(s);
                var side = s == 0 ? l : r;
                return $"{side.Name} {side.Sub.Split(" · ").Last()}";
            }

            if (v.Tie)
            {
                var why = m.StatsMode ? "no clear difference" : $"within {TieWithinPercent:0}%";
                for (var s = 0; s < 2; s++)
                    result[s] = [new("outline", "about the same", VideoPalette.Muted), new("big", "~same", VideoPalette.Ink), new("caption", why, VideoPalette.Muted)];
                return result;
            }

            var win = (int)v.Winner;
            var lose = 1 - win;
            var winColor = (win == 0 ? m.Left : m.Right).Color;
            var caption = $"less time than {Full(lose)}";
            if (v.CiLowPercent is { } lo && v.CiHighPercent is { } hi)
                caption += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" · 95% CI {lo:0} to {hi:0}%");
            result[win] =
            [
                new("chip", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v.DeltaSeconds:0.00} s sooner"), winColor),
                new("big", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v.Percent:0.0}%"), winColor),
                new("caption", caption, VideoPalette.Ink),
            ];
            result[lose] = [new("outline", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"+{v.DeltaSeconds:0.00} s later"), VideoPalette.Coral)];
            return result;
        }

        // ---- drawing helpers -----------------------------------------------------------------------------------
        private SKFont Fit(string text, int weight, float size, float maxWidth)
        {
            while (size > 10 && type.Sans(weight, size).MeasureText(text) > maxWidth) size--;
            return type.Sans(weight, size);
        }

        private static float Middle(float centerY, SKFont font)
        {
            var m = font.Metrics;
            return centerY - (m.Ascent + m.Descent) / 2;
        }

        private static void Text(SKCanvas c, string text, float x, float baseline, SKFont font, SKColor color, SKTextAlign align = SKTextAlign.Left)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            c.DrawText(text, x, baseline, align, font, paint);
        }

        private static void Tracked(SKCanvas c, string text, float x, float baseline, SKFont font, SKColor color, float tracking, bool center = false)
        {
            var widths = text.Select(ch => font.MeasureText(ch.ToString()) + tracking).ToArray();
            if (center) x -= (widths.Sum() - tracking) / 2;
            for (var i = 0; i < text.Length; i++)
            {
                Text(c, text[i].ToString(), x, baseline, font, color);
                x += widths[i];
            }
        }

        /// <summary>Digits in cells of one width, so a running clock never jitters. Returns where the text ended.</summary>
        private static float Tabular(SKCanvas c, string text, float x, float baseline, SKFont font, SKColor color)
        {
            var cell = "0123456789".Max(d => font.MeasureText(d.ToString()));
            foreach (var ch in text)
            {
                var s = ch.ToString();
                var w = font.MeasureText(s);
                if (char.IsDigit(ch)) { Text(c, s, x + (cell - w) / 2, baseline, font, color); x += cell; }
                else { Text(c, s, x, baseline, font, color); x += w; }
            }

            return x;
        }

        private static void Line(SKCanvas c, float x0, float y, float x1)
        {
            using var paint = new SKPaint { Color = VideoPalette.Divider, StrokeWidth = 2, IsAntialias = true };
            c.DrawLine(x0, y, x1, y, paint);
        }
    }

    /// <summary>Vertical positions inside a card for the verdict block.</summary>
    private sealed record Layout(int ChipTop, int ChipBottom, int BigBaseline, int CaptionBaseline, int BigSize, int CaptionSize, int ChipSize)
    {
        public static readonly Layout Runs = new(466, 520, 640, 688, 100, 28, 30);
        public static readonly Layout Stats = new(652, 702, 778, 814, 80, 24, 28);
    }
}
