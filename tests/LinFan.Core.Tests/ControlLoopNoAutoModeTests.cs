// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// A channel with a writable PWM but no auto mode (no pwmN_enable, e.g. dell_smm_hwmon): setting auto always fails.
/// The loop hands back a fan it drove at full speed - once per hand-back, as RestoreDefaults does - and does not
/// write a fan it has not driven (the daemon's start-up RestoreDefaults has set such a channel to full speed already).
/// </summary>
public class ControlLoopNoAutoModeTests
{
    private static CurveConfig Curve(bool enabled) => new()
    {
        Id = "c",
        Name = "c",
        SourceSensorIds = new[] { "t" },
        SmoothingSeconds = 0,
        Enabled = enabled,
        Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
    };

    private static AppConfig WithCurve(bool enabled) => new()
    {
        FailSafeTempC = 90,
        Curves = new[] { Curve(enabled) },
        Fans = new[] { new FanConfig { FanId = "f", AssignedCurveId = "c" } },
    };

    private static FakeHardware Hw(bool autoMode)
    {
        var hw = new FakeHardware { ThrowOnSetMode = !autoMode };
        hw.AddTempSensor("t", 60);                      // 60 % → pwm 153
        hw.AddFan("f");
        return hw;
    }

    private static int FullSpeedWrites(FakeHardware hw) => hw.Writes.Count(w => w == ("f", (byte)255));

    [Fact]
    public void NeverDriven_WithoutCurve_IsNeverWritten()
    {
        var hw = Hw(autoMode: false);
        var loop = new ControlLoop(hw, hw);
        var config = new AppConfig { FailSafeTempC = 90, Fans = new[] { new FanConfig { FanId = "f" } } };

        for (int i = 0; i < 5; i++)
        {
            FanAction a = Assert.Single(loop.Tick(config).Actions);
            Assert.Equal(FanActionKind.Skipped, a.Kind);   // not Failed: no warning flood either
            Assert.Equal(FanControlState.HardwareAuto, a.State);
        }
        Assert.Empty(hw.Writes);                           // not rewritten tick after tick
    }

    [Fact]
    public void CurveSwitchedOff_HandsBackAtFullSpeedOnce_AgainAfterDrivingAgain()
    {
        var hw = Hw(autoMode: false);
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithCurve(enabled: true));
        Assert.Equal(("f", (byte)153), Assert.Single(hw.Writes));

        loop.ResetFilters();                               // as the daemon does on the config change
        FanAction handBack = Assert.Single(loop.Tick(WithCurve(enabled: false)).Actions);
        for (int i = 0; i < 3; i++)
            loop.Tick(WithCurve(enabled: false));

        Assert.Equal(FanActionKind.FullSpeed, handBack.Kind);
        Assert.Equal((FanControlState.HardwareAuto, "c"), (handBack.State, handBack.CurveId));
        Assert.Equal(1, FullSpeedWrites(hw));              // once, not every tick
        Assert.Equal(2, hw.Writes.Count);

        loop.ResetFilters();                               // driven again → handed back again
        loop.Tick(WithCurve(enabled: true));
        loop.ResetFilters();
        loop.Tick(WithCurve(enabled: false));
        loop.Tick(WithCurve(enabled: false));

