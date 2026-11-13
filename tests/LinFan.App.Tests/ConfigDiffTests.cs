// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.Tests;

public sealed class ConfigDiffTests
{
    private static readonly CurveConfig Quiet = new()
    {
        Id = "c1",
        Name = "Quiet",
        SourceSensorIds = new[] { "t1" },
        Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
    };

    private static readonly CurveConfig Loud = new()
    {
        Id = "c2",
        Name = "Loud",
        SourceSensorIds = new[] { "t1" },
        Points = new[] { new CurvePoint(30, 60), new CurvePoint(80, 100) },
    };

    /// <summary>Two profiles sharing curve ids, as „Duplizieren" creates them.</summary>
    private static AppConfig Saved() => new()
    {
        Curves = new[] { Quiet, Loud },
        Fans = new[] { new FanConfig { FanId = "f1", Name = "CPU", AssignedCurveId = "c1" } },
        Sensors = new[] { new SensorConfig { SensorId = "t1", Name = "Tctl" } },
        SensorGroups = new[] { "CPU" },
        Profiles = new[]
        {
            new Profile { Id = "p1", Name = "Eins", Curves = new[] { Quiet, Loud }, Assignments = new[] { new ProfileAssignment("f1", "c1") } },
            new Profile { Id = "p2", Name = "Eins (Kopie)", Curves = new[] { Quiet, Loud }, Assignments = new[] { new ProfileAssignment("f1", "c1") } },
        },
        ActiveProfileId = "p1",
    };

    private static AppConfig WithProfile(AppConfig config, string id, Func<Profile, Profile> change) => config with
    {
        Profiles = config.Profiles.Select(p => p.Id == id ? change(p) : p).ToList(),
    };

    [Fact]
    public void Identical_HasNoDifference()
    {
        ConfigDiff diff = ConfigDiff.Compute(Saved(), Saved());

        Assert.Equal(0, diff.ChangedCurveCount);
        Assert.False(diff.CurvesAreaChanged);
        Assert.False(diff.SettingsAreaChanged);
    }

    [Fact]
    public void ChangedCurve_InADuplicatedProfile_MarksOnlyThatProfilesCurve()
    {
        AppConfig current = WithProfile(Saved(), "p2", p => p with
        {
            Curves = new[] { Quiet with { Name = "Leise" }, Loud },
        });

        ConfigDiff diff = ConfigDiff.Compute(Saved(), current);

        Assert.True(diff.IsCurveChanged("p2", "c1"));
        Assert.False(diff.IsCurveChanged("p1", "c1")); // same curve id, other profile: untouched
        Assert.False(diff.IsCurveChanged("p2", "c2"));
        Assert.True(diff.IsProfileChanged("p2"));
        Assert.False(diff.IsProfileChanged("p1"));
        Assert.Equal(1, diff.ChangedCurveCount);
        Assert.False(diff.ProfilesChanged); // a curve change is not a profile metadata change
        Assert.True(diff.CurvesAreaChanged);
        Assert.False(diff.SettingsAreaChanged);
    }

    [Fact]
    public void ChangedAssignment_IsACurveChange_NotAFanSetting()
    {
        AppConfig saved = Saved();
        AppConfig current = WithProfile(saved, "p1", p => p with { Assignments = new[] { new ProfileAssignment("f1", "c2") } }) with
        {
            Fans = new[] { saved.Fans[0] with { AssignedCurveId = "c2" } }, // the running assignment follows
        };

        ConfigDiff diff = ConfigDiff.Compute(saved, current);

        Assert.True(diff.IsCurveChanged("p1", "c1")); // lost its fan
        Assert.True(diff.IsCurveChanged("p1", "c2")); // gained it
        Assert.False(diff.FansChanged);
    }

    [Fact]
    public void RenamedOrNewOrRemovedProfile_IsProfileMetadata()
    {
        AppConfig saved = Saved();

        Assert.True(ConfigDiff.Compute(saved, WithProfile(saved, "p2", p => p with { Name = "Zwei" })).ProfilesChanged);
        Assert.True(ConfigDiff.Compute(saved, saved with { Profiles = saved.Profiles.Take(1).ToList() }).ProfilesChanged);

        var added = new Profile { Id = "p3", Name = "Neu", Curves = new[] { Quiet }, Assignments = Array.Empty<ProfileAssignment>() };
        ConfigDiff withNew = ConfigDiff.Compute(saved, saved with { Profiles = saved.Profiles.Append(added).ToList() });
        Assert.True(withNew.ProfilesChanged);
        Assert.True(withNew.IsProfileChanged("p3"));
        Assert.True(withNew.IsCurveChanged("p3", "c1")); // the new profile's curves are unsaved, too
    }

    [Fact]
    public void DeletedCurve_ChangesItsProfile()
    {
        ConfigDiff diff = ConfigDiff.Compute(Saved(), WithProfile(Saved(), "p1", p => p with { Curves = new[] { Quiet } }));

        Assert.True(diff.IsProfileChanged("p1"));
        Assert.True(diff.ProfilesChanged);
        Assert.Equal(0, diff.ChangedCurveCount);
    }

    [Fact]
    public void FanAndSensorSettings_AreTheSettingsArea()
    {
        AppConfig saved = Saved();

        ConfigDiff fans = ConfigDiff.Compute(saved, saved with { Fans = new[] { saved.Fans[0] with { MinPwm = 90 } } });
        Assert.True(fans.FansChanged);
        Assert.True(fans.SettingsAreaChanged);
        Assert.False(fans.CurvesAreaChanged);

        ConfigDiff sensors = ConfigDiff.Compute(saved, saved with { Sensors = new[] { saved.Sensors[0] with { Group = "CPU" } } });
        Assert.True(sensors.SensorsChanged);

        ConfigDiff groups = ConfigDiff.Compute(saved, saved with { SensorGroups = new[] { "CPU", "GPU" } });
        Assert.True(groups.SensorsChanged);
        Assert.False(groups.FansChanged);
    }

    [Fact]
    public void LegacyConfigWithoutProfiles_ComparesTheRunningCurves()
    {
        var saved = new AppConfig { Curves = new[] { Quiet }, Fans = new[] { new FanConfig { FanId = "f1", AssignedCurveId = "c1" } } };
        var current = saved with { Curves = new[] { Quiet with { HysteresisC = 5 } } };

        ConfigDiff diff = ConfigDiff.Compute(saved, current);

        Assert.True(diff.IsCurveChanged(null, "c1"));
        Assert.False(diff.ProfilesChanged);
    }
}
