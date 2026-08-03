// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class ProfileServiceTests
{
    private static AppConfig TwoProfiles() => new()
    {
        Fans = new[]
        {
            new FanConfig { FanId = "f1", AssignedCurveId = "quiet" },
            new FanConfig { FanId = "f2", AssignedCurveId = "quiet" },
        },
        Profiles = new[]
        {
            new Profile { Id = "p-silent", Name = "Silent",
                Assignments = new[] { new ProfileAssignment("f1", "quiet"), new ProfileAssignment("f2", null) } },
            new Profile { Id = "p-perf", Name = "Performance",
                Assignments = new[] { new ProfileAssignment("f1", "loud"), new ProfileAssignment("f2", "loud") } },
        },
    };

    [Fact]
    public void Apply_CopiesProfileAssignments_AndSetsActive()
    {
        AppConfig result = ProfileService.Apply(TwoProfiles(), "p-perf");

        Assert.Equal("p-perf", result.ActiveProfileId);
        Assert.Equal("loud", result.Fans.Single(f => f.FanId == "f1").AssignedCurveId);
        Assert.Equal("loud", result.Fans.Single(f => f.FanId == "f2").AssignedCurveId);
    }

    [Fact]
    public void Apply_NullAssignment_LeavesFanUnregulated()
    {
        AppConfig result = ProfileService.Apply(TwoProfiles(), "p-silent");

        Assert.Equal("quiet", result.Fans.Single(f => f.FanId == "f1").AssignedCurveId);
        Assert.Null(result.Fans.Single(f => f.FanId == "f2").AssignedCurveId);
    }

    [Fact]
    public void Apply_UnknownProfile_OnlySetsActiveId()
    {
        AppConfig before = TwoProfiles();
        AppConfig result = ProfileService.Apply(before, "does-not-exist");

        Assert.Equal("does-not-exist", result.ActiveProfileId);
        Assert.Equal("quiet", result.Fans.Single(f => f.FanId == "f1").AssignedCurveId); // unverändert
    }

    [Fact]
    public void Apply_LoadsProfileCurves_IntoActiveCurves()
    {
        var config = new AppConfig
        {
            Curves = new[] { new CurveConfig { Id = "old", Name = "Old" } },
            Profiles = new[]
            {
                new Profile { Id = "p", Name = "P",
                    Curves = new[] { new CurveConfig { Id = "perf", Name = "Performance" } },
                    Assignments = Array.Empty<ProfileAssignment>() },
            },
        };

        AppConfig result = ProfileService.Apply(config, "p");

        Assert.Equal("perf", Assert.Single(result.Curves).Id); // aktive Kurven = Profil-Kurven
    }

    [Fact]
    public void EnsureProfiles_NoProfiles_CreatesDefaultFromCurrentCurves()
    {
        var config = new AppConfig
        {
            Curves = new[] { new CurveConfig { Id = "c", Name = "C" } },
            Fans = new[] { new FanConfig { FanId = "f", AssignedCurveId = "c" } },
        };

        AppConfig result = ProfileService.EnsureProfiles(config);

        Profile def = Assert.Single(result.Profiles);
        Assert.Equal("default", result.ActiveProfileId);
        Assert.Equal("c", Assert.Single(def.Curves).Id);
        Assert.Equal("c", def.Assignments.Single(a => a.FanId == "f").CurveId);
    }

    [Fact]
    public void EnsureProfiles_ProfileWithoutCurves_IsSeeded()
    {
        var config = new AppConfig
        {
            Curves = new[] { new CurveConfig { Id = "c", Name = "C" } },
            Profiles = new[] { new Profile { Id = "p", Name = "P" } }, // keine Curves
            ActiveProfileId = "p",
        };

        AppConfig result = ProfileService.EnsureProfiles(config);

        Assert.Equal("c", Assert.Single(Assert.Single(result.Profiles).Curves).Id);
    }
}
