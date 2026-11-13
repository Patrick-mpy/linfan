// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary><see cref="DefaultFanNames"/>: „Lüfter N" with the lowest number no other fan's name takes.</summary>
public sealed class DefaultFanNamesTests
{
    [Fact]
    public void FansWithoutAName_AreNumberedInOrder()
    {
        Assert.Equal(new[] { "Lüfter 1", "Lüfter 2", "Lüfter 3" }, DefaultFanNames.For(["", "", " "], []));
    }

    // The overview, the curves and the messages take a fan's name from the snapshot - the daemon's hardware label for a fan
    // without an own name, until a save writes „Lüfter N" down. The app names it there as in the settings.
    [Fact]
    public void ApplyTo_NamesTheFansWithoutAnOwnName_AlsoInTheCalibrationStatus()
    {
        var snapshot = new MonitorSnapshot("ok", [],
            [
                new FanReading("nct6798/pwm1", "nct6798 pwm1", 900, 100, true),
                new FanReading("nct6798/pwm2", "Heck", 700, 90, true),
            ],
            new AppConfig { Fans = [new FanConfig { FanId = "nct6798/pwm2", Name = "Heck" }] },
            Calibration: new CalibrationStatus("nct6798/pwm1", CalibrationPhase.Measuring, 100, 900, true, false, null, null,
                FanName: "nct6798 pwm1"));

        MonitorSnapshot named = DefaultFanNames.ApplyTo(snapshot);

        Assert.Equal(new[] { "Lüfter 1", "Heck" }, named.Fans.Select(f => f.Name));
        Assert.Equal("Lüfter 1", named.Calibration!.FanName);
    }

    [Fact]
    public void ApplyTo_WithEveryFanNamed_ReturnsTheSameSnapshot()
    {
        var snapshot = new MonitorSnapshot("ok", [], [new FanReading("f1", "Heck", 700, 90, true)],
            new AppConfig { Fans = [new FanConfig { FanId = "f1", Name = "Heck" }] });

        Assert.Same(snapshot, DefaultFanNames.ApplyTo(snapshot)); // the rows tell a new tick from a repeat by identity
    }

    [Fact]
    public void AFanWithAnOwnName_GetsNone_AndItsNameIsNotReused()
    {
        IReadOnlyList<string> defaults = DefaultFanNames.For(["", "lüfter 1", ""], ["Lüfter 3"]);

        // „lüfter 1" (any case) is taken, „Lüfter 3" by a fan the list does not show.
        Assert.Equal(new[] { "Lüfter 2", "", "Lüfter 4" }, defaults);
    }
}
