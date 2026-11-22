using System.Reflection;

namespace Bench.Tests;

internal static class Fixtures
{
    public static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));

    public static byte[] Bytes(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static class Encoding
    {
        public static System.Text.Encoding UTF8 => System.Text.Encoding.UTF8;
    }
}
