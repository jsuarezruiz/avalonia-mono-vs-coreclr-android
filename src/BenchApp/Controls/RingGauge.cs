using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BenchApp.Controls;

/// <summary>
/// A 270 degree gauge that opens at the bottom. <see cref="Value"/> is a fraction between 0 and 1. Brushes and
/// thickness are styled properties, so the look comes from styles and resources, not from this class.
/// </summary>
public sealed class RingGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Value));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(StrokeThickness), 10);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(ValueBrush));

    private const double StartAngle = 135;
    private const double SweepAngle = 270;

    static RingGauge() =>
        AffectsRender<RingGauge>(ValueProperty, StrokeThicknessProperty, TrackBrushProperty, ValueBrushProperty);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        var radius = (size - StrokeThickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        DrawArc(context, center, radius, 1, TrackBrush);
        DrawArc(context, center, radius, Math.Clamp(Value, 0, 1), ValueBrush);
    }

    private void DrawArc(DrawingContext context, Point center, double radius, double fraction, IBrush? brush)
    {
        if (brush is null || fraction <= 0)
            return;

        var sweep = SweepAngle * fraction;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(Polar(center, radius, StartAngle), false);
            g.ArcTo(Polar(center, radius, StartAngle + sweep), new Size(radius, radius), 0, sweep > 180,
                SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round), geometry);
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
