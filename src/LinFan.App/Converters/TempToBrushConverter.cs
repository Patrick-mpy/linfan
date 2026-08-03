// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LinFan.App.Converters;

/// <summary>
/// Temperatur (°C, <c>double</c>) → <see cref="IBrush"/> nach Schwere: kühl = grün, warm = orange,
/// heiß = rot, kein Messwert (<c>NaN</c>/kein <c>double</c>) = gedämpft. Treibt Wert-Farbe und
/// Sparkline-Stroke im Dashboard, damit ein heißer Sensor auf einen Blick auffällt.
/// Schwellen bewusst fix (passend zu üblichen CPU/GPU-Bereichen) — später ggf. konfigurierbar.
/// </summary>
public sealed class TempToBrushConverter : IValueConverter
{
    public static readonly TempToBrushConverter Instance = new();

    /// <summary>Ab hier „warm" (orange); darunter „kühl" (grün).</summary>
    public const double WarmC = 60.0;

    /// <summary>Ab hier „heiß" (rot).</summary>
    public const double HotC = 80.0;

    // Semantische Farben (an SemanticColors angelehnt; auf hell wie dunkel lesbar).
    private static readonly IBrush Cool = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Warm = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Hot = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#71717A"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double temp || double.IsNaN(temp))
            return Unknown;
        return temp >= HotC ? Hot : temp >= WarmC ? Warm : Cool;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
