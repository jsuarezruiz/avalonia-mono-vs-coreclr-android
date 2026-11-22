using SkiaSharp;

namespace Bench;

/// <summary>
/// Picks fonts from what the machine has, in order of preference, and caches them. The clock never depends on a
/// font having tabular digits: the renderer lays digits out in fixed cells.
/// </summary>
internal sealed class Typography : IDisposable
{
    private static readonly string[] SansFamilies = ["Avenir Next", "Inter", "SF Pro Display", "Helvetica Neue", "Segoe UI", "Arial"];
    private static readonly string[] NumericFamilies = ["DIN Alternate", "Bahnschrift", "Avenir Next", "Inter", "Segoe UI", "Arial"];

    private readonly Dictionary<(bool Numeric, int Weight), SKTypeface> _faces = [];
    private readonly Dictionary<(bool Numeric, int Weight, float Size), SKFont> _fonts = [];

    public SKFont Sans(int weight, float size) => Get(false, weight, size);
    public SKFont Numeric(float size) => Get(true, 700, size);

    private SKFont Get(bool numeric, int weight, float size)
    {
        if (_fonts.TryGetValue((numeric, weight, size), out var cached)) return cached;
        if (!_faces.TryGetValue((numeric, weight), out var face))
        {
            face = Pick(numeric ? NumericFamilies : SansFamilies, weight);
            _faces[(numeric, weight)] = face;
        }

        var font = new SKFont(face, size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        _fonts[(numeric, weight, size)] = font;
        return font;
    }

    private static SKTypeface Pick(string[] families, int weight)
    {
        foreach (var family in families)
        {
            var face = SKTypeface.FromFamilyName(family, weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            // FromFamilyName silently substitutes another font when the family is missing; only accept a real match.
            if (face is not null && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase)) return face;
            face?.Dispose();
        }

        return SKTypeface.FromFamilyName(null, weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
    }

    public void Dispose()
    {
        foreach (var f in _fonts.Values) f.Dispose();
        foreach (var f in _faces.Values) f.Dispose();
    }
}
