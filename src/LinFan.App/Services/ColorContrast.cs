// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media;

namespace LinFan.App.Services;

/// <summary>
/// Colour arithmetic for the accent colours (<see cref="AccentPalette"/>): WCAG contrast, HSL lightness, mixing and
/// "lighten/darken until it reads". Pure; alpha is ignored except where a method says otherwise.
/// </summary>
public static class ColorContrast
{
    /// <summary>Lightness step of <see cref="Fit"/> (HSL, 0..1).</summary>
    private const double FitStep = 0.02;

    /// <summary>WCAG 2 relative luminance (0 = black, 1 = white).</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio of two opaque colours, 1..21.</summary>
    public static double Ratio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The lowest contrast of <paramref name="c"/> against any of the backgrounds.</summary>
    public static double MinRatio(Color c, IEnumerable<Color> backgrounds) => backgrounds.Min(b => Ratio(c, b));

    /// <summary>The colour with its HSL lightness moved by <paramref name="delta"/> (clamped to 0..1), opaque.</summary>
    public static Color ShiftLightness(Color c, double delta)
    {
        HslColor hsl = c.ToHsl();
        return new HslColor(1, hsl.H, hsl.S, Math.Clamp(hsl.L + delta, 0, 1)).ToRgb();
    }

    /// <summary><paramref name="a"/> mixed into <paramref name="b"/> by <paramref name="amountOfA"/> (0..1), like CSS <c>color-mix</c> in sRGB.</summary>
    public static Color Mix(Color a, Color b, double amountOfA)
    {
        byte Channel(byte x, byte y) => (byte)Math.Round(x * amountOfA + y * (1 - amountOfA));
        return Color.FromRgb(Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    /// <summary>
    /// Lightens (<paramref name="lighten"/>) or darkens the colour in small HSL steps until it reaches
    /// <paramref name="min"/>:1 against every background; unchanged when it already does. Ends at white or black at the
    /// latest, so the result is only short of <paramref name="min"/> when not even those reach it.
    /// </summary>
    public static Color Fit(Color c, IReadOnlyCollection<Color> backgrounds, double min, bool lighten)
    {
        Color result = Color.FromRgb(c.R, c.G, c.B);
        for (int i = 0; i < 1 / FitStep + 1 && MinRatio(result, backgrounds) < min; i++)
            result = ShiftLightness(result, lighten ? FitStep : -FitStep);
        return result;
    }

    /// <summary>"#RRGGBB" (upper case, no alpha) - how the GUI shows and stores a colour.</summary>
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Reads a colour the user typed: six hex digits, with or without a leading '#', surrounding blanks ignored.
    /// Anything else - short forms, alpha, names - is rejected, so what is stored is always <see cref="ToHex"/>-shaped.
    /// </summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        string s = (text ?? "").Trim();
        if (s.StartsWith('#'))
            s = s[1..];
        if (s.Length != 6 || !s.All(Uri.IsHexDigit))
            return false;
        color = Color.FromRgb(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..], 16));
        return true;
    }
}
