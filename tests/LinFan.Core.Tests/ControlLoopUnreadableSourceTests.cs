// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// A curve whose sources are unreadable (e.g. a sleeping dGPU answering EIO) only holds a value it wrote itself. If it
/// has not driven the fan since the fan was last reset - a manual release, a profile switch - the fan is handed back,
/// instead of staying at whatever it had (a manual 0 included) while other sensors keep the watchdog seeing.
/// </summary>
public class ControlLoopUnreadableSourceTests
{
    private static CurveConfig Curve(string id, string source) => new()
    {
        Id = id,
        Name = id,
        SourceSensorIds = new[] { source },
        SmoothingSeconds = 0,
        Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
    };

    private static AppConfig Config(params CurveConfig[] curves) => new()
    {
        FailSafeTempC = 90,
        Curves = curves,
        Fans = new[] { new FanConfig { FanId = "f", AssignedCurveId = curves[0].Id } },
    };

    /// <summary>"cpu" stays readable (the watchdog sees), "gpu" is the dead source.</summary>
    private static FakeHardware Hw(bool autoMode = true)
    {
        var hw = new FakeHardware { ThrowOnSetMode = !autoMode };
        hw.AddTempSensor("cpu", 60);
        hw.AddTempSensor("gpu", double.NaN);
        hw.AddFan("f");
        return hw;
    }

    [Fact]
    public void ManualRelease_WithTheSourceUnreadable_HandsTheFanBack()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config(Curve("g", "gpu"));
        loop.SetManualOverride("f", 0);
        loop.Tick(config);

        loop.SetManualOverride("f", null);
        for (int i = 0; i < 3; i++)
        {
            ControlTick tick = loop.Tick(config);
            FanAction a = Assert.Single(tick.Actions);
            Assert.Equal((FanControlState.HardwareAuto, "g"), (a.State, a.CurveId)); // not holding the manual 0
            Assert.False(tick.FailSafeTriggered);                                   // the watchdog sees "cpu"
        }
        Assert.Contains(("f", FanMode.Auto), hw.ModeWrites);
        Assert.Equal(("f", (byte)0), Assert.Single(hw.Writes));
    }

    [Fact]
    public void ProfileSwitch_ToACurveWithADeadSource_HandsTheFanBack()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        loop.Tick(Config(Curve("a", "cpu")));             // curve a drives: 60 % → 153
        Assert.Equal(("f", (byte)153), Assert.Single(hw.Writes));

        loop.ResetFilters();                              // as the daemon does on the switch
        FanAction a = Assert.Single(loop.Tick(Config(Curve("b", "gpu"))).Actions);

        Assert.Equal((FanControlState.HardwareAuto, "b"), (a.State, a.CurveId)); // not curve a's value under curve b
        Assert.Equal(("f", FanMode.Auto), Assert.Single(hw.ModeWrites));
    }

    [Fact]
    public void ACurveThatDrove_KeepsHoldingItsValue_AndDrivesAgainOnceReadable()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        hw.Values["gpu"] = 60;
        AppConfig config = Config(Curve("g", "gpu"));
        loop.Tick(config);

        hw.Values["gpu"] = double.NaN;
        Assert.Equal(FanControlState.CurveHolding, Assert.Single(loop.Tick(config).Actions).State);
        Assert.Empty(hw.ModeWrites);

        hw.Values["gpu"] = 70;
        Assert.Equal(FanControlState.Curve, Assert.Single(loop.Tick(config).Actions).State);
    }

    [Fact]
    public void ManualRelease_OnAChannelWithoutAutoMode_GivesFullSpeedOnce()
    {
        var hw = Hw(autoMode: false);
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config(Curve("g", "gpu"));
        loop.SetManualOverride("f", 0);
        loop.Tick(config);

        loop.SetManualOverride("f", null);
        for (int i = 0; i < 3; i++)
            loop.Tick(config);

        Assert.Equal(new[] { ("f", (byte)0), ("f", (byte)255) }, hw.Writes);
    }
}
