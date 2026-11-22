using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SparkleParty.Views;

namespace BenchApp.Android;

/// <summary>
/// Startup marks plus, when launched with <c>--ei bench_seconds N</c>, a scripted frame-pacing run.
/// </summary>
/// <remarks>
/// The run visits the four effects in order. For each one it lets the screen settle, records N seconds of frame
/// intervals with nobody touching it ("idle"), then N seconds while a synthetic pointer circles the screen
/// ("touch"). Intervals come from the timestamps <c>RequestAnimationFrame</c> hands out, so they are the real
/// spacing of rendered frames, and a UI thread that cannot keep up shows as long intervals. Per phase it logs one
/// line under the BENCH tag with mean and percentile intervals, slow-frame count and GC activity.
/// </remarks>
internal sealed class FrameBench
{
    private const double SettleSeconds = 1.5;
    private const double TouchPeriodSeconds = 3.0;

    private static readonly string[] EffectNames = ["waterfall", "fireworks", "comet", "pinwheel"];

    private enum Phase
    {
        Settle,
        Idle,
        Touch,
    }

    private readonly Control _control;
    private readonly int _seconds;
    private readonly List<double> _samples = new(2048);

    private TopLevel? _top;
    private MainView? _view;
    private float _refreshHz = 60f;

    private Phase _phase;
    private int _effect;
    private TimeSpan _phaseStart;
    private TimeSpan? _last;
    private int _gc0;
    private int _gc1;
    private int _gc2;
    private long _allocated;

    private FrameBench(Control control, int seconds)
    {
        _control = control;
        _seconds = seconds;
        control.Loaded += OnLoaded;
    }

    public static void Attach(Control view)
    {
        var seconds = BenchProbe.Activity?.Intent?.GetIntExtra("bench_seconds", 0) ?? 0;
        _ = new FrameBench(view, seconds);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _control.Loaded -= OnLoaded;
        BenchProbe.Mark("view_loaded");

        _top = TopLevel.GetTopLevel(_control);
        if (_top is null)
        {
            BenchProbe.Mark("fully_drawn");
            return;
        }

        // First callback: start of the first frame that contains the screen. Second: one frame later, i.e. after
        // that frame has been rendered.
        _top.RequestAnimationFrame(_ =>
        {
            BenchProbe.Mark("first_frame");
            _top.RequestAnimationFrame(_ =>
            {
                BenchProbe.Mark("fully_drawn");

                if (_seconds > 0 && _control is MainView view)
                {
                    _view = view;
                    _refreshHz = ReadRefreshRate();
                    BenchProbe.Info($"frames_config seconds={_seconds} refresh_hz={_refreshHz}");
                    _top.RequestAnimationFrame(Begin);
                }
            });
        });
    }

    private void Begin(TimeSpan now)
    {
        Enter(Phase.Settle, now);
        _top!.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        var elapsed = (now - _phaseStart).TotalSeconds;

        if (_phase == Phase.Settle)
        {
            if (elapsed >= SettleSeconds)
                Enter(Phase.Idle, now);
        }
        else
        {
            if (_last is { } last)
                _samples.Add((now - last).TotalMilliseconds);

            if (_phase == Phase.Touch)
                _view!.Touch(TouchPoint(elapsed));

            if (elapsed >= _seconds)
            {
                Report();

                if (_phase == Phase.Idle)
                {
                    Enter(Phase.Touch, now);
                }
                else if (++_effect < EffectNames.Length)
                {
                    Enter(Phase.Settle, now);
                }
                else
                {
                    _view!.Touch(null);
                    BenchProbe.Mark("bench_done");
                    return;
                }
            }
        }

        _last = now;
        _top!.RequestAnimationFrame(OnFrame);
    }

    private void Enter(Phase phase, TimeSpan now)
    {
        _phase = phase;
        _phaseStart = now;

        if (phase == Phase.Settle)
        {
            _view!.Touch(null);
            _view.Show(_effect);
        }
        else
        {
            _samples.Clear();
            _gc0 = GC.CollectionCount(0);
            _gc1 = GC.CollectionCount(1);
            _gc2 = GC.CollectionCount(2);
            _allocated = AllocatedBytes();
        }
    }

    private Point TouchPoint(double elapsed)
    {
        var size = _control.Bounds.Size;
        var radius = Math.Min(size.Width, size.Height) * 0.25;
        var angle = elapsed / TouchPeriodSeconds * Math.PI * 2d;

        return new Point(
            (size.Width / 2d) + (Math.Cos(angle) * radius),
            (size.Height * 0.45d) + (Math.Sin(angle) * radius));
    }

    private void Report()
    {
        var count = _samples.Count;
        var name = $"effect={EffectNames[_effect]} phase={(_phase == Phase.Idle ? "idle" : "touch")}";

        if (count == 0)
        {
            BenchProbe.Info($"frames {name} n=0");
            return;
        }

        var sorted = _samples.ToArray();
        Array.Sort(sorted);

        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * count) - 1, 0, count - 1)];

        var sum = 0d;
        var slow = 0;
        var slowAbove = 1.5d * 1000d / _refreshHz;
        foreach (var ms in sorted)
        {
            sum += ms;
            if (ms > slowAbove)
                slow++;
        }

        var allocated = AllocatedBytes();
        var allocatedMb = allocated < 0 || _allocated < 0 ? -1d : (allocated - _allocated) / 1048576d;

        BenchProbe.Info(FormattableString.Invariant(
            $"frames {name} n={count} mean_ms={sum / count:F2} p50_ms={Percentile(0.50):F2} p95_ms={Percentile(0.95):F2} p99_ms={Percentile(0.99):F2} max_ms={sorted[count - 1]:F2} slow={slow} refresh_hz={_refreshHz:F1} gc0={GC.CollectionCount(0) - _gc0} gc1={GC.CollectionCount(1) - _gc1} gc2={GC.CollectionCount(2) - _gc2} alloc_mb={allocatedMb:F1}"));
    }

    private static long AllocatedBytes()
    {
        try
        {
            return GC.GetTotalAllocatedBytes(false);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static float ReadRefreshRate()
    {
#pragma warning disable CA1422, CS0618 // DefaultDisplay is deprecated on newer APIs but fine on API 24+.
        return BenchProbe.Activity?.WindowManager?.DefaultDisplay?.RefreshRate ?? 60f;
#pragma warning restore CA1422, CS0618
    }
}
