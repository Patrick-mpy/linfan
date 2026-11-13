// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// <see cref="OwnNames"/>: a name that is just the hardware label goes, so the label follows the hardware - an own name
/// stays, also the label of hardware that has been replaced (it cannot be told from one the user chose).
/// </summary>
public sealed class OwnNamesTests
{
    private static readonly SensorDescriptor[] Sensors =
    [
        new(new SensorId("/gpu-amd/0/temperature/0"), "AMD Radeon RX 9070 GPU Core", SensorKind.Temperature, "°C", "lhm"),
        new(new SensorId("k10temp/temp1"), "k10temp Tctl", SensorKind.Temperature, "°C", "hwmon"),
    ];

    private static readonly FanDescriptor[] Fans =
    [
        new(new FanId("nct6798/pwm2"), "nct6798 pwm2", CanControl: true, Tachometer: null, Source: "hwmon"),
    ];

    private static AppConfig With(SensorConfig[]? sensors = null, FanConfig[]? fans = null) => AppConfig.Empty with
    {
        Sensors = sensors ?? [],
        Fans = fans ?? [],
    };

    [Fact]
    public void ANameThatIsTheCurrentLabel_IsCleared_OthersStay()
    {
        AppConfig config = With(sensors:
        [
            new SensorConfig { SensorId = "k10temp/temp1", Name = " k10temp Tctl ", Group = "CPU" },
            new SensorConfig { SensorId = "/gpu-amd/0/temperature/0", Name = "AMD Radeon RX 6600 XT GPU Core" },
            new SensorConfig { SensorId = "gone/temp1", Name = "gone temp1" },
        ]);

        AppConfig result = OwnNames.WithoutHardwareLabels(config, Sensors, Fans, out int cleared);

        Assert.Equal(1, cleared);
        Assert.Equal("", result.Sensors[0].Name);
        Assert.Equal("CPU", result.Sensors[0].Group);                            // only the name goes
        Assert.Equal("AMD Radeon RX 6600 XT GPU Core", result.Sensors[1].Name); // the old card's label: kept
        Assert.Equal("gone temp1", result.Sensors[2].Name);                      // not detected: nothing to compare with
    }

    [Theory]
    [InlineData("nct6798 pwm2")]  // the label
    [InlineData("nct6798/pwm2")]  // the raw id an older editor fell back to
    public void AFanNamedAfterItsLabelOrId_IsCleared(string frozen)
    {
        AppConfig config = With(fans: [new FanConfig { FanId = "nct6798/pwm2", Name = frozen, MinPwm = 60 }]);

        AppConfig result = OwnNames.WithoutHardwareLabels(config, Sensors, Fans, out int cleared);

        Assert.Equal(1, cleared);
        FanConfig fan = Assert.Single(result.Fans);
        Assert.Equal("", fan.Name);
        Assert.Equal(60, fan.MinPwm);
    }

    [Fact]
    public void ABackendListingAnIdTwice_AndANullNameFromAHandEdit_AreFine()
    {
        SensorDescriptor[] twice = [.. Sensors, new(new SensorId("k10temp/temp1"), "other", SensorKind.Temperature, "°C", "x")];
        AppConfig config = With(sensors:
        [
            new SensorConfig { SensorId = "k10temp/temp1", Name = "k10temp Tctl" },
            new SensorConfig { SensorId = "/gpu-amd/0/temperature/0", Name = null! },
        ]);

        AppConfig result = OwnNames.WithoutHardwareLabels(config, twice, Fans, out int cleared);

        Assert.Equal(1, cleared); // the first label per id counts
        Assert.Equal("", result.Sensors[0].Name);
        Assert.Null(result.Sensors[1].Name);
    }

    [Fact]
    public void WithNothingToClear_ReturnsTheSameConfig()
    {
        AppConfig config = With(
            sensors: [new SensorConfig { SensorId = "k10temp/temp1", Name = "CPU" }],
            fans: [new FanConfig { FanId = "nct6798/pwm2", Name = "Lüfter 1" }]);

        AppConfig result = OwnNames.WithoutHardwareLabels(config, Sensors, Fans, out int cleared);

        Assert.Equal(0, cleared);
        Assert.Same(config, result);
    }
}
