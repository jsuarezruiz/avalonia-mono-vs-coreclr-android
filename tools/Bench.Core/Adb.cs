using System.Diagnostics;

namespace Bench;

/// <summary>A thin, typed wrapper over the adb command line, pinned to one device.</summary>
public sealed class Adb
{
    public string Path { get; }
    public string Serial { get; }

    private Adb(string path, string serial)
    {
        Path = path;
        Serial = serial;
    }

    /// <summary>
    /// Finds adb and the device to use. With several devices attached it refuses to guess: it lists them (with the
    /// AVD name for emulators, so you can tell yours from someone else's) and asks for --serial.
    /// </summary>
    public static async Task<Adb> ConnectAsync(string? serial = null, string? adbPath = null, CancellationToken ct = default)
    {
        var path = Locate(adbPath);
        var listing = await ProcessRunner.RunAsync(path, ["devices", "-l"], TimeSpan.FromSeconds(20), ct);
        var devices = AdbParsers.ParseDevices(listing.StdOut);
        var ready = devices.Where(d => d.State == "device").ToList();

        serial ??= Environment.GetEnvironmentVariable("ANDROID_SERIAL");
        if (serial is null)
        {
            if (ready.Count == 1) serial = ready[0].Serial;
            else if (ready.Count == 0)
                throw new BenchException("No device is ready. Connect one (USB debugging on) or start an emulator, then check `adb devices`.");
            else
                throw new BenchException("More than one device is attached; pick one with --serial:\n" +
                    string.Join('\n', await Task.WhenAll(ready.Select(async d => $"  {d.Serial}  {d.Model}  {await DescribeAsync(path, d.Serial, ct)}"))));
        }
        else if (ready.All(d => d.Serial != serial))
        {
            var state = devices.FirstOrDefault(d => d.Serial == serial)?.State ?? "not found";
            throw new BenchException(state == "unauthorized"
                ? $"Device {serial} is unauthorized. Unlock the phone and accept the \"Allow USB debugging?\" prompt."
                : $"Device {serial} is {state}.");
        }

        return new Adb(path, serial);
    }

    private static async Task<string> DescribeAsync(string adb, string serial, CancellationToken ct)
    {
        if (!serial.StartsWith("emulator-", StringComparison.Ordinal)) return "";
        var r = await ProcessRunner.RunAsync(adb, ["-s", serial, "emu", "avd", "name"], TimeSpan.FromSeconds(5), ct);
        var name = r.StdOut.Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(name) ? "" : $"(emulator, AVD {name})";
    }

    public static string Locate(string? explicitPath = null)
    {
        if (explicitPath is not null) return explicitPath;
        if (Environment.GetEnvironmentVariable("ADB") is { Length: > 0 } fromEnv) return fromEnv;

        var exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Android", "sdk"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Android", "Sdk"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            var candidate = System.IO.Path.Combine(root!, "platform-tools", exe);
            if (File.Exists(candidate)) return candidate;
        }

