// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.Core.Tests;

public sealed class DefaultProfilesTests
{
    private static readonly IReadOnlyList<FanConfig> TwoFans = new[]
    {
        new FanConfig { FanId = "fan1", Name = "CPU" },
        new FanConfig { FanId = "fan2", Name = "Chassis" },
    };

    private const string PrimaryId = "hwmon0/temp1";

    // ── Struktur ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ReturnsExactlyThreeProfiles()
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);

        Assert.Equal(3, profiles.Count);
    }

    [Fact]
    public void Build_ProfileOrder_IsSilentBalancedPerformance()
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);

        Assert.Equal("silent", profiles[0].Id);
        Assert.Equal("balanced", profiles[1].Id);
        Assert.Equal("performance", profiles[2].Id);
    }

    [Fact]
    public void Build_ProfileNames_DefaultToNeutralEnglish()
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);

        Assert.Equal("Silent", profiles[0].Name);
        Assert.Equal("Balanced", profiles[1].Name);
        Assert.Equal("Performance", profiles[2].Name);
    }

    [Fact]
    public void Build_CustomNames_ReachProfilesAndCurves()
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId, "Leise", "Ausgewogen", "Leistung");

        Assert.Equal("Leise", profiles[0].Name);
        Assert.Equal("Ausgewogen", profiles[1].Name);
        Assert.Equal("Leistung", profiles[2].Name);
        Assert.Equal("Leise", profiles[0].Curves[0].Name);
        Assert.Equal("Ausgewogen", profiles[1].Curves[0].Name);
        Assert.Equal("Leistung", profiles[2].Curves[0].Name);
    }

    // ── Kurven ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_EachProfile_HasExactlyOneCurve(int index)
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);

        Assert.Single(profiles[index].Curves);
    }

    [Theory]
    [InlineData(0, "silent-curve")]
    [InlineData(1, "balanced-curve")]
    [InlineData(2, "performance-curve")]
    public void Build_CurveId_MatchesProfileId(int index, string expectedCurveId)
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);

        Assert.Equal(expectedCurveId, profiles[index].Curves[0].Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_Curve_UsesPrimarySensorId(int index)
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);
        CurveConfig curve = profiles[index].Curves[0];

        Assert.Contains(PrimaryId, curve.SourceSensorIds);
        Assert.Single(curve.SourceSensorIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_Curve_UsesLinearInterpolation(int index)
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);
        CurveConfig curve = profiles[index].Curves[0];

        Assert.Equal(InterpolationMode.Linear, curve.InterpolationMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_Curve_UsesMaxAggregation(int index)
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);
        CurveConfig curve = profiles[index].Curves[0];

        Assert.Equal(SensorAggregation.Max, curve.Aggregation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_Curve_HysteresisIsTwo(int index)
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);
        CurveConfig curve = profiles[index].Curves[0];

        Assert.Equal(2.0, curve.HysteresisC, 3);
    }

    // ── Stützpunkte pro Preset ────────────────────────────────────────────────

    [Fact]
    public void Build_SilentCurve_HasFivePointsWithCorrectFirstAndLast()
    {
        CurveConfig curve = DefaultProfiles.Build(TwoFans, PrimaryId)[0].Curves[0];

        Assert.Equal(5, curve.Points.Count);
        Assert.Equal(new CurvePoint(35, 0), curve.Points[0]);
        Assert.Equal(new CurvePoint(88, 100), curve.Points[4]);
    }

    [Fact]
    public void Build_BalancedCurve_HasFivePointsWithCorrectFirstAndLast()
    {
        CurveConfig curve = DefaultProfiles.Build(TwoFans, PrimaryId)[1].Curves[0];

        Assert.Equal(5, curve.Points.Count);
        Assert.Equal(new CurvePoint(30, 20), curve.Points[0]);
        Assert.Equal(new CurvePoint(90, 100), curve.Points[4]);
    }

    [Fact]
    public void Build_PerformanceCurve_HasFourPointsWithCorrectFirstAndLast()
    {
        CurveConfig curve = DefaultProfiles.Build(TwoFans, PrimaryId)[2].Curves[0];

        Assert.Equal(4, curve.Points.Count);
        Assert.Equal(new CurvePoint(30, 35), curve.Points[0]);
        Assert.Equal(new CurvePoint(72, 100), curve.Points[3]);
    }

    // ── Assignments ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Build_Assignments_CoverAllFans(int index)
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);
        var assignedFanIds = profiles[index].Assignments.Select(a => a.FanId).ToHashSet();

        foreach (FanConfig fan in TwoFans)
            Assert.Contains(fan.FanId, assignedFanIds);
    }

    [Theory]
    [InlineData(0, "silent-curve")]
    [InlineData(1, "balanced-curve")]
    [InlineData(2, "performance-curve")]
    public void Build_Assignments_AllPointToProfileCurve(int index, string expectedCurveId)
    {
        var profiles = DefaultProfiles.Build(TwoFans, PrimaryId);

        foreach (ProfileAssignment assignment in profiles[index].Assignments)
            Assert.Equal(expectedCurveId, assignment.CurveId);
    }

    // ── Grenzfälle ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_EmptyFanList_NoAssignmentsAndNoThrow()
    {
        var profiles = DefaultProfiles.Build(Array.Empty<FanConfig>(), PrimaryId);

        Assert.Equal(3, profiles.Count);
        foreach (Profile profile in profiles)
            Assert.Empty(profile.Assignments);
    }

    [Fact]
    public void Build_NullFans_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DefaultProfiles.Build(null!, PrimaryId));
    }

    [Fact]
    public void Build_NullSensorId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            DefaultProfiles.Build(TwoFans, null!));
    }

    [Fact]
    public void Build_EmptySensorId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            DefaultProfiles.Build(TwoFans, ""));
    }

    [Fact]
    public void Build_IsDeterministic_SameInputSameOutput()
    {
        var first = DefaultProfiles.Build(TwoFans, PrimaryId);
        var second = DefaultProfiles.Build(TwoFans, PrimaryId);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Id, second[i].Id);
            Assert.Equal(first[i].Curves[0].Points.Count, second[i].Curves[0].Points.Count);
        }
    }
}
