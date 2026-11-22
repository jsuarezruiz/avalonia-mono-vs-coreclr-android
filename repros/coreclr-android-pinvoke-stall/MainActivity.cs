using Android.OS;
using Android.Runtime;
using System.Runtime.InteropServices;
using Android.Util;

namespace ClrProbe;

// The same call Avalonia.Android's ChoreographerTimer makes: a background thread with a Looper P/Invokes
// AChoreographer_getInstance from the "android" library.
static class Native
{
    [DllImport("android")]
    public static extern IntPtr AChoreographer_getInstance();
}

static class Probe
{
    static TaskCompletionSource<IntPtr> Start()
    {
        var tcs = new TaskCompletionSource<IntPtr>();
        new Thread(() =>
        {
            Looper.Prepare();
            tcs.SetResult(Native.AChoreographer_getInstance());
            Looper.Loop();
        })
        { Name = "Choreographer Thread" }.Start();
        return tcs;
    }

    // Another thread blocks on the result, as Avalonia 12.1.2+ does at startup.
    public static void RunBlocking(string where)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ptr = Start().Task.Result;
        Log.Info("CLRPROBE", $"{where}: blocking wait took {sw.ElapsedMilliseconds} ms (ptr={ptr})");
    }

    // Nobody waits: the calling thread carries on, as Avalonia 12.1.1 did until a view became visible.
    public static void RunNotBlocking(string where)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = Start().Task;
        Log.Info("CLRPROBE", $"{where}: calling thread not blocked ({sw.ElapsedMilliseconds} ms)");
        task.ContinueWith(t => Log.Info("CLRPROBE", $"{where}: result arrived after {sw.ElapsedMilliseconds} ms"));
    }
}

[Application]
public class ProbeApplication : Application
{
    public ProbeApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
#if MODE_app
        Probe.RunBlocking("Application.OnCreate");
#endif
    }
}

[Activity(Name = "clrprobe.MainActivity", Label = "ClrProbe", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
#if MODE_activity
        Probe.RunBlocking("Activity.OnCreate");
#endif
#if MODE_async
        Probe.RunNotBlocking("Activity.OnCreate");
#endif
        SetContentView(Resource.Layout.activity_main);
    }
}
