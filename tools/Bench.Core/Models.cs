namespace Bench;

/// <summary>The device a session ran on.</summary>
/// <param name="Name">The marketing name the phone reports, e.g. "POCO F5". Empty when the device does not say.</param>
public sealed record DeviceInfo(string Serial, string Model, string AndroidApi, string Abi, bool IsEmulator, string Name = "")
{
    /// <summary>What to show people: the marketing name, else "Android emulator", else the model code.</summary>
    public string DisplayName => Name.Length > 0 ? Name : IsEmulator ? "Android emulator" : Model;

    /// <summary>File-name friendly model, e.g. "sdk-gphone64-arm64".</summary>
    public string Slug => System.Text.RegularExpressions.Regex.Replace(Model, "[^A-Za-z0-9]+", "-").Trim('-');
}

/// <summary>One app to measure: an APK, its package id and (once installed) its launcher activity.</summary>
public sealed class Candidate
{
    public required string Label { get; init; }
    /// <summary>Optional second line under the label, e.g. "AOT (profiled) · .NET 10".</summary>
    public string Sub { get; init; } = "";
    public required string ApkPath { get; init; }
    public required string PackageId { get; init; }
    /// <summary>package/activity, set by installation.</summary>
    public string? Component { get; set; }
    public long ApkBytes { get; init; }
    /// <summary>Which signal tells the runner the app is done drawing; learnt during the warm-up launch.</summary>
    public MarkSource Marks { get; set; } = MarkSource.Unknown;
    /// <summary>"dashboard", "sparkle" for this repo's apps; "app" for any other APK.</summary>
    public string Scenario { get; init; } = "app";

    /// <summary>The track and variant of a repo entry such as net10/mono-jit, so rows keep the old CSV columns.</summary>
    public string Track => RepoLayout.SplitEntry(Entry)?.Track ?? "";
    public string RowVariant => RepoLayout.SplitEntry(Entry)?.Variant ?? Entry;

    private readonly string? _entry;

    /// <summary>The key this app's rows carry in the CSVs (the "track/variant" of the old scripts). Defaults to the label.</summary>
    public string Entry { get => _entry ?? Label; init => _entry = value; }
}

public enum MarkSource { Unknown, Bench, System, None }

/// <summary>One cold start. One row of the startup CSV.</summary>
public sealed class RunRow
{
    public string Scenario { get; init; } = "app";
    public string Track { get; init; } = "";
    public string Variant { get; init; } = "";
    public int Run { get; init; }
    public string Device { get; init; } = "";
    public string AndroidApi { get; init; } = "";
    public string Abi { get; init; } = "";
    public bool IsEmulator { get; init; }
    public string LaunchState { get; init; } = "COLD";
    public double? TotalTimeMs { get; init; }
    public double? WaitTimeMs { get; init; }
    public double? ApplicationCtorMs { get; init; }
    public double? FrameworkInitMs { get; init; }
    public double? ViewLoadedMs { get; init; }
    public double? FirstFrameMs { get; init; }
    /// <summary>The app's own fully_drawn mark (ms since process start), when the app logs the BENCH marks.</summary>
    public double? FullyDrawnMs { get; init; }
    public double? PssKb { get; init; }
    public long ApkBytes { get; init; }
    public int NativeLibTimeouts { get; init; }
    public double? HostLoad { get; init; }
    public int HostCpus { get; init; }
    /// <summary>Android's own "Fully drawn" time (ms since the launch request) for apps that call ReportFullyDrawn.</summary>
    public double? SystemFullyDrawnMs { get; init; }

    /// <summary>"track/variant" like the old scripts, or just the variant when there is no track.</summary>
    public string Entry => string.IsNullOrEmpty(Track) ? Variant : $"{Track}/{Variant}";
}
