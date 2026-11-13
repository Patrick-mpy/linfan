// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// A fan the loop drove whose config entry disappears (e.g. a reload from a file without it) is no longer visited by
/// the curve step - it must be handed back once instead of keeping its last PWM.
/// </summary>
public class ControlLoopLostConfigEntryTests
{
    private static readonly CurveConfig Curve = new()
    {
        Id = "c",
        Name = "c",
        SourceSensorIds = new[] { "t" },
        SmoothingSeconds = 0,
        Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
    };

    private static AppConfig WithFans(params string[] fans) => new()
    {
        FailSafeTempC = 90,
        Curves = new[] { Curve },
        Fans = fans.Select(id => new FanConfig { FanId = id, AssignedCurveId = "c" }).ToArray(),
    };

    private static FakeHardware Hw()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 60);                      // 60 % → pwm 153
        hw.AddFan("f");
        hw.AddFan("g");
        return hw;
    }

    [Fact]
    public void DrivenFan_LeavingTheConfig_IsHandedBackOnce()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithFans("f", "g"));

        loop.ResetFilters();
        ControlTick tick = loop.Tick(WithFans("f"));
        for (int i = 0; i < 3; i++)
            loop.Tick(WithFans("f"));

        Assert.Equal(FanControlState.HardwareAuto, Assert.Single(tick.Actions, a => a.FanId == "g").State);
        Assert.Equal(("g", FanMode.Auto), Assert.Single(hw.ModeWrites));       // once, not every tick
        Assert.Equal(1, hw.Writes.Count(w => w.Fan == "g"));                    // only the curve write before
        Assert.Equal(FanControlState.Curve, Assert.Single(tick.Actions, a => a.FanId == "f").State);
    }

    [Fact]
    public void DrivenFan_LeavingTheConfig_OnAChannelWithoutAutoMode_GetsFullSpeedOnce()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithFans("g"));

        hw.ThrowOnSetMode = true;
        for (int i = 0; i < 3; i++)
            loop.Tick(WithFans());

        Assert.Equal(new[] { ("g", (byte)153), ("g", (byte)255) }, hw.Writes);
    }

    [Fact]
    public void HandBackFailing_IsRetried_UntilItWorks()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithFans("g"));

        hw.ThrowOnSetMode = true;
        hw.ThrowOnSetPwm = true;
        Assert.Equal(FanControlState.Failed, Assert.Single(loop.Tick(WithFans()).Actions).State);

        hw.ThrowOnSetMode = false;
        hw.ThrowOnSetPwm = false;
        loop.Tick(WithFans());
        loop.Tick(WithFans());

        Assert.Equal(("g", FanMode.Auto), Assert.Single(hw.ModeWrites));
    }

    [Fact]
    public void FanTheLoopNeverDrove_IsNotTouched()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);

        for (int i = 0; i < 3; i++)
            Assert.DoesNotContain(loop.Tick(WithFans("f")).Actions, a => a.FanId == "g");

        Assert.Empty(hw.ModeWrites);
        Assert.DoesNotContain(hw.Writes, w => w.Fan == "g");
    }
}
