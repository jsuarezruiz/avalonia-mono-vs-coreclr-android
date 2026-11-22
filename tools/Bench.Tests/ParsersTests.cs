namespace Bench.Tests;

public class ParsersTests
{
    [Fact]
    public void Parses_am_start_output()
    {
        var r = AdbParsers.ParseAmStart(Fixtures.Text("am-start-w.txt"));
        Assert.Equal("ok", r.Status);
        Assert.Equal("COLD", r.LaunchState);
        Assert.Equal(1194, r.TotalTimeMs);
        Assert.Equal(1270, r.WaitTimeMs);
    }

    [Fact]
    public void Reads_the_apps_own_marks()
    {
        var phases = AdbParsers.ParseBenchPhases(Fixtures.Text("logcat-bench.txt"));
        Assert.Equal(348, phases["application_ctor"]);
        Assert.Equal(1199, phases["first_frame"]);
        Assert.Equal(1220, phases["fully_drawn"]);
    }

    [Fact]
    public void Reads_androids_fully_drawn_line_for_the_right_package_only()
    {
        var log = Fixtures.Text("logcat-bench.txt");
        Assert.Equal(1311, AdbParsers.ParseSystemFullyDrawn(log, "com.avaloniabench.net11.dashboard.coreclr"));
        Assert.Null(AdbParsers.ParseSystemFullyDrawn(log, "com.other.app"));
    }

    [Theory]
    [InlineData("+311ms", 311)]
    [InlineData("+1s311ms", 1311)]
    [InlineData("+2s", 2000)]
    [InlineData("+1m2s3ms", 62_003)]
    public void Parses_durations(string text, double expectedMs) =>
        Assert.Equal(expectedMs, AdbParsers.ParseDuration(text));

    [Fact]
    public void Reads_pss_from_meminfo() =>
        Assert.Equal(112639, AdbParsers.ParsePss(Fixtures.Text("meminfo.txt")));

    [Fact]
    public void Reads_the_resolved_launcher_component() =>
        Assert.Equal("com.avaloniabench.net11.dashboard.coreclr/bench.MainActivity",
            AdbParsers.ParseResolvedComponent(Fixtures.Text("resolve-activity.txt")));

    [Fact]
    public void Counts_resolver_timeouts()
    {
        const string log = "W monodroid-assembly: Timeout while waiting for shared library 'libandroid.so' to load.\n" +
                           "W monodroid-assembly: Timeout while waiting for shared library 'android' to load.\n" +
                           "I something else\n";
        Assert.Equal(2, AdbParsers.CountResolverTimeouts(log));
    }

    [Fact]
    public void Parses_the_device_list()
    {
        const string output = "List of devices attached\n" +
            "emulator-5554          device product:sdk_gphone64_arm64 model:sdk_gphone64_arm64 device:emu64a transport_id:1\n" +
            "R5CT123456             unauthorized usb:1-1 transport_id:3\n";
        var devices = AdbParsers.ParseDevices(output);
        Assert.Equal(2, devices.Count);
        Assert.Equal(("emulator-5554", "device", "sdk_gphone64_arm64"), (devices[0].Serial, devices[0].State, devices[0].Model));
        Assert.Equal("unauthorized", devices[1].State);
    }
}

public class ApkManifestTests
{
    [Fact]
    public void Reads_the_package_id_from_a_real_binary_manifest() =>
        Assert.Equal("com.avaloniabench.net11.dashboard.coreclr", ApkManifest.ReadPackageName(Fixtures.Bytes("AndroidManifest.coreclr.bin")));

    [Fact]
    public void Rejects_data_that_is_not_binary_xml() =>
        Assert.Throws<InvalidDataException>(() => ApkManifest.ReadPackageName("<manifest/>"u8.ToArray()));
}

public class CsvTests
{
    [Fact]
    public void Round_trips_rows_including_quoted_fields()
    {
        var row = new RunRow
        {
            Scenario = "app", Variant = "Mono, AOT", Run = 3, Device = "Pixel \"8\"", AndroidApi = "34", Abi = "arm64-v8a",
            TotalTimeMs = 480, FullyDrawnMs = 442.5, SystemFullyDrawnMs = 511, ApkBytes = 123, HostCpus = 16, HostLoad = 4.5,
        };
        var path = Path.GetTempFileName();
        try
        {
            StartupCsv.Write(path, [row]);
            var read = Assert.Single(StartupCsv.Read(path));
            Assert.Equal("Mono, AOT", read.Variant);
            Assert.Equal("Pixel \"8\"", read.Device);
            Assert.Equal(442.5, read.FullyDrawnMs);
            Assert.Equal(511, read.SystemFullyDrawnMs);
            Assert.Equal(16, read.HostCpus);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reads_the_csv_the_shell_scripts_wrote()
    {
        var rows = StartupCsv.Parse(Fixtures.Text("startup-sample.csv").Split('\n'));
        Assert.Equal(60, rows.Count);
        Assert.All(rows, r => Assert.Equal("COLD", r.LaunchState));
        Assert.Contains(rows, r => r.Entry == "net11/coreclr");
    }
}

public class PhonePreflightTests
{
    [Fact]
    public void Recognises_a_phone_that_blocks_input_from_adb()
    {
        const string xiaomi = "Exception occurred while executing 'keyevent':\njava.lang.SecurityException: Injecting input events requires the caller (or the source of the instrumentation, if any) to have the INJECT_EVENTS permission.";
        Assert.True(AdbParsers.InputWasRefused(xiaomi));
        Assert.False(AdbParsers.InputWasRefused(""));
    }
}
