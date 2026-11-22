using Android.App;
using Android.OS;
using Android.Util;

namespace BenchApp.Android;

/// <summary>
/// Turns checkpoints into "milliseconds since this process was forked" and writes them to logcat under the
/// BENCH tag, e.g. "phase=first_frame ms=512". `bench` reads them back.
/// Compiled into every Android head (see Common.Android.props).
/// </summary>
internal static class BenchProbe
{
    private const string Tag = "BENCH";
    private static bool _installed;

    public static Activity? Activity { get; set; }

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        Mark("application_ctor");
        Info($"package={Application.Context.PackageName} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    }

    public static void Mark(string name)
    {
        var elapsedMs = SystemClock.ElapsedRealtime() - Process.StartElapsedRealtime;
        Info($"phase={name} ms={elapsedMs}");

        if (name == "fully_drawn")
            Activity?.ReportFullyDrawn();
    }

    public static void Info(string message) => Log.Info(Tag, message);
}
