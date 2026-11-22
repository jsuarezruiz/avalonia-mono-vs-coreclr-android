using System.Globalization;

namespace Bench;

/// <summary>Number formatting shared by every report, so the same value always prints the same way.</summary>
public static class Format
{
    /// <summary>
    /// printf-style fixed formatting: the exact binary value is rounded correctly, and only a true tie (exactly
    /// halfway, such as 442.5) goes to the even digit. .NET alone would round every tie away from zero.
    /// </summary>
    public static string Fixed(double v, int digits = 0)
    {
        var plain = v.ToString("F" + digits, CultureInfo.InvariantCulture);
        var exact = v.ToString("F" + (digits + 40), CultureInfo.InvariantCulture);     // exact expansion of the double
        var dot = exact.IndexOf('.');
        var tail = exact[(dot + 1 + digits)..];
        if (tail.Length == 0 || tail[0] != '5' || tail.Skip(1).Any(c => c != '0')) return plain;   // not a tie

        var kept = exact[..(digits == 0 ? dot : dot + 1 + digits)];
        var number = decimal.Parse(kept, CultureInfo.InvariantCulture);
        if (((kept[^1] - '0') & 1) != 0)
            number += (v < 0 ? -1 : 1) * (digits == 0 ? 1m : 1m / (decimal)Math.Pow(10, digits));
        return number.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    /// <summary>Like printf("%+.1f").</summary>
    public static string Signed(double v, int digits = 1)
    {
        var text = Fixed(v, digits);
        return text.StartsWith('-') ? text : "+" + text;
    }
}
