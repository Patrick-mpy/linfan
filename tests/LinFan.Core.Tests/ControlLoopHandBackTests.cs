// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// <see cref="ControlLoop.HandBackToAuto"/>: a fan whose entry was removed on purpose goes back to hardware auto once in
/// the next tick, whether or not the loop drove it - unless that tick already acts on it.
/// </summary>
public class ControlLoopHandBackTests
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
        hw.AddTempSensor("t", 60);
        hw.AddFan("f");
        hw.AddFan("g");
        return hw;
    }

    [Fact]
    public void AFanTheLoopNeverDrove_GoesToAutoOnce_InTheNextTick()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);

        loop.HandBackToAuto(["g"]);
        ControlTick tick = loop.Tick(WithFans());
        loop.Tick(WithFans());
        loop.Tick(WithFans());

        Assert.Equal(FanControlState.HardwareAuto, Assert.Single(tick.Actions).State);
        Assert.Equal(("g", FanMode.Auto), Assert.Single(hw.ModeWrites));
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void AFanTheLoopDrove_IsHandedBackOnce_NotByBothPaths()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        loop.Tick(WithFans("g"));

        loop.HandBackToAuto(["g"]);
        ControlTick tick = loop.Tick(WithFans());

        Assert.Single(tick.Actions, a => a.FanId == "g");
        Assert.Equal(("g", FanMode.Auto), Assert.Single(hw.ModeWrites));
    }

    [Fact]
    public void ManualAndSuspendedFans_AndFansWithAnEntry_AreLeftToTheirOwnPath()
    {
        var hw = Hw();
        hw.AddFan("h");
        var loop = new ControlLoop(hw, hw);
        loop.SetManualOverride("f", 80);
        loop.Suspend("g", FanControlState.Calibrating);

        loop.HandBackToAuto(["f", "g", "h"]);
        ControlTick tick = loop.Tick(WithFans("h"));

        Assert.Empty(hw.ModeWrites);
        Assert.Equal(FanControlState.Manual, Assert.Single(tick.Actions, a => a.FanId == "f").State);
        Assert.Equal(FanControlState.Calibrating, Assert.Single(tick.Actions, a => a.FanId == "g").State);
        Assert.Equal(FanControlState.Curve, Assert.Single(tick.Actions, a => a.FanId == "h").State);

        loop.Resume("g");
        loop.Tick(WithFans("h"));
        Assert.Empty(hw.ModeWrites); // the mark is spent: the owner that took the fan hands it back
    }

    [Fact]
    public void InADryRun_NothingIsWritten()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw, dryRun: true);

        loop.HandBackToAuto(["g"]);
        loop.Tick(WithFans());

        Assert.Empty(hw.ModeWrites);
        Assert.Empty(hw.Writes);
        Assert.Equal(0, hw.RestoreCount);
    }
}
