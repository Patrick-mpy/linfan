// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LinFan.App.Converters;

/// <summary>
/// <c>bool</c> → <see cref="IBrush"/>. Erwartet zwei Farben (true,false) als
/// <c>ConverterParameter</c> in der Form <c>"trueColor,falseColor"</c> — wegen der Komma-Falle in
/// <c>{Binding}</c> einfach-gequotet, z. B. <c>ConverterParameter='#22C55E,#52525B'</c>.
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string spec)
            return null;
        string[] parts = spec.Split(',');
        if (parts.Length != 2)
            return null;
        return new SolidColorBrush(Color.Parse(parts[value is true ? 0 : 1].Trim()));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
