using Avalonia.Controls;
using Avalonia.Interactivity;
using BenchApp.Diagnostics;

namespace BenchApp.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        StartupMarks.Mark(StartupMarks.ViewLoaded);

        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            StartupMarks.Mark(StartupMarks.FullyDrawn);
            return;
        }

        // The first callback runs at the start of the first frame that contains the dashboard.
        // The second runs one frame later, i.e. after that frame has been rendered.
        topLevel.RequestAnimationFrame(_ =>
        {
            StartupMarks.Mark(StartupMarks.FirstFrame);
            topLevel.RequestAnimationFrame(_ => StartupMarks.Mark(StartupMarks.FullyDrawn));
        });
    }
}
