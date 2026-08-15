// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class HwmonIdMigrationTests
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["hwmon7/temp1"] = "k10temp/temp1",
        ["hwmon7/fan1"] = "k10temp/fan1",
        ["hwmon3/pwm2"] = "nct6797/pwm2",
        ["hwmon3/temp2"] = "nct6797/temp2",
    };

    private static AppConfig FullConfig() => new()
    {
        SchemaVersion = 2,
        Sensors = new[] { new SensorConfig { SensorId = "hwmon7/temp1", Name = "CPU" } },
        Fans = new[] { new FanConfig { FanId = "hwmon3/pwm2", AssignedCurveId = "c1" } },
        Curves = new[]
        {
            new CurveConfig { Id = "c1", SourceSensorIds = new[] { "hwmon7/temp1", "hwmon3/temp2" } },
        },
        Profiles = new[]
        {
            new Profile
            {
                Id = "p1",
                Curves = new[] { new CurveConfig { Id = "c1", SourceSensorIds = new[] { "hwmon7/temp1" } } },
                Assignments = new[] { new ProfileAssignment("hwmon3/pwm2", "c1") },
            },
        },
        ActiveProfileId = "p1",
    };

    [Fact]
    public void Apply_RewritesAllHardwareIdFields_AndBumpsSchema()
    {
        AppConfig result = HwmonIdMigration.Apply(FullConfig(), Map, out bool changed);

        Assert.True(changed);
        Assert.Equal(3, result.SchemaVersion);
        Assert.Equal("k10temp/temp1", result.Sensors[0].SensorId);
        Assert.Equal("nct6797/pwm2", result.Fans[0].FanId);
        Assert.Equal(new[] { "k10temp/temp1", "nct6797/temp2" }, result.Curves[0].SourceSensorIds);
        Assert.Equal(new[] { "k10temp/temp1" }, result.Profiles[0].Curves[0].SourceSensorIds);
        Assert.Equal("nct6797/pwm2", result.Profiles[0].Assignments[0].FanId);
    }

    [Fact]
    public void Apply_MigratesLegacySingleSourceField()
    {
        var config = new AppConfig
        {
            SchemaVersion = 2,
            Curves = new[] { new CurveConfig { Id = "c1", SourceSensorId = "hwmon7/temp1" } },
        };

        AppConfig result = HwmonIdMigration.Apply(config, Map, out bool changed);

        Assert.True(changed);
        Assert.Equal("k10temp/temp1", result.Curves[0].SourceSensorId);
    }

    [Fact]
    public void Apply_LeavesUnmappedIdsUntouched_ButStillReportsChange()
    {
        var config = new AppConfig
        {
            SchemaVersion = 2,
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon7/temp1" },  // im Map
                new SensorConfig { SensorId = "hwmon9/temp1" },  // NICHT im Map (Enumeration verschoben)
            },
        };

        AppConfig result = HwmonIdMigration.Apply(config, Map, out bool changed);

        Assert.True(changed);
        Assert.Equal("k10temp/temp1", result.Sensors[0].SensorId);
        Assert.Equal("hwmon9/temp1", result.Sensors[1].SensorId); // bleibt → degradiert später zu NaN
    }

    [Fact]
    public void Apply_DoesNotTouchCurveOrProfileIds()
    {
        AppConfig result = HwmonIdMigration.Apply(FullConfig(), Map, out _);

        Assert.Equal("c1", result.Fans[0].AssignedCurveId);
        Assert.Equal("c1", result.Curves[0].Id);
        Assert.Equal("c1", result.Profiles[0].Assignments[0].CurveId);
        Assert.Equal("p1", result.Profiles[0].Id);
        Assert.Equal("p1", result.ActiveProfileId);
    }

    [Fact]
    public void Apply_NoMatchingIds_ReturnsSameInstance_NotChanged()
    {
        var config = new AppConfig
        {
            SchemaVersion = 2,
            Sensors = new[] { new SensorConfig { SensorId = "k10temp/temp1" } }, // schon stabil
        };

        AppConfig result = HwmonIdMigration.Apply(config, Map, out bool changed);

        Assert.False(changed);
        Assert.Same(config, result);           // keine Allokation, keine Version-Anhebung
        Assert.Equal(2, result.SchemaVersion);
    }

    [Fact]
    public void Apply_EmptyMap_ReturnsSameInstance()
    {
        var config = FullConfig();
        AppConfig result = HwmonIdMigration.Apply(config, new Dictionary<string, string>(), out bool changed);

        Assert.False(changed);
        Assert.Same(config, result);
    }

    [Fact]
    public void Apply_CollapsingIds_DeduplicatesKeepingFirst()
    {
        // Alt-Config enthält denselben Kanal zweimal: die instabile Alt-Id UND die bereits stabile Id
        // (z. B. eine frühere Teil-Migration). Beide zeigen nach dem Remap auf dieselbe stabile Id -
        // es darf nur EIN Eintrag übrig bleiben, sonst persistierte die Migration eine doppelte Id.
        var config = new AppConfig
        {
            SchemaVersion = 2,
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon7/temp1", Name = "legacy-zuerst" },
                new SensorConfig { SensorId = "k10temp/temp1", Name = "stabil-danach" },
            },
            Fans = new[]
            {
                new FanConfig { FanId = "hwmon3/pwm2", Name = "legacy-zuerst" },
                new FanConfig { FanId = "nct6797/pwm2", Name = "stabil-danach" },
            },
        };

        AppConfig result = HwmonIdMigration.Apply(config, Map, out bool changed);

        Assert.True(changed);
        SensorConfig s = Assert.Single(result.Sensors);
        Assert.Equal("k10temp/temp1", s.SensorId);
        Assert.Equal("legacy-zuerst", s.Name); // erster gewinnt (Reihenfolge bleibt erhalten)
        FanConfig f = Assert.Single(result.Fans);
        Assert.Equal("nct6797/pwm2", f.FanId);
        Assert.Equal("legacy-zuerst", f.Name);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        AppConfig once = HwmonIdMigration.Apply(FullConfig(), Map, out _);
        AppConfig twice = HwmonIdMigration.Apply(once, Map, out bool changedAgain);

        Assert.False(changedAgain);            // stabile Ids stehen nicht mehr als Schlüssel im Map
        Assert.Same(once, twice);
    }
}
