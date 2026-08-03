// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class CalibrationServiceTests
{
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static FakeHardware FanRig(double temp, Func<byte, int> rpmForPwm, bool canControl = true)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", temp);
        hw.AddFanSensor("hwmon7/fan1", 0);
        hw.AddFan("hwmon7/pwm1", canControl, tachId: "hwmon7/fan1");
        hw.TachId = "hwmon7/fan1";
        hw.RpmForPwm = rpmForPwm;
        return hw;
    }

    [Fact]
    public async Task Calibrate_DetectsStartPwm_AndRpmRange_AndRestores()
    {
        var hw = FanRig(40, pwm => pwm < 96 ? 0 : 300 + pwm * 4);   // läuft ab pwm=96 an
        var svc = new CalibrationService(hw, hw, NoDelay);

        var result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { StepSize = 32 });

        Assert.Equal((byte)96, result.StartPwm);
        Assert.True(result.MaxRpm > result.MinRpm);
        Assert.True(result.MinRpm >= 100);                          // Schwellwert
        Assert.True(hw.RestoreCount >= 1);                          // Fail-Safe nach der Rampe
    }

    [Fact]
    public async Task Calibrate_OverTemperature_Aborts_AndRestores()
    {
        var hw = FanRig(95, _ => 1000);                             // Temperatur schon über Limit
        var svc = new CalibrationService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<OverTemperatureException>(() =>
            svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { FailSafeTempC = 90 }));

        Assert.True(hw.RestoreCount >= 1);
    }

    private sealed class SyncProgress : IProgress<CalibrationProgress>
    {
        public List<CalibrationProgress> Reports { get; } = new();
        public void Report(CalibrationProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Calibrate_ReportsProgress_PerStep()
    {
        var hw = FanRig(40, pwm => pwm < 96 ? 0 : 300 + pwm * 4);
        var svc = new CalibrationService(hw, hw, NoDelay);
        var progress = new SyncProgress();

        await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { StepSize = 32 },
            progress: progress);

        Assert.NotEmpty(progress.Reports);                       // pro Stufe eine Meldung
        Assert.All(progress.Reports, r => Assert.InRange(r.Pwm, 0, 255));
    }

    [Fact]
    public async Task Calibrate_NoReadableTemperature_Aborts_AndRestores()
    {
        var hw = FanRig(double.NaN, _ => 1000);                    // Temp-Sensor liefert durchgängig NaN
        var svc = new CalibrationService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<NoTemperatureReadingException>(() =>
            svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { StepSize = 32 }));

        Assert.True(hw.RestoreCount >= 1);                          // keine Rampe ohne Watchdog
    }

    [Fact]
    public async Task Calibrate_NotControllable_Throws()
    {
        var hw = FanRig(40, _ => 0, canControl: false);
        var svc = new CalibrationService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<FanNotControllableException>(() =>
            svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions()));
    }

    [Fact]
    public async Task Calibrate_TachometerOverride_MeasuresRpm_WhenBackendHasNoTach()
    {
        // Lüfter ohne Backend-Tacho, aber ein separater RPM-Sensor + explizites Override (manuelle/auto Kopplung).
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 40);
        hw.AddFanSensor("hwmon7/fan9", 0);
        hw.AddFan("hwmon7/pwm1", canControl: true, tachId: null);
        hw.TachId = "hwmon7/fan9";
        hw.RpmForPwm = pwm => pwm < 96 ? 0 : 300 + pwm * 4;
        var svc = new CalibrationService(hw, hw, NoDelay);

        var result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"),
            new CalibrationOptions { StepSize = 32, TachometerOverride = new SensorId("hwmon7/fan9") });

        Assert.Equal((byte)96, result.StartPwm);                    // Override liefert das RPM-Feedback
        Assert.True(result.MaxRpm > 0);
    }

    [Fact]
    public async Task Calibrate_NoBackendTach_NoOverride_ThrowsNoTach()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 40);
        hw.AddFan("hwmon7/pwm1", canControl: true, tachId: null);   // kein Tacho, kein Override
        var svc = new CalibrationService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<NoTachometerException>(() =>
            svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions()));
    }
}
