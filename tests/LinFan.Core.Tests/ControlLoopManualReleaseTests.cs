// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// A fan without a config entry can still be driven manually. Once that ends it has no curve to take over, so the
/// loop must put it back on firmware auto itself - otherwise it keeps its last manual PWM.
/// </summary>
public class ControlLoopManualReleaseTests
{
    private static readonly AppConfig NoFans = new() { FailSafeTempC = 90 };

    private static (FakeHardware Hw, ControlLoop Loop) ManualUnconfiguredFan(bool dryRun = false)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 50);
        hw.AddFan("u");
        var loop = new ControlLoop(hw, hw, dryRun);
        loop.SetManualOverride("u", 200);
        loop.Tick(NoFans);
        return (hw, loop);
    }

    [Fact]
    public void Released_UnconfiguredFan_GoesBackToAuto_Once()
    {
        var (hw, loop) = ManualUnconfiguredFan();
        Assert.Equal(("u", (byte)200), Assert.Single(hw.Writes));

        loop.SetManualOverride("u", null);
        ControlTick released = loop.Tick(NoFans);

        Assert.Equal(("u", FanMode.Auto), Assert.Single(hw.ModeWrites));
        Assert.Equal(FanControlState.HardwareAuto, Assert.Single(released.Actions).State);

        ControlTick after = loop.Tick(NoFans);
        Assert.Single(hw.ModeWrites);                   // a one-off, not every tick
        Assert.Equal((FanControlState.HardwareAuto, (string?)null), after.ControlOf("u", canControl: true));
    }

    [Fact]
    public void Released_UnconfiguredFan_IsRetried_WhileNeitherAutoNorFullSpeedCanBeSet()
    {
        var (hw, loop) = ManualUnconfiguredFan();
        loop.SetManualOverride("u", null);

        hw.ThrowOnSetMode = true;
        hw.ThrowOnSetPwm = true;
        Assert.Equal(FanControlState.Failed, Assert.Single(loop.Tick(NoFans).Actions).State);

        hw.ThrowOnSetMode = false;
        hw.ThrowOnSetPwm = false;
        Assert.Equal(FanControlState.HardwareAuto, Assert.Single(loop.Tick(NoFans).Actions).State);
        Assert.Equal(("u", FanMode.Auto), Assert.Single(hw.ModeWrites));
    }

    [Fact]
    public void BlindDetection_StaysOn_WhileAReleasedFanIsStillTracked()
    {
        // No curves, so only the manual fan made the loop "controlling". Its release fails (neither auto nor full
        // speed can be set), so it may still sit at its manual PWM: the blind watchdog must keep counting.
        var hw = new FakeHardware();
        hw.AddTempSensor("t", double.NaN);
        hw.AddFan("u");
        var loop = new ControlLoop(hw, hw);
        loop.SetManualOverride("u", 0);
        Assert.False(loop.Tick(NoFans).FailSafeTriggered);         // blind tick 1 (manual)

        loop.SetManualOverride("u", null);
        hw.ThrowOnSetMode = true;
        hw.ThrowOnSetPwm = true;
        Assert.False(loop.Tick(NoFans).FailSafeTriggered);         // blind tick 2 (release fails → tracked)
        ControlTick tick = loop.Tick(NoFans);                      // blind tick 3

        Assert.True(tick.FailSafeTriggered);
        Assert.Equal(FailSafeState.Blind, tick.FailSafe);
        Assert.Equal(1, hw.RestoreCount);
    }

    [Fact]
    public void Released_UnconfiguredFan_DryRun_TouchesNoHardware()
    {
        var (hw, loop) = ManualUnconfiguredFan(dryRun: true);
        loop.SetManualOverride("u", null);

        Assert.Equal(FanControlState.HardwareAuto, Assert.Single(loop.Tick(NoFans).Actions).State);
        Assert.Empty(hw.ModeWrites);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void Released_ConfiguredFan_IsLeftToItsCurve()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 55);
        hw.AddFan("f");
        var config = new AppConfig
        {
            FailSafeTempC = 90,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c", SourceSensorIds = new[] { "t" }, SmoothingSeconds = 0,
                    Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "f", AssignedCurveId = "c" } },
        };
        var loop = new ControlLoop(hw, hw);
        loop.SetManualOverride("f", 200);
        loop.Tick(config);

        loop.SetManualOverride("f", null);
        ControlTick tick = loop.Tick(config);

        Assert.Equal(FanControlState.Curve, Assert.Single(tick.Actions).State);
        Assert.Empty(hw.ModeWrites);                    // no detour over firmware auto
    }

    [Fact]
    public void Released_WhileSuspended_IsLeftToTheOwner()
    {
        var (hw, loop) = ManualUnconfiguredFan();
        loop.Suspend("u", FanControlState.IdentifyThrottled);
        loop.SetManualOverride("u", null);

        FanAction a = Assert.Single(loop.Tick(NoFans).Actions);

        Assert.Equal(FanControlState.IdentifyThrottled, a.State);
        Assert.Empty(hw.ModeWrites);                    // the owner restores firmware auto when it ends
    }
}
