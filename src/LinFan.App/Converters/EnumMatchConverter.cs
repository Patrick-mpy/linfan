// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Data.Converters;

namespace LinFan.App.Converters;

/// <summary>
/// One-Way-Converter für Sichtbarkeits-Bindings an ein Enum: liefert <c>true</c>, wenn der String-Wert
/// des gebundenen Enums in der (kommagetrennten) <c>ConverterParameter</c>-Liste enthalten ist.
/// Beispiel: <c>ConverterParameter="Calibration,ChooseProfile,Done"</c>. Bewusst rein und seiteneffektfrei,
/// damit die Logik (im Gegensatz zu reiner XAML) unit-testbar ist.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    /// <summary>Singleton für die Nutzung per <c>{x:Static}</c> in XAML (keine Resource-Registrierung nötig).</summary>
    public static readonly EnumMatchConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string set)
            return false;

        string current = value.ToString() ?? string.Empty;
        foreach (string token in set.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, current, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("EnumMatchConverter ist nur für One-Way-Bindings gedacht.");
}
