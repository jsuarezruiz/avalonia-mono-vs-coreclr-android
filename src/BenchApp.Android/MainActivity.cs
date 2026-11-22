using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace BenchApp.Android;

// Name is fixed so scripts can launch it with: am start -n <package>/bench.MainActivity
[Activity(
    Name = "bench.MainActivity",
    Label = "Avalonia Bench",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        BenchProbe.Activity = this;
        BenchProbe.Mark("activity_oncreate");
        base.OnCreate(savedInstanceState);
    }
}
