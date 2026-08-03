// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using LinFan.App.Controls;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Prüft Geometrie/Hit-Test und Positions-Algebra der Gehäuse-Vorschau (ohne Rendering): eine Zone je
/// Mount, Mittelpunkt-Round-Trip, und die Einlass/Auslass-Umschaltung (Flip/Mount/SameMount).
/// </summary>
public class FanLocationLayoutTests
{
    private static readonly Size Canvas = new(440, 360);

    [Fact]
    public void Build_HasElevenMountZones_EachCanonical()
    {
        var locations = FanLocationLayout.Build(Canvas).Select(r => r.Location).ToList();

        Assert.Equal(11, locations.Count);
        Assert.Equal(locations.Count, locations.Distinct().Count());
        // Jede Zone trägt ihren kanonischen Mount-Repräsentanten (konventionelle Richtung, keine Variante).
        Assert.All(locations, l => Assert.Equal(l, FanLocationLayout.Mount(l)));
    }

    [Fact]
    public void Hit_AtRegionCenter_ReturnsThatLocation()
    {
        foreach (FanLocationLayout.Region r in FanLocationLayout.Build(Canvas))
            Assert.Equal(r.Location, FanLocationLayout.Hit(r.Bounds.Center, Canvas));
    }

    [Fact]
    public void Build_TooSmall_IsEmpty()
    {
        Assert.Empty(FanLocationLayout.Build(new Size(40, 40)));
        Assert.Null(FanLocationLayout.Hit(new Point(10, 10), new Size(40, 40)));
    }

    [Fact]
    public void Hit_OutsideAllRegions_ReturnsNull()
    {
        Assert.Null(FanLocationLayout.Hit(new Point(-5, -5), Canvas));
        Assert.Null(FanLocationLayout.Hit(new Point(1000, 1000), Canvas));
    }

    [Fact]
    public void EveryLocation_IncludingDirectionVariants_MapsToABuiltZone()
    {
        // Auch die Richtungs-Gegenstücke (CaseFrontExhaust …) müssen eine der 11 Zonen treffen, damit das
        // Diagramm sie hervorheben kann — sie teilen die Zone ihres Mounts.
        var zones = FanLocationLayout.Build(Canvas).Select(r => r.Location).ToList();
        foreach (FanLocation loc in Enum.GetValues<FanLocation>())
            Assert.Contains(zones, z => FanLocationLayout.SameMount(z, loc));
    }

    [Theory]
    [InlineData(FanLocation.CaseFrontIntake, FanLocation.CaseFrontExhaust)]
    [InlineData(FanLocation.CaseBottomIntake, FanLocation.CaseBottomExhaust)]
    [InlineData(FanLocation.CaseSideIntake, FanLocation.CaseSideExhaust)]
    [InlineData(FanLocation.CaseTopExhaust, FanLocation.CaseTopIntake)]
    [InlineData(FanLocation.CaseRearExhaust, FanLocation.CaseRearIntake)]
    public void Flip_PairsCaseDirections_AndIsInvolution(FanLocation a, FanLocation b)
    {
        Assert.Equal(b, FanLocationLayout.Flip(a));
        Assert.Equal(a, FanLocationLayout.Flip(b));
        Assert.True(FanLocationLayout.SameMount(a, b));
        Assert.True(FanLocationLayout.CanFlip(a));
        Assert.True(FanLocationLayout.CanFlip(b));
        // Die beiden Richtungen sind tatsächlich Einlass vs. Auslass.
        Assert.NotEqual(AirflowTuneService.DirectionOf(a), AirflowTuneService.DirectionOf(b));
    }

    [Theory]
    [InlineData(FanLocation.CpuCooler)]
    [InlineData(FanLocation.GpuCooler)]
    [InlineData(FanLocation.Radiator)]
    [InlineData(FanLocation.Psu)]
    [InlineData(FanLocation.Unspecified)]
    [InlineData(FanLocation.Other)]
    public void Flip_LeavesNonCasePositions_Untouched(FanLocation loc)
    {
        Assert.Equal(loc, FanLocationLayout.Flip(loc));
        Assert.False(FanLocationLayout.CanFlip(loc));
    }

    [Fact]
    public void SameMount_DistinguishesDifferentMounts()
    {
        Assert.False(FanLocationLayout.SameMount(FanLocation.CaseFrontIntake, FanLocation.CaseRearExhaust));
        Assert.False(FanLocationLayout.SameMount(FanLocation.CpuCooler, FanLocation.GpuCooler));
        Assert.True(FanLocationLayout.SameMount(FanLocation.CaseTopIntake, FanLocation.CaseTopExhaust));
    }

    [Fact]
    public void Build_AirflowZones_HaveExpectedConventionalDirection()
    {
        var byLoc = FanLocationLayout.Build(Canvas).ToDictionary(r => r.Location);

        Assert.Equal(AirflowDirection.Exhaust, AirflowTuneService.DirectionOf(byLoc[FanLocation.CaseTopExhaust].Location));
        Assert.Equal(AirflowDirection.Exhaust, AirflowTuneService.DirectionOf(byLoc[FanLocation.CaseRearExhaust].Location));
        Assert.Equal(AirflowDirection.Intake, AirflowTuneService.DirectionOf(byLoc[FanLocation.CaseFrontIntake].Location));
        Assert.Equal(AirflowDirection.Intake, AirflowTuneService.DirectionOf(byLoc[FanLocation.CaseBottomIntake].Location));
        Assert.Equal(AirflowDirection.Intake, AirflowTuneService.DirectionOf(byLoc[FanLocation.CaseSideIntake].Location));
        Assert.Equal(AirflowDirection.Internal, AirflowTuneService.DirectionOf(byLoc[FanLocation.CpuCooler].Location));
    }
}
