using System.IO.Compression;
using System.Text;

namespace Bench;

/// <summary>
/// Reads the package id out of an APK's binary AndroidManifest.xml, so a user can hand over two APKs without
/// naming their packages and without needing aapt. Only the attribute this tool needs is decoded.
/// </summary>
public static class ApkManifest
{
    public static string ReadPackageName(string apkPath)
    {
        using var zip = ZipFile.OpenRead(apkPath);
        var entry = zip.GetEntry("AndroidManifest.xml")
                    ?? throw new InvalidDataException($"{apkPath} has no AndroidManifest.xml; is it an APK?");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ReadPackageName(buffer.ToArray())
               ?? throw new InvalidDataException($"No package attribute found in the manifest of {apkPath}.");
    }

    /// <summary>The value of the manifest element's <c>package</c> attribute in Android binary XML, or null.</summary>
    public static string? ReadPackageName(byte[] data)
    {
        const ushort XmlType = 0x0003, StringPool = 0x0001, StartElement = 0x0102;
        if (data.Length < 8 || U16(data, 0) != XmlType) throw new InvalidDataException("Not Android binary XML.");

        int pos = U16(data, 2);
        string[]? strings = null;
        while (pos + 8 <= data.Length)
        {
            var type = U16(data, pos);
            var size = I32(data, pos + 4);
            if (size <= 0) break;

            if (type == StringPool)
            {
                strings = ReadStringPool(data, pos);
            }
            else if (type == StartElement && strings is not null)
            {
                var ext = pos + 16;
                var name = strings[I32(data, ext + 4)];
                if (name == "manifest")
                {
                    var attrStart = U16(data, ext + 8);
                    var attrSize = U16(data, ext + 10);
                    var attrCount = U16(data, ext + 12);
                    for (var i = 0; i < attrCount; i++)
                    {
                        var a = ext + attrStart + i * attrSize;
                        if (strings[I32(data, a + 4)] != "package") continue;
                        var raw = I32(data, a + 8);
                        if (raw >= 0) return strings[raw];
                        return data[a + 15] == 3 ? strings[I32(data, a + 16)] : null;
                    }
                }

                return null;
            }

            pos += size;
        }

        return null;
    }

    private static string[] ReadStringPool(byte[] data, int chunk)
    {
        var headerSize = U16(data, chunk + 2);
        var count = I32(data, chunk + 8);
        var flags = I32(data, chunk + 16);
        var stringsStart = I32(data, chunk + 20);
        var utf8 = (flags & 0x100) != 0;

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var offset = I32(data, chunk + headerSize + i * 4);
            var p = chunk + stringsStart + offset;
            if (utf8)
            {
                _ = ReadLength8(data, ref p);                 // length in characters
                var bytes = ReadLength8(data, ref p);         // length in bytes
                result[i] = Encoding.UTF8.GetString(data, p, bytes);
            }
            else
            {
                var chars = ReadLength16(data, ref p);
                result[i] = Encoding.Unicode.GetString(data, p, chars * 2);
            }
        }

        return result;
    }

    private static int ReadLength8(byte[] data, ref int p)
    {
        int len = data[p++];
        if ((len & 0x80) != 0) len = ((len & 0x7F) << 8) | data[p++];
        return len;
    }

    private static int ReadLength16(byte[] data, ref int p)
    {
        int len = U16(data, p);
        p += 2;
        if ((len & 0x8000) != 0)
        {
            len = ((len & 0x7FFF) << 16) | U16(data, p);
            p += 2;
        }

        return len;
    }

    private static ushort U16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
    private static int I32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
}
