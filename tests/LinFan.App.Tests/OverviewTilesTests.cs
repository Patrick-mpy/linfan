// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// <see cref="OverviewTiles"/> - the hottest-sensor, fans and profile tiles and the section captions from one snapshot.
/// </summary>
public sealed class OverviewTilesTests
{
    private static readonly CurveConfig Quiet = new()
    {
        Id = "c1",
        Name = "Quiet",
        SourceSensorIds = new[] { "t1" },
        Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
    };

    private static MonitorSnapshot Snapshot(SensorReading[]? sensors = null, FanReading[]? fans = null, bool dryRun = false) => new(
        "ok",
        sensors ?? new[]
        {
            new SensorReading("t1", "Tctl", SensorKind.Temperature, "°C", 61.24),
            new SensorReading("t2", "edge", SensorKind.Temperature, "°C", 95),        // hidden
            new SensorReading("t3", "Board", SensorKind.Temperature, "°C", double.NaN),
            new SensorReading("f1rpm", "fan", SensorKind.FanRpm, "RPM", 5000),
        },
        fans ?? new[]
        {
            new FanReading("f1", "CPU", 1200, 128, true, ControlState: FanControlState.Curve, ControlCurveId: "c1"),
            new FanReading("f2", "Front", null, 51, true, ManualOverride: true, ControlState: FanControlState.Manual),
            new FanReading("f3", "Rear", 900, 255, true, ControlState: FanControlState.HardwareAuto),
            new FanReading("ro", "Pump", 2400, 0, false, ControlState: FanControlState.NotControllable),
            new FanReading("hid", "Hidden", 3000, 255, true, ControlState: FanControlState.Curve, ControlCurveId: "c1"),
        },
        new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "t1", Group = "cpu" },
                new SensorConfig { SensorId = "t2", Hidden = true },
            },
            SensorGroups = new[] { "CPU" },
            Fans = new[]
            {
                new FanConfig { FanId = "f1", AssignedCurveId = "c1" },
                new FanConfig { FanId = "hid", AssignedCurveId = "c1", Hidden = true },
            },
            Curves = new[] { Quiet, Quiet with { Id = "off", Enabled = false } },
            Profiles = new[] { new Profile { Id = "p1", Name = "Ausgewogen" } },
            ActiveProfileId = "p1",
        },
        Connected: true, DryRun: dryRun);

    [Fact]
    public void Hottest_IsTheHottestVisibleReadableSensor_WithItsSavedGroupName()
    {
        OverviewTiles tiles = OverviewTiles.Build(Snapshot(), connected: true, unsavedChanges: false);

        Assert.Equal("61.2 °C", tiles.HottestValue);   // the hidden 95 °C does not count
        Assert.Equal(61.24, tiles.HottestTemperature);
        Assert.False(tiles.HottestIsHot);
        Assert.Equal("Tctl · CPU", tiles.HottestName);  // the saved spelling of the group
        Assert.Equal("Heißester Sensor: 61.2 °C, Tctl · CPU", tiles.HottestTileName);
        Assert.Equal("2 Sensoren", tiles.SensorCountText);
    }

    [Fact]
    public void Hottest_Hot_IsSaidInTheTilesName_Ungrouped_Named()
    {
        OverviewTiles tiles = OverviewTiles.Build(
            Snapshot(new[] { new SensorReading("x", "Loose", SensorKind.Temperature, "°C", 85) }), connected: true, unsavedChanges: false);

        Assert.True(tiles.HottestIsHot);
        Assert.Equal("Loose · Ungruppiert", tiles.HottestName);
        Assert.Equal("Heißester Sensor: 85.0 °C (heiß), Loose · Ungruppiert", tiles.HottestTileName);
        Assert.Equal("1 Sensor", tiles.SensorCountText);
    }

    [Fact]
    public void NoSensor_ShowsADash()
    {
        OverviewTiles tiles = OverviewTiles.Build(Snapshot(Array.Empty<SensorReading>()), connected: true, unsavedChanges: false);

        Assert.Equal("-", tiles.HottestValue);
        Assert.Equal("Keine Sensoren", tiles.HottestName);
    }

    [Fact]
    public void Fans_AverageTheControllableVisibleOnes_AndSayWhoControlsThem()
    {
        OverviewTiles tiles = OverviewTiles.Build(Snapshot(), connected: true, unsavedChanges: false);

        // f1 50 %, f2 20 %, f3 100 % → 57 %; speed only from the fans with a tachometer: (1200 + 900) / 2.
        Assert.Equal("Ø 57 % · 1050 RPM", tiles.FanHeadline);
        Assert.Equal("3 steuerbar · 1 über Kurven, 1 manuell, 1 sonstige", tiles.FanDetail);
        Assert.Equal("4 Lüfter · Verlauf: letzte 60 Sek.", tiles.FanCountText); // the read-only one is in the table too
    }

    [Fact]
    public void Fans_Offline_DoNotClaimWhoControlsThem()
    {
        Assert.Equal("3 steuerbar", OverviewTiles.Build(Snapshot(), connected: false, unsavedChanges: false).FanDetail);
    }

    [Fact]
    public void Fans_InADryRun_CountByCurves()
    {
        Assert.StartsWith("3 steuerbar · 1 über Kurven", OverviewTiles.Build(Snapshot(dryRun: true), true, false).FanDetail);
    }

    [Fact]
    public void Profile_NamesTheRunningOne_CountsActiveCurves_AndUnsavedChanges()
    {
        OverviewTiles clean = OverviewTiles.Build(Snapshot(), connected: true, unsavedChanges: false);
        OverviewTiles dirty = OverviewTiles.Build(Snapshot(), connected: true, unsavedChanges: true);

        Assert.Equal("Ausgewogen", clean.ProfileName);
        Assert.Equal("1 Kurve aktiv", clean.ProfileDetail); // the switched-off copy does not count
        Assert.Equal("1 Kurve aktiv · ungespeicherte Änderungen", dirty.ProfileDetail);
        Assert.Equal("Sensor → Kurve → Lüfter · Profil „Ausgewogen“", clean.LoopsSubtitle);
        Assert.Equal("Profil: Ausgewogen, 1 Kurve aktiv", clean.ProfileTileName);
    }

    [Fact]
    public void Texts_FollowTheLanguage()
    {
        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);
            OverviewTiles tiles = OverviewTiles.Build(Snapshot(), connected: true, unsavedChanges: false);

            Assert.Equal("3 controllable · 1 by curves, 1 manual, 1 other", tiles.FanDetail);
            Assert.Equal("1 curve active", tiles.ProfileDetail);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // the pinned test culture
        }
    }
}
