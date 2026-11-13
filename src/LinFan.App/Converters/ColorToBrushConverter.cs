// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace LinFan.App.Converters;

/// <summary>
/// <see cref="Color"/> → <see cref="IBrush"/>, so controllers hand out colours and the brushes stay in the view
/// (the accent dots and the derived colours of the appearance page). Anything else gives no brush.
/// </summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public static readonly ColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Color color ? new ImmutableSolidColorBrush(color) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
