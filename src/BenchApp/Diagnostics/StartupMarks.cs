using System;

namespace BenchApp.Diagnostics;

/// <summary>
/// Platform-neutral startup checkpoints. The shared UI raises them; a platform head
/// (see BenchApp.Android) subscribes and turns them into timestamps.
/// </summary>
public static class StartupMarks
{
    /// <summary>App.OnFrameworkInitializationCompleted finished (Avalonia is up, view factory registered).</summary>
    public const string FrameworkInitialized = "framework_init";

    /// <summary>The dashboard finished its first layout pass and is attached to the visual tree.</summary>
    public const string ViewLoaded = "view_loaded";

    /// <summary>First animation-frame callback after layout, i.e. the first frame that contains the dashboard.</summary>
    public const string FirstFrame = "first_frame";

    /// <summary>The frame after <see cref="FirstFrame"/>: the dashboard has been rendered at least once.</summary>
    public const string FullyDrawn = "fully_drawn";

    public static event Action<string>? Marked;

    public static void Mark(string name) => Marked?.Invoke(name);
}
