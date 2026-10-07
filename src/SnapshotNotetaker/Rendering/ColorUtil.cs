using System.Globalization;
using System.Windows.Media;

namespace SnapshotNotetaker.Rendering;

public static class ColorUtil
{
    public static readonly Color Ink = Color.FromRgb(0x1F, 0x23, 0x28);
    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly object CacheLock = new();

    /// <summary>Frozen, cached brush (safe to share across threads).</summary>
    public static SolidColorBrush Brush(Color color)
    {
        lock (CacheLock)
        {
            if (BrushCache.TryGetValue(color, out var brush)) return brush;
            brush = new SolidColorBrush(color);
            brush.Freeze();
            if (BrushCache.Count < 4096) BrushCache[color] = brush;
            return brush;
        }
    }

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>WCAG relative luminance in [0,1].</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static double Contrast(double l1, double l2)
    {
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    /// <summary>White or near-black, whichever reads better on <paramref name="background"/>.</summary>
    public static Color BestTextOn(Color background)
    {
        double l = Luminance(background);
        return Contrast(1.0, l) >= Contrast(Luminance(Ink), l) ? Colors.White : Ink;
    }

    /// <summary>An outline color that separates <paramref name="fill"/> from its surroundings.</summary>
    public static Color ContrastOutline(Color fill) => Luminance(fill) < 0.45 ? Colors.White : Ink;

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().TrimStart('#');
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        if (s.Length == 6) { color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v); return true; }
        if (s.Length == 8) { color = Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v); return true; }
        return false;
    }
}
