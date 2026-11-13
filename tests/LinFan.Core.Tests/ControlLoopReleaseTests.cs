// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// A coordinator hands its fans back on firmware auto (calibration restores even the fans it never took). The curve
/// must take over again right away - not only once the temperature drifts past the hysteresis - and exactly once.
/// </summary>
public class ControlLoopReleaseTests
{
    private static AppConfig Config() => new()
    {
        FailSafeTempC = 90,
        Curves = new[]
        {
            new CurveConfig
            {
                Id = "c", Name = "c", SourceSensorIds = new[] { "t" }, HysteresisC = 2, SmoothingSeconds = 0,
                Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
            },
        },
        Fans = new[]
        {
            new FanConfig { FanId = "f", AssignedCurveId = "c" },
            new FanConfig { FanId = "g", AssignedCurveId = "c" },
        },
    };

    private static FakeHardware Hw()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 60);                      // 60 % → pwm 153, constant: within the hysteresis
        hw.AddFan("f");
        hw.AddFan("g");
        return hw;
    }

    private static int WritesOf(FakeHardware hw, string fan) => hw.Writes.Count(w => w.Fan == fan);

    [Theory]
    [InlineData(FanControlState.Identifying, FanControlState.IdentifyThrottled)]
    [InlineData(FanControlState.Coupling, FanControlState.CouplingThrottled)]
    public void ReleaseOfAllFans_RewritesEachCurveFanOnce(FanControlState target, FanControlState throttled)
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config();
        loop.Tick(config);                              // both on the curve: one write each

        loop.Suspend("f", target);
        loop.Suspend("g", throttled);
        loop.Tick(config);
        Assert.Equal(2, hw.Writes.Count);               // nothing written while suspended

        hw.RestoreDefaults();                           // the coordinator's finally …
        loop.Resume("f");                               // … then it hands every fan back
        loop.Resume("g");
        for (int i = 0; i < 3; i++)
            loop.Tick(config);

        Assert.Equal(2, WritesOf(hw, "f"));             // rewritten once, then the hysteresis holds again
        Assert.Equal(2, WritesOf(hw, "g"));
        Assert.All(hw.Writes, w => Assert.Equal(153, w.Pwm));
    }

    [Fact]
    public void ReleaseAfterCalibration_AlsoRewritesTheFansItNeverTook()
    {
        // The calibration suspends only its target but restores every fan when it ends (failed or cancelled
        // alike - there is no result that would reset the loop through a config change).
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config();
        loop.Tick(config);

        loop.Suspend("f", FanControlState.Calibrating);
        loop.Tick(config);
        Assert.Equal(1, WritesOf(hw, "f"));
        Assert.Equal(1, WritesOf(hw, "g"));             // g stays on its curve, held by the hysteresis

        hw.RestoreDefaults();
        loop.Resume("f");
        loop.Tick(config);
        loop.Tick(config);

        Assert.Equal(2, WritesOf(hw, "f"));
        Assert.Equal(2, WritesOf(hw, "g"));
    }

    [Fact]
    public void Resume_OfAFanThatWasNotSuspended_ChangesNothing()
    {
        var hw = Hw();
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config();
        loop.Tick(config);

        loop.Resume("f");
        loop.Tick(config);

        Assert.Equal(2, hw.Writes.Count);               // still held: no release happened
    }
}
