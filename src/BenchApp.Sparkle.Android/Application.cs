using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace BenchApp.Android;

[Application]
public class Application : AvaloniaAndroidApplication<BenchSparkleApp>
{
    protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
        BenchProbe.Install();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
