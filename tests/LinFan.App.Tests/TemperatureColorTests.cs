// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controls;

namespace LinFan.App.Tests;

/// <summary>
/// Which colour a temperature takes (<see cref="TemperatureColor.KeyOf"/>): cool, warm from 60 °C, hot from 80 °C - both
/// marks inclusive - and no reading (NaN) the muted one. The colours themselves are per theme (SemanticColors).
/// </summary>
public sealed class TemperatureColorTests
{
    [Theory]
    [InlineData(20.0, "TempCool")]
    [InlineData(59.9, "TempCool")]
    [InlineData(60.0, "TempWarm")]
    [InlineData(79.9, "TempWarm")]
    [InlineData(80.0, "TempHot")]
    [InlineData(95.0, "TempHot")]
    [InlineData(double.NaN, "TempNone")]
    public void ATemperature_TakesTheColourOfItsLevel(double temperature, string key) =>
        Assert.Equal(key, TemperatureColor.KeyOf(temperature));
}
