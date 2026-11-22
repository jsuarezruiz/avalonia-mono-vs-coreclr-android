namespace Bench;

/// <summary>Getting apps onto the device, shared by every runner.</summary>
public static class DeviceSetup
{
    /// <summary>Installs each app (replacing any earlier install) and finds its launcher activity.</summary>
    public static async Task PrepareAsync(Adb adb, IReadOnlyList<Candidate> candidates, Action<string> log, CancellationToken ct = default)
    {
        foreach (var c in candidates)
        {
            log($"installing {c.Label} ({c.PackageId})");
            await adb.InstallAsync(c.ApkPath, c.PackageId, ct);
            c.Component = await adb.ResolveLauncherActivityAsync(c.PackageId, ct)
                          ?? throw new BenchException($"{c.PackageId} has no launcher activity, so there is nothing to start.");
        }
    }
}
