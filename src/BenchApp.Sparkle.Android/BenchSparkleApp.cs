using Avalonia.Controls.ApplicationLifetimes;

namespace BenchApp.Android;

/// <summary>
/// The vignette's own <c>App</c>, plus the benchmark hooks. Subclassing keeps the copied code untouched: after the
/// vignette has set up its main view factory we wrap it, so the view it creates gets the probe attached.
/// </summary>
public sealed class BenchSparkleApp : SparkleParty.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (ApplicationLifetime is IActivityApplicationLifetime activity && activity.MainViewFactory is { } original)
        {
            activity.MainViewFactory = () =>
            {
                var view = original();
                if (view is not null)
                    FrameBench.Attach(view);
                return view!;
            };
        }

        BenchProbe.Mark("framework_init");
    }
}
