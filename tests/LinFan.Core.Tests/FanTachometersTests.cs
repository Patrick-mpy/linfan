// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class FanTachometersTests
{
    private static FanDescriptor Fan(string id, string? pairedTach) =>
        new(new FanId(id), id, CanControl: true, pairedTach is null ? null : new SensorId(pairedTach), id);

    [Fact]
    public void Of_WithoutCoupling_IsTheBackendsPairing()
    {
        var tachs = new FanTachometers([new FanConfig { FanId = "nct/pwm1" }]);

        Assert.Equal(new SensorId("nct/fan1"), tachs.Of(Fan("nct/pwm1", "nct/fan1")));
        Assert.Null(tachs.Of(Fan("nct/pwm2", pairedTach: null)));
    }

    [Fact]
    public void Of_AnExplicitCoupling_WinsOverThePairing()
    {
        var tachs = new FanTachometers([new FanConfig { FanId = "nct/pwm1", RpmSource = "nct/fan4" }]);

        Assert.Equal(new SensorId("nct/fan4"), tachs.Of(Fan("nct/pwm1", "nct/fan1")));
        Assert.True(tachs.HasOwnCoupling(Fan("nct/pwm1", "nct/fan1")));
        Assert.False(tachs.HasOwnCoupling(Fan("nct/pwm2", "nct/fan2")));
    }

    [Fact]
    public void Of_APairingAnotherFanIsCoupledTo_IsNone()
    {
        // The backend pairs pwm2 with fan1, but pwm1 is coupled to fan1: pwm2 would show and calibrate with pwm1's speed.
        var tachs = new FanTachometers([new FanConfig { FanId = "nct/pwm1", RpmSource = "nct/fan1" }, new FanConfig { FanId = "nct/pwm2" }]);

        Assert.Null(tachs.Of(Fan("nct/pwm2", "nct/fan1")));
        Assert.Equal(new SensorId("nct/fan1"), tachs.Of(Fan("nct/pwm1", pairedTach: null)));
    }

    [Fact]
    public void Of_ACouplingOfAnUndetectedFan_StillTakesTheSensor()
    {
        // The fan coupled to fan1 is not detected right now: its coupling still says fan1 is not pwm2's.
        var tachs = new FanTachometers([new FanConfig { FanId = "old/pwm9", RpmSource = "nct/fan1" }]);

        Assert.Null(tachs.Of(Fan("nct/pwm2", "nct/fan1")));
    }
}
