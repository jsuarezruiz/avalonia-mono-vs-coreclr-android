using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BenchApp.Controls;

/// <summary>
/// A smooth line chart for evenly spaced samples, optionally filled down to the baseline, with an optional marker
/// at a position along the x axis. Stack several in one grid (same <see cref="Maximum"/>) to overlay series.
/// Brushes are styled properties, so the look comes from styles and resources.
/// </summary>
public sealed class AreaChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<AreaChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<AreaChart, double>(nameof(Maximum), 1);

    /// <summary>Horizontal margin as a fraction of the width, so axis labels can sit under the samples.</summary>
    public static readonly StyledProperty<double> InsetProperty =
        AvaloniaProperty.Register<AreaChart, double>(nameof(Inset));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<AreaChart, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<AreaChart, double>(nameof(StrokeThickness), 2);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<AreaChart, IBrush?>(nameof(Fill));

    /// <summary>Position of the marker between the first (0) and last (1) sample; NaN hides it.</summary>
    public static readonly StyledProperty<double> MarkerPositionProperty =
        AvaloniaProperty.Register<AreaChart, double>(nameof(MarkerPosition), double.NaN);

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<AreaChart, IBrush?>(nameof(MarkerBrush));

    public static readonly StyledProperty<IBrush?> MarkerRingBrushProperty =
        AvaloniaProperty.Register<AreaChart, IBrush?>(nameof(MarkerRingBrush));

    /// <summary>When set, a dashed vertical line is drawn through the marker.</summary>
    public static readonly StyledProperty<IBrush?> MarkerLineBrushProperty =
        AvaloniaProperty.Register<AreaChart, IBrush?>(nameof(MarkerLineBrush));

    private const double VerticalPadding = 6;

    static AreaChart() => AffectsRender<AreaChart>(
        ValuesProperty, MaximumProperty, InsetProperty, StrokeProperty, StrokeThicknessProperty, FillProperty,
        MarkerPositionProperty, MarkerBrushProperty, MarkerRingBrushProperty, MarkerLineBrushProperty);

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Inset { get => GetValue(InsetProperty); set => SetValue(InsetProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double MarkerPosition { get => GetValue(MarkerPositionProperty); set => SetValue(MarkerPositionProperty, value); }
    public IBrush? MarkerBrush { get => GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }
    public IBrush? MarkerRingBrush { get => GetValue(MarkerRingBrushProperty); set => SetValue(MarkerRingBrushProperty, value); }
    public IBrush? MarkerLineBrush { get => GetValue(MarkerLineBrushProperty); set => SetValue(MarkerLineBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var values = Values;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (values is null || values.Count < 2 || Maximum <= 0 || width <= 0 || height <= 0)
            return;

        var left = width * Inset;
        var plotWidth = width - 2 * left;
        var points = new Point[values.Count];
        for (var i = 0; i < points.Length; i++)
            points[i] = new Point(left + i * plotWidth / (points.Length - 1), ToY(values[i], height));

        if (Fill is { } fill)
            context.DrawGeometry(fill, null, BuildSpline(points, height, closeToBaseline: true));

        if (Stroke is { } stroke)
        {
            var pen = new Pen(stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, BuildSpline(points, height, closeToBaseline: false));
        }

        DrawMarker(context, values, left, plotWidth, height);
    }

    private void DrawMarker(DrawingContext context, IReadOnlyList<double> values, double left, double plotWidth,
        double height)
    {
        var position = MarkerPosition;
        if (double.IsNaN(position) || MarkerBrush is not { } brush)
            return;

        var index = Math.Clamp(position, 0, 1) * (values.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = Math.Min(lower + 1, values.Count - 1);
        var value = values[lower] + (values[upper] - values[lower]) * (index - lower);
        var center = new Point(left + Math.Clamp(position, 0, 1) * plotWidth, ToY(value, height));

        if (MarkerLineBrush is { } lineBrush)
        {
            var dashed = new Pen(lineBrush, 1, new DashStyle(new double[] { 3, 3 }, 0));
            context.DrawLine(dashed, new Point(center.X, 0), new Point(center.X, height));
        }

        var ring = MarkerRingBrush is { } ringBrush ? new Pen(ringBrush, 2.5) : null;
        context.DrawEllipse(brush, ring, center, 5, 5);
    }

    private double ToY(double value, double height) =>
        height - VerticalPadding - Math.Clamp(value / Maximum, 0, 1) * (height - 2 * VerticalPadding);

    // Catmull-Rom spline through the samples, written as cubic Beziers.
    private static StreamGeometry BuildSpline(Point[] p, double height, bool closeToBaseline)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(p[0], closeToBaseline);
        for (var i = 0; i < p.Length - 1; i++)
        {
            var before = p[Math.Max(i - 1, 0)];
            var after = p[Math.Min(i + 2, p.Length - 1)];
            var c1 = new Point(p[i].X + (p[i + 1].X - before.X) / 6, p[i].Y + (p[i + 1].Y - before.Y) / 6);
            var c2 = new Point(p[i + 1].X - (after.X - p[i].X) / 6, p[i + 1].Y - (after.Y - p[i].Y) / 6);
            g.CubicBezierTo(c1, c2, p[i + 1]);
        }

        if (closeToBaseline)
        {
            g.LineTo(new Point(p[^1].X, height));
            g.LineTo(new Point(p[0].X, height));
        }

        g.EndFigure(closeToBaseline);
        return geometry;
    }
}
