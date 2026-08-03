// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Media;
using LinFan.App.Converters;

namespace LinFan.App.Tests;

/// <summary>
/// Tests für <see cref="TempToBrushConverter"/> (Dashboard-Temperaturfarbe): kühl/warm/heiß an den
/// Schwellen <see cref="TempToBrushConverter.WarmC"/>/<see cref="TempToBrushConverter.HotC"/>;
/// <c>NaN</c> und Nicht-<c>double</c> → gedämpfte „unbekannt"-Farbe statt Crash.
/// </summary>
public sealed class TempToBrushConverterTests
{
    private static readonly TempToBrushConverter Conv = TempToBrushConverter.Instance;

    private static Color? ConvertColor(object? value) =>
        Conv.Convert(value, typeof(IBrush), parameter: null, CultureInfo.InvariantCulture) is ISolidColorBrush b
            ? b.Color
            : null;

    private static readonly Color Cool = Color.Parse("#22C55E");
    private static readonly Color Warm = Color.Parse("#F59E0B");
    private static readonly Color Hot = Color.Parse("#F87171");
    private static readonly Color Unknown = Color.Parse("#71717A");

    [Theory]
    [InlineData(20.0)]
    [InlineData(59.9)]
    public void BelowWarm_IsCool(double t) => Assert.Equal(Cool, ConvertColor(t));

    [Theory]
    [InlineData(60.0)]   // Schwelle inklusiv
    [InlineData(79.9)]
    public void WarmRange_IsWarm(double t) => Assert.Equal(Warm, ConvertColor(t));

    [Theory]
    [InlineData(80.0)]   // Schwelle inklusiv
    [InlineData(95.0)]
    public void AtOrAboveHot_IsHot(double t) => Assert.Equal(Hot, ConvertColor(t));

    [Fact]
    public void Nan_IsUnknown() => Assert.Equal(Unknown, ConvertColor(double.NaN));

    [Fact]
    public void NonDouble_IsUnknown() => Assert.Equal(Unknown, ConvertColor("48 °C"));
}
