using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BenchApp.Diagnostics;
using BenchApp.ViewModels;
using BenchApp.Views;

namespace BenchApp;

public partial class App : Application
{
    public override void Initialize()
    {
        StartupMarks.Mark("app_initialize_begin");
        AvaloniaXamlLoader.Load(this);
        StartupMarks.Mark("app_initialize_end");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new DashboardView { DataContext = DashboardViewModel.CreateSample() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new DashboardView { DataContext = DashboardViewModel.CreateSample() };
        }

        StartupMarks.Mark(StartupMarks.FrameworkInitialized);
        base.OnFrameworkInitializationCompleted();
    }
}
