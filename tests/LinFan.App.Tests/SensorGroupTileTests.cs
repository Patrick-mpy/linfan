// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// <see cref="SensorGroup"/> as an overview tile: the highest readable value, the bar on the 0-100 °C scale, the
/// subline, the hot mark and the open toggle.
/// </summary>
public sealed class SensorGroupTileTests
{
    private static SensorRow Sensor(string id, double value)
    {
        var row = new SensorRow(id, id, "°C");
        row.Update(id, value);
        return row;
    }

    [Fact]
    public void Refresh_TakesTheHighestReadableValue()
    {
        var tile = new SensorGroup("CPU", "CPU");
        tile.Sensors.Add(Sensor("a", 48.25));
        tile.Sensors.Add(Sensor("b", double.NaN));
        tile.Sensors.Add(Sensor("c", 61));

        tile.Refresh();

        Assert.Equal(61, tile.Max);
        Assert.Equal("61.0 °C", tile.MaxDisplay);
        Assert.Equal(61, tile.BarPercent);
        Assert.Equal("Höchstwert von 3 Sensoren", tile.Sub);
        Assert.False(tile.IsHot);
    }

    [Fact]
    public void OneSensor_IsNamed()
    {
        var tile = new SensorGroup("GPU", "GPU");
        tile.Sensors.Add(Sensor("edge", 40));

        tile.Refresh();

        Assert.Equal("edge", tile.Sub);
    }

    [Fact]
    public void NothingReadable_ShowsADash_AndAnEmptyBar()
    {
        var tile = new SensorGroup("CPU", "CPU");
        tile.Sensors.Add(Sensor("a", double.NaN));

        tile.Refresh();

        Assert.Equal("-", tile.MaxDisplay);
        Assert.Equal(0, tile.BarPercent);
        Assert.False(tile.IsHot);
    }

    [Fact]
    public void Hot_FromTheMark_AndTheBarStopsAt100()
    {
        var tile = new SensorGroup("CPU", "CPU");
        tile.Sensors.Add(Sensor("a", 105));

        tile.Refresh();

        Assert.True(tile.IsHot);
        Assert.Equal(100, tile.BarPercent);
    }

    [Fact]
    public void Toggle_OpensAndReports()
    {
        SensorGroup? reported = null;
        var tile = new SensorGroup("CPU", "CPU", t => reported = t);

        tile.ToggleCommand.Execute(null);

        Assert.True(tile.IsOpen);
        Assert.Same(tile, reported);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData(" CPU ", "CPU")]
    public void KeyOf_TrimsAndMapsNoGroupToEmpty(string? group, string key) => Assert.Equal(key, SensorGroup.KeyOf(group));
}
