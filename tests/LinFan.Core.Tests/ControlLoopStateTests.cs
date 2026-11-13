// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// The per-fan control state and the fail-safe state a tick reports (<see cref="FanAction.State"/>,
/// <see cref="ControlTick.FailSafe"/>, <see cref="ControlTick.ControlOf"/>). The GUI shows these 1:1 as
/// "controlled by", so each case pins what the loop really did - and that recording changes nothing it writes.
/// </summary>
public class ControlLoopStateTests
{
    private static CurveConfig Curve(string id = "c", string source = "t", bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        SourceSensorIds = new[] { source },
        HysteresisC = 2,
        SmoothingSeconds = 0,
        Enabled = enabled,
        Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
    };

    private static AppConfig Config(CurveConfig[] curves, params FanConfig[] fans) =>
        new() { FailSafeTempC = 90, Curves = curves, Fans = fans };

    private static FanConfig Fan(string id, string? curveId = "c") => new() { FanId = id, AssignedCurveId = curveId };

    /// <summary>Temperature "t" (the curve source) plus the given controllable fans.</summary>
    private static FakeHardware Hw(double temp, params string[] fans)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", temp);
        foreach (string fan in fans)
            hw.AddFan(fan, canControl: true);
        return hw;
    }

    private static FanAction ActionOf(ControlTick tick, string fanId) => Assert.Single(tick.Actions, a => a.FanId == fanId);

    [Fact]
    public void Curve_Written_ReportsCurveWithItsId()
    {
        var hw = Hw(55, "f");
        var tick = new ControlLoop(hw, hw).Tick(Config(new[] { Curve() }, Fan("f")));

        FanAction a = ActionOf(tick, "f");
        Assert.Equal(FanControlState.Curve, a.State);
        Assert.Equal("c", a.CurveId);
        Assert.Equal(FailSafeState.None, tick.FailSafe);
        Assert.Equal((FanControlState.Curve, "c"), tick.ControlOf("f", canControl: true));
    }

    [Fact]
    public void Curve_KeptByHysteresis_IsStillCurve()
    {
        var hw = Hw(55, "f");
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config(new[] { Curve() }, Fan("f"));
        loop.Tick(config);

        hw.Values["t"] = 56;                            // +1 < hysteresis 2 → kept, not rewritten
        FanAction a = ActionOf(loop.Tick(config), "f");

        Assert.Equal(FanActionKind.Held, a.Kind);
        Assert.Equal(FanControlState.Curve, a.State);
        Assert.Equal("c", a.CurveId);
    }

    [Fact]
    public void DryRun_HasNoStateOfItsOwn_CurveMeansWouldDrive()
    {
        var hw = Hw(55, "f");
        FanAction a = ActionOf(new ControlLoop(hw, hw, dryRun: true).Tick(Config(new[] { Curve() }, Fan("f"))), "f");

        Assert.Equal(FanActionKind.DryRun, a.Kind);
        Assert.Equal(FanControlState.Curve, a.State);
        Assert.Equal("c", a.CurveId);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void UnreadableSource_HoldsLastValue_NotHardwareAuto()
    {
        // The curve's only source is gone, another sensor keeps the watchdog seeing: the loop skips the fan, it
        // stays at its last PWM - neither firmware auto nor a fail-safe.
        var hw = Hw(55, "f");
        hw.AddTempSensor("cpu", 50);
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config(new[] { Curve() }, Fan("f"));
        loop.Tick(config);
        int writes = hw.Writes.Count;

        hw.Values["t"] = double.NaN;
        for (int i = 0; i < 5; i++)
        {
            ControlTick tick = loop.Tick(config);

            FanAction a = ActionOf(tick, "f");
            Assert.Equal(FanControlState.CurveHolding, a.State);
            Assert.Equal("c", a.CurveId);
            Assert.Equal(FailSafeState.None, tick.FailSafe);
        }
        Assert.Equal(writes, hw.Writes.Count);
        Assert.Empty(hw.ModeWrites);
        Assert.Equal(0, hw.RestoreCount);
    }

    [Fact]
    public void Manual_ReportsManual_WithoutCurve()
    {
        var hw = Hw(55, "f");
        var loop = new ControlLoop(hw, hw);
        loop.SetManualOverride("f", 200);

        FanAction a = ActionOf(loop.Tick(Config(new[] { Curve() }, Fan("f"))), "f");

        Assert.Equal(FanControlState.Manual, a.State);
        Assert.Null(a.CurveId);
    }

    [Fact]
    public void Calibration_MarksOnlyTheTarget_OthersStayOnTheirCurve()
    {
        var hw = Hw(55, "f", "g");
        var loop = new ControlLoop(hw, hw);
        loop.Suspend("f", FanControlState.Calibrating);

        ControlTick tick = loop.Tick(Config(new[] { Curve() }, Fan("f"), Fan("g")));

        Assert.Equal(FanControlState.Calibrating, ActionOf(tick, "f").State);
        Assert.Null(ActionOf(tick, "f").CurveId);
        Assert.Equal(FanControlState.Curve, ActionOf(tick, "g").State);
        Assert.DoesNotContain(hw.Writes, w => w.Fan == "f"); // the calibration drives it, not the loop
    }

    [Theory]
    [InlineData(FanControlState.Identifying, FanControlState.IdentifyThrottled)]
    [InlineData(FanControlState.Coupling, FanControlState.CouplingThrottled)]
    public void TargetAndThrottledFans_ReportTheirOwnersState(FanControlState target, FanControlState throttled)
    {
        // Identify and coupling take every controllable fan, also one without a config entry ("h").
        var hw = Hw(55, "f", "g", "h");
        var loop = new ControlLoop(hw, hw);
        loop.Suspend("f", target);
        loop.Suspend("g", throttled);
        loop.Suspend("h", throttled);

        ControlTick tick = loop.Tick(Config(new[] { Curve() }, Fan("f"), Fan("g")));

        Assert.Equal(target, ActionOf(tick, "f").State);
        Assert.Equal(throttled, ActionOf(tick, "g").State);
        Assert.Equal(throttled, ActionOf(tick, "h").State);
        Assert.Equal((throttled, (string?)null), tick.ControlOf("h", canControl: true));
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void Suspension_WinsOverManual_AndResumeReturnsToTheCurve()
    {
        var hw = Hw(55, "f");
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config(new[] { Curve() }, Fan("f"));
        loop.SetManualOverride("f", 200);
        loop.Suspend("f", FanControlState.Identifying);

        Assert.Equal(FanControlState.Identifying, ActionOf(loop.Tick(config), "f").State);

        loop.Resume("f");
        loop.SetManualOverride("f", null);
        Assert.Equal(FanControlState.Curve, ActionOf(loop.Tick(config), "f").State);
    }

    [Fact]
    public void NoCurve_OrUnknownCurve_IsHardwareAuto_WithoutCurveId()
    {
        var hw = Hw(55, "f", "g");
        ControlTick tick = new ControlLoop(hw, hw).Tick(Config(new[] { Curve() }, Fan("f", null), Fan("g", "gone")));

        Assert.Equal((FanControlState.HardwareAuto, (string?)null), (ActionOf(tick, "f").State, ActionOf(tick, "f").CurveId));
        Assert.Equal((FanControlState.HardwareAuto, (string?)null), (ActionOf(tick, "g").State, ActionOf(tick, "g").CurveId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledCurve_IsHardwareAuto_NamingTheCurve(bool dryRun)
    {
        var hw = Hw(55, "f");
        FanAction a = ActionOf(
            new ControlLoop(hw, hw, dryRun).Tick(Config(new[] { Curve(enabled: false) }, Fan("f"))), "f");

        Assert.Equal(FanControlState.HardwareAuto, a.State);
        Assert.Equal("c", a.CurveId);               // tells "curve switched off" from "no curve"
    }

    [Fact]
    public void ReadOnlyFan_WithoutCurve_IsNotControllable()
    {
        var hw = Hw(55);
        hw.AddFan("ro", canControl: false);

        FanAction a = ActionOf(new ControlLoop(hw, hw).Tick(Config(new[] { Curve() }, Fan("ro", null))), "ro");

        Assert.Equal(FanControlState.NotControllable, a.State);
    }

    [Fact]
    public void FailedWrite_ReportsFailed_WithTheCurve_ButReadOnlyWinsInTheSnapshotView()
    {
        // The fake throws on writing a read-only channel - a curve assigned to one fails every tick.
        var hw = Hw(55);
        hw.AddFan("ro", canControl: false);

        ControlTick tick = new ControlLoop(hw, hw).Tick(Config(new[] { Curve() }, Fan("ro")));

        FanAction a = ActionOf(tick, "ro");
        Assert.Equal(FanControlState.Failed, a.State);
        Assert.Equal("c", a.CurveId);
        Assert.Equal((FanControlState.Failed, "c"), tick.ControlOf("ro", canControl: true));
        Assert.Equal((FanControlState.NotControllable, (string?)null), tick.ControlOf("ro", canControl: false));
    }

    [Fact]
    public void OverTemperature_ReportsFailSafe_ForEveryFan_UntilATickReadsBelowTheLimit()
    {
        var hw = Hw(95, "f");
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config(new[] { Curve() }, Fan("f"));

        ControlTick hot = loop.Tick(config);

        Assert.True(hot.FailSafeTriggered);
        Assert.Equal(FailSafeState.OverTemperature, hot.FailSafe);
        Assert.Equal((FanControlState.FailSafe, (string?)null), hot.ControlOf("f", canControl: true));
        Assert.Equal((FanControlState.FailSafe, (string?)null), hot.ControlOf("unconfigured", canControl: true));

        hw.Values["t"] = 55;
        ControlTick cool = loop.Tick(config);

        Assert.Equal(FailSafeState.None, cool.FailSafe);
        Assert.Equal(FanControlState.Curve, ActionOf(cool, "f").State);
    }

    [Fact]
    public void OverTemperature_ThenUnreadable_HoldsTheFailSafe()
    {
        // Nothing may take the fans out of the safe state while the temperature is unknown: blind right after a trip
        // holds it at once, without counting towards a new one.
        var hw = Hw(95, "f");
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config(new[] { Curve() }, Fan("f"));
        loop.Tick(config);

        hw.Values["t"] = double.NaN;
        ControlTick tick = loop.Tick(config);

        Assert.True(tick.FailSafeTriggered);
        Assert.Equal(FailSafeState.Blind, tick.FailSafe);
        Assert.Equal((FanControlState.FailSafe, (string?)null), tick.ControlOf("f", canControl: true));
    }

    [Fact]
    public void Blind_HoldsBeforeTheTrip_ThenStaysFailSafeUntilATemperatureIsReadable()
    {
        var hw = Hw(55, "f");
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config(new[] { Curve() }, Fan("f"));
        loop.Tick(config);                              // the curve drives the fan …
        hw.Values["t"] = double.NaN;                    // … then every temperature is gone

        // Counting towards the trip: the fan holds the curve's last value, no fail-safe yet.
        for (int i = 0; i < 2; i++)
        {
            ControlTick counting = loop.Tick(config);
            Assert.Equal(FailSafeState.None, counting.FailSafe);
            Assert.Equal(FanControlState.CurveHolding, ActionOf(counting, "f").State);
        }

        ControlTick trip = loop.Tick(config);
        Assert.True(trip.FailSafeTriggered);
        Assert.Equal(FailSafeState.Blind, trip.FailSafe);
        Assert.Equal(FanControlState.FailSafe, trip.ControlOf("f", canControl: true).State);

        // Still blind: the fail-safe holds every tick - reported as such, and nothing (not even a manual value set
        // meanwhile) is written between two trips.
        loop.SetManualOverride("f", 0);
        for (int i = 0; i < 2; i++)
        {
            ControlTick held = loop.Tick(config);
            Assert.True(held.FailSafeTriggered);
            Assert.Equal(FailSafeState.Blind, held.FailSafe);
            Assert.Equal(FanControlState.FailSafe, held.ControlOf("f", canControl: true).State);
            Assert.Equal(FanControlState.FailSafe, held.ControlOf("unconfigured", canControl: true).State);
        }
        Assert.Equal(3, hw.RestoreCount);
        Assert.DoesNotContain(hw.Writes, w => w.Pwm == 0);

        hw.Values["t"] = 55;
        ControlTick readable = loop.Tick(config);
        Assert.Equal(FailSafeState.None, readable.FailSafe);
        Assert.Equal(FanControlState.Curve, ActionOf(readable, "f").State);
    }

    [Fact]
    public void Blind_AfterTheTrip_IsNotReported_WhileNothingIsControlled()
    {
        var hw = Hw(double.NaN, "f");
        var loop = new ControlLoop(hw, hw);
        for (int i = 0; i < 3; i++)
            loop.Tick(Config(new[] { Curve() }, Fan("f")));

        ControlTick tick = loop.Tick(Config(new[] { Curve() }, Fan("f", null)));

        Assert.Equal(FailSafeState.None, tick.FailSafe);
        Assert.Equal(FanControlState.HardwareAuto, ActionOf(tick, "f").State);
    }

    [Fact]
    public void ControlOf_FanTheLoopDidNotTouch_IsHardwareAuto_OrNotControllable()
    {
        var hw = Hw(55, "f");
        ControlTick tick = new ControlLoop(hw, hw).Tick(Config(new[] { Curve() }, Fan("f")));

        Assert.Equal((FanControlState.HardwareAuto, (string?)null), tick.ControlOf("unconfigured", canControl: true));
        Assert.Equal((FanControlState.NotControllable, (string?)null), tick.ControlOf("unconfigured", canControl: false));
    }
}
