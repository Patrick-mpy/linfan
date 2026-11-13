// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// The watchdog skips unreadable sensors. When the hottest one - close to the limit - turns unreadable, it would go on
/// watching only the cooler ones and miss the over-temperature it can no longer see. Such a loss counts as blind; a
/// sensor that drops out while cool (a Wi-Fi chip in power save, a dGPU suspending) does not.
/// </summary>
public class ControlLoopLostSensorTests
{
    private const double Limit = 90;

    private static AppConfig Config() => new()
    {
        FailSafeTempC = Limit,
        Curves = new[]
        {
            new CurveConfig
            {
                Id = "c",
                Name = "c",
                SourceSensorIds = new[] { "board" },
                SmoothingSeconds = 0,
                Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
            },
        },
        Fans = new[] { new FanConfig { FanId = "f", AssignedCurveId = "c" } },
    };

    private static FakeHardware Hw(double cpu)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("cpu", cpu);
        hw.AddTempSensor("board", 45);
        hw.AddFan("f");
        return hw;
    }

    [Fact]
    public void SensorLostNearTheLimit_TripsAsBlind_AndHoldsUntilItReadsAgain()
    {
        var hw = Hw(cpu: 82); // within the margin of the limit
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config();
        Assert.False(loop.Tick(config).FailSafeTriggered);

        hw.Values["cpu"] = double.NaN;
        Assert.False(loop.Tick(config).FailSafeTriggered);
        Assert.False(loop.Tick(config).FailSafeTriggered);
        ControlTick trip = loop.Tick(config);
        Assert.True(trip.FailSafeTriggered);
        Assert.Equal(FailSafeState.Blind, trip.FailSafe);
        Assert.Contains("cpu", trip.FailSafeReason);

        // Held: "board" still feeds the curve, yet nothing drives the fan again while "cpu" stays unreadable.
        int writes = hw.Writes.Count;
        int restores = hw.RestoreCount;
        for (int i = 0; i < 5; i++)
            Assert.True(loop.Tick(config).FailSafeTriggered);
        Assert.Equal(writes, hw.Writes.Count);
        Assert.Equal(restores + 5, hw.RestoreCount);

        hw.Values["cpu"] = 60;
        ControlTick back = loop.Tick(config);
        Assert.False(back.FailSafeTriggered);
        Assert.Equal(FailSafeState.None, back.FailSafe);
        Assert.Equal(FanControlState.Curve, Assert.Single(back.Actions).State);
    }

    [Fact]
    public void SensorLostWhileCool_IsNotBlind()
    {
        var hw = Hw(cpu: Limit - ControlLoop.LostSensorMarginC - 1);
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config();
        loop.Tick(config);

        hw.Values["cpu"] = double.NaN;
        for (int i = 0; i < 6; i++)
        {
            ControlTick tick = loop.Tick(config);
            Assert.False(tick.FailSafeTriggered);
            Assert.Equal(FailSafeState.None, tick.FailSafe);
        }
    }

    [Fact]
    public void SensorThatCooledDown_BeforeItWasLost_IsNotBlind()
    {
        var hw = Hw(cpu: 85);
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config();
        loop.Tick(config);
        hw.Values["cpu"] = 50; // the last reading counts, not the hottest one ever seen
        loop.Tick(config);

        hw.Values["cpu"] = double.NaN;
        for (int i = 0; i < 6; i++)
            Assert.False(loop.Tick(config).FailSafeTriggered);
    }

    [Fact]
    public void SensorLostNearTheLimit_WithNothingControlled_IsNotAFailSafe()
    {
        var hw = Hw(cpu: 85);
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config() with { Fans = Array.Empty<FanConfig>() };
        loop.Tick(config);

        hw.Values["cpu"] = double.NaN;
        for (int i = 0; i < 6; i++)
            Assert.False(loop.Tick(config).FailSafeTriggered); // the firmware has the fans; nothing to guard
    }

    /// <summary>After an over-temperature trip, the hot sensor going missing must not hand the fans to the curves.</summary>
    [Fact]
    public void HotSensorLostRightAfterAnOverTemperatureTrip_KeepsTheFailSafe()
    {
        var hw = Hw(cpu: 95);
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config();
        Assert.Equal(FailSafeState.OverTemperature, loop.Tick(config).FailSafe);

        hw.Values["cpu"] = double.NaN; // the cooler "board" alone would let the curve drive the fan again
        int writes = hw.Writes.Count;
        for (int i = 0; i < 4; i++)
            Assert.True(loop.Tick(config).FailSafeTriggered);
        Assert.Equal(writes, hw.Writes.Count);
    }

    /// <summary>
    /// A calibration or identification drives a fan low on purpose; its own watchdog only notices when no temperature
    /// is readable at all. The loop's watchdog covers a hot sensor going missing meanwhile - before any curve exists.
    /// </summary>
    [Fact]
    public void SensorLostNearTheLimit_WhileAFanIsSuspended_Trips()
    {
        var hw = Hw(cpu: 82);
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config() with { Fans = Array.Empty<FanConfig>() };
        loop.Suspend("f", FanControlState.Calibrating);
        loop.Tick(config);

        hw.Values["cpu"] = double.NaN;
        loop.Tick(config);
        loop.Tick(config);
        ControlTick trip = loop.Tick(config);

        Assert.True(trip.FailSafeTriggered);
        Assert.Equal(FailSafeState.Blind, trip.FailSafe);
    }

    /// <summary>"Near the limit" is judged against the limit in force: raising it releases a sensor no longer near it.</summary>
    [Fact]
    public void RaisingTheLimit_ReleasesASensorNoLongerNearIt()
    {
        var hw = Hw(cpu: 80);
        var loop = new ControlLoop(hw, hw);
        AppConfig config = Config();
        loop.Tick(config);
        hw.Values["cpu"] = double.NaN;
        for (int i = 0; i < 3; i++)
            loop.Tick(config);
        Assert.True(loop.Tick(config).FailSafeTriggered);

        ControlTick released = loop.Tick(config with { FailSafeTempC = 105 });

        Assert.False(released.FailSafeTriggered);
        Assert.Equal(FailSafeState.None, released.FailSafe);
    }
}