        return "adb"; // rely on PATH
    }

    public Task<ProcResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Path, new[] { "-s", Serial }.Concat(args), timeout ?? TimeSpan.FromSeconds(60), ct);

    /// <summary>Runs a shell command on the device and returns its output with line endings normalised.</summary>
    public async Task<string> ShellAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await RunAsync(["shell", command], timeout, ct);
        return r.StdOut.Replace("\r", "");
    }

    public async Task<DeviceInfo> DescribeDeviceAsync(CancellationToken ct = default)
    {
        async Task<string> Prop(string name) => (await ShellAsync($"getprop {name}", ct: ct)).Trim();
        var model = await Prop("ro.product.model");
        var name = "";
        foreach (var key in new[] { "ro.product.marketname", "ro.product.vendor.marketname", "ro.product.odm.marketname", "ro.product.system.marketname" })
            if ((name = await Prop(key)).Length > 0) break;
        var emulator = await Prop("ro.kernel.qemu") == "1" || await Prop("ro.boot.qemu") == "1"
                       || await Prop("ro.hardware") is "ranchu" or "goldfish";
        return new DeviceInfo(Serial, model, await Prop("ro.build.version.sdk"), await Prop("ro.product.cpu.abi"), emulator, name);
    }

    /// <summary>True when the screen is on (wakefulness "Awake"). A sleeping phone cannot be launched or recorded properly.</summary>
    public async Task<bool> IsAwakeAsync(CancellationToken ct = default) =>
        AdbParsers.ParseWakefulness(await ShellAsync("dumpsys power", ct: ct)) is null or "Awake";

    /// <summary>Presses the wake key. The result tells whether the phone allowed it (Xiaomi blocks it by default).</summary>
    public Task<ProcResult> WakeAsync(CancellationToken ct = default) => RunAsync(["shell", "input", "keyevent", "KEYCODE_WAKEUP"], ct: ct);

    /// <summary>True when the "Stay awake while charging" developer option is on (any of AC, USB or wireless).</summary>
    public async Task<bool> StaysAwakeWhilePluggedAsync(CancellationToken ct = default)
    {
        var value = (await ShellAsync("settings get global stay_on_while_plugged_in", ct: ct)).Trim();
        return int.TryParse(value, out var mask) && mask != 0;
    }

    public async Task InstallAsync(string apk, string packageId, CancellationToken ct = default)
    {
        await RunAsync(["uninstall", packageId], ct: ct);        // ignore "not installed"
        var r = await RunAsync(["install", "-r", apk], TimeSpan.FromMinutes(3), ct);
        if (!r.StdOut.Contains("Success", StringComparison.Ordinal))
        {
            var hint = r.StdOut.Contains("INSUFFICIENT_STORAGE", StringComparison.Ordinal)
                ? "\nThe device is nearly full. Free space or use a roomier device; each app needs roughly 30-60 MB."
                : r.StdOut.Contains("USER_RESTRICTED", StringComparison.Ordinal) || r.StdOut.Contains("INSTALL_FAILED_ABORTED", StringComparison.Ordinal)
                    ? "\nThe phone refused the install. On Xiaomi / POCO / Redmi phones turn on \"Install via USB\" and \"USB debugging (Security settings)\" in Developer options, then accept any prompt on the screen."
                    : "";
            throw new BenchException($"Installing {apk} failed:\n{r.StdOut}{r.StdErr}{hint}");
        }
    }

    public async Task<string?> ResolveLauncherActivityAsync(string packageId, CancellationToken ct = default) =>
        AdbParsers.ParseResolvedComponent(await ShellAsync(
            $"cmd package resolve-activity --brief -c android.intent.category.LAUNCHER {packageId}", ct: ct));

    public Task ForceStopAsync(string packageId, CancellationToken ct = default) => ShellAsync($"am force-stop {packageId}", ct: ct);
    /// <summary>
    /// Goes to the home screen by starting the Home activity. Unlike pressing the Home key this needs no input
    /// permission, so it also works on phones that block adb input (Xiaomi, POCO, Redmi).
    /// </summary>
    public Task HomeAsync(CancellationToken ct = default) =>
        ShellAsync("am start -a android.intent.action.MAIN -c android.intent.category.HOME", ct: ct);
    public Task ClearLogcatAsync(CancellationToken ct = default) => RunAsync(["logcat", "-c"], ct: ct);

    public async Task<string> LogcatAsync(string[] filterSpecs, CancellationToken ct = default)
    {
        var r = await RunAsync(new[] { "logcat", "-d", "-s" }.Concat(filterSpecs), ct: ct);
        return r.StdOut.Replace("\r", "");
    }

    /// <summary><c>am start -W</c>: starts the activity and waits for the system to report the launch.</summary>
    public async Task<AmStartResult> StartAndWaitAsync(string component, CancellationToken ct = default) =>
        AdbParsers.ParseAmStart(await ShellAsync($"am start -W -n {component}", TimeSpan.FromSeconds(60), ct));

    /// <summary>Starts the activity with <c>--ei bench_seconds N</c> (the frame-pacing run) without waiting for it.</summary>
    public Task StartFrameBenchAsync(string component, int seconds, CancellationToken ct = default) =>
        ShellAsync($"am start -n {component} --ei bench_seconds {seconds}", ct: ct);

    public async Task<double?> PssKbAsync(string packageId, CancellationToken ct = default) =>
        AdbParsers.ParsePss(await ShellAsync($"dumpsys meminfo {packageId}", ct: ct));

    /// <summary>The screen size in pixels, e.g. (1080, 2400).</summary>
    public async Task<(int Width, int Height)> ScreenSizeAsync(CancellationToken ct = default)
    {
        var text = await ShellAsync("wm size", ct: ct);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?<w>\d+)x(?<h>\d+)");
        return m.Success ? (int.Parse(m.Groups["w"].Value), int.Parse(m.Groups["h"].Value)) : (1080, 2400);
    }

    public Process StartScreenRecord(string size, string remotePath) =>
        ProcessRunner.Start(Path, ["-s", Serial, "shell", "screenrecord", "--size", size, "--bit-rate", "8000000",
            "--time-limit", "30", remotePath]);
}

/// <summary>An error the user can act on; the command line prints its message without a stack trace.</summary>
public sealed class BenchException(string message) : Exception(message);
