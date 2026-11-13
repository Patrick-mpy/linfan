// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Tests;

/// <summary>
/// A calibration needs a speed sensor to measure with - the daemon refuses it without one, so the GUI locks it the same
/// way, and only when the daemon says which sensor it reads the fan from.
/// </summary>
public sealed class DeviceActionBlockTests
{
    private const string Version = "0.5.0+abc1234";

    private static MonitorSnapshot Snap(string? daemonVersion, params SensorReading[] sensors) =>
        new("ok", sensors, Array.Empty<FanReading>(), new AppConfig(), Connected: true, DaemonVersion: daemonVersion);

    private static FanReading Fan(string? tachometer) =>
        new("hwmon0/pwm1", "CPU", 1200, 128, CanControl: true, Tachometer: tachometer);

    private static SensorReading Rpm(string id) => new(id, id, SensorKind.FanRpm, "RPM", 1200);

    [Fact]
    public void ACalibration_NeedsASpeedSensorOfItsOwn_ThatIsDetectedRightNow()
    {
        MonitorSnapshot snapshot = Snap(Version, Rpm("hwmon0/fan1"),
            new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45));

        Assert.False(DeviceActionBlock.LacksSpeedSensor(snapshot, Fan("hwmon0/fan1")));
        Assert.True(DeviceActionBlock.LacksSpeedSensor(snapshot, Fan(null))); // none paired, or its pairing is another fan's
        Assert.True(DeviceActionBlock.LacksSpeedSensor(snapshot, Fan("hwmon0/fan2"))); // coupled to one that is gone
        Assert.True(DeviceActionBlock.LacksSpeedSensor(snapshot, Fan("hwmon0/temp1"))); // not a speed sensor
    }

    /// <summary>A daemon before 0.5 reports neither its version nor the fans' sensors: unknown, so nothing is locked.</summary>
    [Fact]
    public void AnOlderDaemon_LocksNothing()
    {
        Assert.False(DeviceActionBlock.LacksSpeedSensor(Snap(daemonVersion: null), Fan(null)));
    }

    [Fact]
    public void TheCalibrateReason_NamesTheSharedLockFirst_ThenTheMissingSensor()
    {
        MonitorSnapshot snapshot = Snap(Version, Rpm("hwmon0/fan1"));
        string offline = DeviceActionBlock.Reason(connected: false, dryRun: false, busy: false);

        Assert.Equal(offline, DeviceActionBlock.CalibrateReason(offline, snapshot, Fan(null)));
        Assert.Equal("Gesperrt - dieser Lüfter hat keinen eigenen, erkannten Drehzahl-Sensor zum Messen. Zuerst einen Tacho koppeln.",
            DeviceActionBlock.CalibrateReason("", snapshot, Fan(null)));
        Assert.Equal("", DeviceActionBlock.CalibrateReason("", snapshot, Fan("hwmon0/fan1")));
    }
}
