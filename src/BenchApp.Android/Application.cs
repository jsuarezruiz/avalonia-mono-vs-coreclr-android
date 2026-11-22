using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace BenchApp.Android;

[Application]
public class Application : AvaloniaAndroidApplication<App>
{
    protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
#if BENCH_PRELOAD_LIBANDROID
        // Experiment only: load libandroid on this thread before Avalonia starts its own threads.
        System.Runtime.InteropServices.NativeLibrary.TryLoad("libandroid.so", out _);
        System.Runtime.InteropServices.NativeLibrary.TryLoad("android", out _);
#endif
        BenchProbe.Install();
        BenchApp.Diagnostics.StartupMarks.Marked += BenchProbe.Mark;
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        BenchProbe.Mark("customize_app_builder");
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .AfterSetup(_ => BenchProbe.Mark("after_setup"));
    }
}
