// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Media;
using LinFan.App.Converters;

namespace LinFan.App.Tests;

/// <summary>
/// Tests für <see cref="BoolToBrushConverter"/> (Aktiv-Badge der Kurvenliste): true/false → die jeweils
/// erste/zweite Farbe aus dem <c>ConverterParameter</c>; fehlerhafte Parameter → null statt Crash.
/// </summary>
public sealed class BoolToBrushConverterTests
{
    private static readonly BoolToBrushConverter Conv = BoolToBrushConverter.Instance;

    private static Color? ConvertColor(object? value, string? parameter)
    {
        object? result = Conv.Convert(value, typeof(IBrush), parameter, CultureInfo.InvariantCulture);
        return result is ISolidColorBrush b ? b.Color : null;
    }

    [Fact]
    public void True_UsesFirstColor() =>
        Assert.Equal(Color.Parse("#22C55E"), ConvertColor(true, "#22C55E,#52525B"));

    [Fact]
    public void False_UsesSecondColor() =>
        Assert.Equal(Color.Parse("#52525B"), ConvertColor(false, "#22C55E,#52525B"));

    [Fact]
    public void NonBoolValue_TreatedAsFalse() =>
        Assert.Equal(Color.Parse("#52525B"), ConvertColor(null, "#22C55E,#52525B"));

    [Theory]
    [InlineData("#22C55E")]            // nur eine Farbe
    [InlineData("#A,#B,#C")]           // drei Farben
    public void BadParameter_ReturnsNull(string parameter) =>
        Assert.Null(Conv.Convert(true, typeof(IBrush), parameter, CultureInfo.InvariantCulture));

    [Fact]
    public void NonStringParameter_ReturnsNull() =>
        Assert.Null(Conv.Convert(true, typeof(IBrush), parameter: 42, CultureInfo.InvariantCulture));
}