        Assert.Equal(2, FullSpeedWrites(hw));
    }

    [Fact]
    public void ManualRelease_HandsBackAtFullSpeedOnce()
    {
        var hw = Hw(autoMode: false);
        var loop = new ControlLoop(hw, hw);
        var noFans = new AppConfig { FailSafeTempC = 90 };
        loop.SetManualOverride("f", 0);
        loop.Tick(noFans);

        loop.SetManualOverride("f", null);
        Assert.Equal(FanActionKind.FullSpeed, Assert.Single(loop.Tick(noFans).Actions).Kind);
        for (int i = 0; i < 3; i++)
            Assert.Empty(loop.Tick(noFans).Actions);      // no longer tracked: no retry

        Assert.Equal(new[] { ("f", (byte)0), ("f", (byte)255) }, hw.Writes);
    }

    [Fact]
    public void AfterAFailSafe_TheRestoreWasTheHandBack()
    {
        // RestoreDefaults already put the channel to full speed - the loop does not write it again.
        var hw = Hw(autoMode: false);
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithCurve(enabled: true));
        hw.Values["t"] = 95;
        Assert.True(loop.Tick(WithCurve(enabled: true)).FailSafeTriggered);

        hw.Values["t"] = 60;
        loop.Tick(WithCurve(enabled: false));

        Assert.Equal(0, FullSpeedWrites(hw));
    }

    [Fact]
    public void ChannelWithAutoMode_IsSetToAutoEveryTick_NeverToFullSpeed()
    {
        var hw = Hw(autoMode: true);
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithCurve(enabled: true));

        loop.ResetFilters();
        for (int i = 0; i < 3; i++)
            loop.Tick(WithCurve(enabled: false));

        Assert.Equal(3, hw.ModeWrites.Count(m => m == ("f", FanMode.Auto)));
        Assert.Equal(0, FullSpeedWrites(hw));
    }

    [Fact]
    public void TransientAutoFailure_GivesFullSpeed_AndRetriesAutoNextTick()
    {
        // The channel has pwm_enable; setting auto fails once (EIO). Full speed meanwhile - but the fan is not written
        // off as "no auto mode": auto is retried and taken as soon as it works.
        var hw = Hw(autoMode: true);
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithCurve(enabled: true));

        loop.ResetFilters();
        hw.FailSetModeTransiently = true;
        Assert.Equal(FanActionKind.FullSpeed, Assert.Single(loop.Tick(WithCurve(enabled: false)).Actions).Kind);
        Assert.Equal(1, FullSpeedWrites(hw));

        hw.FailSetModeTransiently = false;
        loop.Tick(WithCurve(enabled: false));
        Assert.Equal(("f", FanMode.Auto), Assert.Single(hw.ModeWrites));

        loop.Tick(WithCurve(enabled: false));
        Assert.Equal(1, FullSpeedWrites(hw));              // handed back for good now
    }

    [Fact]
    public void TransientAutoFailure_AfterManualRelease_KeepsTheFanTracked()
    {
        var hw = Hw(autoMode: true);
        var loop = new ControlLoop(hw, hw);
        var noFans = new AppConfig { FailSafeTempC = 90 };
        loop.SetManualOverride("f", 0);
        loop.Tick(noFans);

        loop.SetManualOverride("f", null);
        hw.FailSetModeTransiently = true;
        loop.Tick(noFans);                                 // full speed meanwhile
        hw.FailSetModeTransiently = false;
        loop.Tick(noFans);                                 // retried: now on auto

        Assert.Equal(new[] { ("f", (byte)0), ("f", (byte)255) }, hw.Writes);
        Assert.Equal(("f", FanMode.Auto), Assert.Single(hw.ModeWrites));
        Assert.Empty(loop.Tick(noFans).Actions);           // and left alone after that
    }

    [Fact]
    public void BlindDetection_StaysOn_WhileADrivenFanIsNotHandedBack()
    {
        // The fan was driven by a curve and has none now; handing it back keeps failing (no auto mode, full speed
        // rejected). Nothing else is controlled - the blind watchdog must still count.
        var hw = Hw(autoMode: false);
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithCurve(enabled: true));

        hw.ThrowOnSetPwm = true;
        hw.Values["t"] = double.NaN;
        var unassigned = new AppConfig { FailSafeTempC = 90, Fans = new[] { new FanConfig { FanId = "f" } } };
        Assert.Equal(FanControlState.Failed, Assert.Single(loop.Tick(unassigned).Actions).State);
        Assert.False(loop.Tick(unassigned).FailSafeTriggered);
        ControlTick tick = loop.Tick(unassigned);

        Assert.True(tick.FailSafeTriggered);
        Assert.Equal(FailSafeState.Blind, tick.FailSafe);
    }
}
