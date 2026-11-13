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

    /// <summary>RPM per ramp step: the first entries are the run-out from the auto speed, then <paramref name="driven"/>.</summary>
    private static Func<byte, int> RunningOut(int[] runOut, Func<byte, int> driven)
    {
        int step = 0;
        return pwm => step < runOut.Length ? runOut[step++] : driven(pwm);
    }

    [Fact]
    public async Task Calibrate_FanRunningOutAtTheRampStart_IsNotAStartAtZero()
    {
        // Still spinning down from its auto speed for the first two steps, then standing until its real start at 96.
        var hw = FanRig(40, RunningOut([1500, 600], pwm => pwm < 96 ? 0 : 300 + pwm * 4));
        var svc = new CalibrationService(hw, hw, NoDelay);

        var result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { StepSize = 16 });

        Assert.Equal((byte)96, result.StartPwm);
        Assert.Equal(300 + 96 * 4, result.MinRpm);                 // not the 600 of the run-out
        Assert.Equal(300 + 240 * 4, result.MaxRpm);                // not the 1500 of the run-out
        Assert.Equal(16, result.Samples.Count);                    // the raw series stays complete
    }

    [Fact]
    public async Task Calibrate_FanThatOnlyRanOut_HasNoStartPoint()
    {
        // Spins down and never starts again (no tach response to the PWM): no start point, MinPwm stays untouched.
        var hw = FanRig(40, RunningOut([1500, 600], _ => 0));
        var svc = new CalibrationService(hw, hw, NoDelay);

        var result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { StepSize = 16 });

        Assert.Equal(FanCalibration.NoSafeStart, result.StartPwm);
        Assert.Equal(0, result.MinRpm);
        Assert.Equal(0, result.MaxRpm);
    }

    [Fact]
    public async Task Calibrate_FanThatNeverStands_StartsAtZero()
    {
        // A hardware floor: the fan spins at every PWM, PWM 0 included - its start point is 0.
        var hw = FanRig(40, pwm => 500 + pwm * 4);
        var svc = new CalibrationService(hw, hw, NoDelay);

        var result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { StepSize = 32 });

        Assert.Equal((byte)0, result.StartPwm);
        Assert.Equal(500, result.MinRpm);
    }

    [Fact]
    public async Task Calibrate_TachDropoutAboveTheStart_KeepsTheFirstStart()
    {
        // Starts at 64; one reading at 128 drops to 0 (a tach glitch) - the start point stays the first one.
        var hw = FanRig(40, pwm => pwm < 64 || pwm == 128 ? 0 : 300 + pwm * 4);
        var svc = new CalibrationService(hw, hw, NoDelay);

        var result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { StepSize = 32 });

        Assert.Equal((byte)64, result.StartPwm);
        Assert.Equal(300 + 64 * 4, result.MinRpm);
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
    public async Task Calibrate_AnOwnCoupling_MeasuresRpm_WhenBackendHasNoTach()
    {
        // A fan without a backend tach, but a separate speed sensor it is coupled to (by hand or measured).
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 40);
        hw.AddFanSensor("hwmon7/fan9", 0);
        hw.AddFan("hwmon7/pwm1", canControl: true, tachId: null);
        hw.TachId = "hwmon7/fan9";
        hw.RpmForPwm = pwm => pwm < 96 ? 0 : 300 + pwm * 4;
        var svc = new CalibrationService(hw, hw, NoDelay);

        var result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"),
            new CalibrationOptions
            {
                StepSize = 32,
                Couplings = [new FanConfig { FanId = "hwmon7/pwm1", RpmSource = "hwmon7/fan9" }],
            });

        Assert.Equal((byte)96, result.StartPwm);                    // the coupled sensor gives the speed
        Assert.True(result.MaxRpm > 0);
        Assert.Equal("hwmon7/fan9", result.Tachometer);
    }

    [Fact]
    public async Task Calibrate_RecordsTheSensorItMeasuredWith()
    {
        var hw = FanRig(40, pwm => pwm < 96 ? 0 : 300 + pwm * 4);
        var svc = new CalibrationService(hw, hw, NoDelay);

        FanCalibration result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions());

        Assert.Equal("hwmon7/fan1", result.Tachometer);
    }

    [Fact]
    public async Task Calibrate_WithTheDefaultOptions_StaysWithinTheConfigLimit()
    {
        // The result is saved without a limit check (daemon and CLI): the default ramp must stay within it, the run-out
        // phase included - it waits, but takes no samples.
        var hw = FanRig(40, RunningOut([1500, 600], pwm => pwm < 96 ? 0 : 300 + pwm * 4));
        var svc = new CalibrationService(hw, hw, NoDelay);

        FanCalibration result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions());

        Assert.Equal(255 / new CalibrationOptions().StepSize + 1, result.Samples.Count);
        Assert.InRange(result.Samples.Count, 1, ConfigLimits.MaxCalibrationSamples);
    }

    [Fact]
    public void DrivenSamples_LeaveOutTheRunOutBeforeTheFirstStandingSample()
    {
        CalibrationSample[] samples = [new(0, 1500), new(32, 600), new(64, 0), new(96, 99), new(128, 800), new(160, 0)];

        Assert.Equal([new(64, 0), new(96, 99), new(128, 800), new(160, 0)], CalibrationService.DrivenSamples(samples));
    }

    [Fact]
    public void DrivenSamples_OfASeriesThatStartsStanding_OrNeverStands_AreTheWholeSeries()
    {
        CalibrationSample[] standing = [new(0, 99), new(32, 0), new(64, 800)];
        CalibrationSample[] floor = [new(0, 500), new(32, 600), new(64, 800)];

        Assert.Same(standing, CalibrationService.DrivenSamples(standing));
        Assert.Same(floor, CalibrationService.DrivenSamples(floor));        // a hardware floor answers from the start
        Assert.Empty(CalibrationService.DrivenSamples([]));
    }

    [Fact]
    public async Task Calibrate_NoBackendTach_NoCoupling_ThrowsNoTach()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 40);
        hw.AddFan("hwmon7/pwm1", canControl: true, tachId: null);   // kein Tacho, kein Override
        var svc = new CalibrationService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<NoTachometerException>(() =>
            svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions()));
    }

    [Fact]
    public async Task Calibrate_APairingAnotherFanIsCoupledTo_ThrowsNoTach_WithoutAWrite()
    {
        // fan1 follows pwm1's PWM in the rig, but another fan is coupled to it: one sensor, one fan.
        var hw = FanRig(40, pwm => pwm < 96 ? 0 : 300 + pwm * 4);
        var svc = new CalibrationService(hw, hw, NoDelay);

        var ex = await Assert.ThrowsAsync<NoTachometerException>(() => svc.CalibrateAsync(new FanId("hwmon7/pwm1"),
            new CalibrationOptions { Couplings = [new FanConfig { FanId = "hwmon7/pwm2", RpmSource = "hwmon7/fan1" }] }));

        Assert.Contains("hwmon7/fan1", ex.Message);
        Assert.Empty(hw.Writes);
        Assert.Empty(hw.ModeWrites);
    }

    [Fact]
    public async Task Calibrate_ACoupledSensorThatIsNotDetected_ThrowsNoTach_InsteadOfThePairing()
    {
        var hw = FanRig(40, pwm => pwm < 96 ? 0 : 300 + pwm * 4);
        var svc = new CalibrationService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<NoTachometerException>(() => svc.CalibrateAsync(new FanId("hwmon7/pwm1"),
            new CalibrationOptions { Couplings = [new FanConfig { FanId = "hwmon7/pwm1", RpmSource = "amdgpu/fan1" }] }));

        Assert.Empty(hw.Writes);
    }

    /// <summary>
    /// A fan that runs out from its auto speed over time, keeps turning at <paramref name="holdPwm"/> once turning, but
    /// starts from standstill only at <paramref name="startPwm"/>.
    /// </summary>
    private sealed class CoastingFan(int rpm, int decayPerSecond, byte holdPwm, byte startPwm)
    {
        private byte _pwm;

        public int OnPwm(byte pwm)
        {
            _pwm = pwm;
            return Update(0);
        }

        public int Elapse(TimeSpan span) => Update(span.TotalSeconds);

        private int Update(double seconds)
        {
            bool driven = _pwm >= startPwm || (_pwm >= holdPwm && rpm >= 100);
            rpm = driven ? 300 + _pwm * 4 : Math.Max(0, rpm - (int)(decayPerSecond * seconds));
            return rpm;
        }
    }

    [Fact]
    public async Task Calibrate_LetsTheFanRunOutFirst_SoAPwmItOnlyHoldsIsNoStart()
    {
        // With the default ramp (32 steps, 2 s) a fan coasting down from 2000 RPM is still turning when PWM 64 holds it;
        // without the run-out it never stands and reads as starting at 0. It needs 5 s to stop and starts at 96.
        var fan = new CoastingFan(rpm: 2000, decayPerSecond: 400, holdPwm: 64, startPwm: 96);
        var hw = FanRig(40, fan.OnPwm);
        hw.Values["hwmon7/fan1"] = 2000;
        Task Elapse(TimeSpan span, CancellationToken _)
        {
            hw.Values["hwmon7/fan1"] = fan.Elapse(span);
            return Task.CompletedTask;
        }
        var svc = new CalibrationService(hw, hw, Elapse);

        var result = await svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions());

        Assert.Equal((byte)96, result.StartPwm);
        Assert.Equal(0, result.Samples[0].Rpm);                    // the first sample comes after the run-out
    }

    [Fact]
    public async Task Calibrate_OverTemperatureWhileTheFanRunsOut_Aborts_AndRestores()
    {
        var hw = FanRig(40, _ => 1500);                             // keeps turning: the run-out waits its full time
        hw.Values["hwmon7/fan1"] = 1500;
        int slices = 0;
        Task Heat(TimeSpan _, CancellationToken __)
        {
            if (++slices == 3)
                hw.Values["t"] = 95;
            return Task.CompletedTask;
        }
        var svc = new CalibrationService(hw, hw, Heat);

        await Assert.ThrowsAsync<OverTemperatureException>(() =>
            svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions { FailSafeTempC = 90 }));

        Assert.True(hw.RestoreCount >= 1);
        Assert.Equal(3, slices);                                    // caught at the next slice, not at the end
    }

    [Fact]
    public async Task Calibrate_CanceledWhileTheFanRunsOut_Restores()
    {
        var hw = FanRig(40, _ => 1500);                             // keeps turning: the run-out would wait its full time
        hw.Values["hwmon7/fan1"] = 1500;
        using var cts = new CancellationTokenSource();
        int slices = 0;
        Task CancelOnSecondSlice(TimeSpan _, CancellationToken ct)
        {
            if (++slices == 2)
                cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        var svc = new CalibrationService(hw, hw, CancelOnSecondSlice);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.CalibrateAsync(new FanId("hwmon7/pwm1"), new CalibrationOptions(), cts.Token));

        Assert.True(hw.RestoreCount >= 1);
        Assert.Equal(2, slices);                                    // no step of the ramp began
        Assert.Equal(new[] { ("hwmon7/pwm1", (byte)0) }, hw.Writes);
    }
}
