using System.Runtime.InteropServices;

namespace Bench;

/// <summary>How busy the machine running the measurements is. A busy host makes every timing unreliable.</summary>
public static class HostInfo
{
    public static int Cpus => Environment.ProcessorCount;

    /// <summary>The 1-minute load average on macOS and Linux; null where the OS has none (Windows).</summary>
    public static double? LoadAverage()
    {
        try
        {
            var loads = new double[3];
            return GetLoadAverage(loads, 3) > 0 ? loads[0] : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("libc", EntryPoint = "getloadavg")]
    private static extern int GetLoadAverage([Out] double[] loadavg, int count);
}
