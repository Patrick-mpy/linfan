// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Data.Converters;

namespace LinFan.App.Converters;

/// <summary>
/// One-Way-Converter für Sichtbarkeits-Bindings an einen String: liefert <c>true</c>, wenn der Wert ein
/// nicht-leerer (auch nicht nur Whitespace) String ist. Bewusst als eigener, unit-testbarer Converter statt
/// eines <c>ObjectConverters.IsNotNull</c>-Notnagels - leere Strings sollen <c>false</c> ergeben, nicht nur null.
/// </summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    /// <summary>Singleton für die Nutzung per <c>{x:Static}</c> in XAML (keine Resource-Registrierung nötig).</summary>
    public static readonly StringNotEmptyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("StringNotEmptyConverter ist nur für One-Way-Bindings gedacht.");
}
