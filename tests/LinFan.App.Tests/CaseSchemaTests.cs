// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Tests;

/// <summary>The case schema of Settings › Airflow and the position dialog: zones, mount algebra, majority, the pick.</summary>
public sealed class CaseSchemaTests
{
    private static CaseFan Fan(string id, FanLocation location) =>
        new(id, id, location, new ManualControl(id, canControl: true, sendManual: null, sendAuto: null));

    [Theory]
    [InlineData(CaseType.Tower, 9)]
    [InlineData(CaseType.Server, 7)]
    [InlineData(CaseType.Open, 4)]
    [InlineData(CaseType.Laptop, 6)]
    public void Build_HasTheGridZonesOfTheTypeAndTheTwoTrayZones_EveryPositionOfTheTypeInExactlyOne(CaseType caseType, int grid)
    {
        IReadOnlyList<CaseZone> zones = CaseSchema.Build([], caseType);

        Assert.Equal(grid, zones.Count(z => !z.IsTray));
        Assert.Equal(new[] { FanLocation.Unspecified, FanLocation.Other }, zones.Where(z => z.IsTray).Select(z => z.Key));
        Assert.DoesNotContain(zones, z => z.IsOutside); // only while a fan needs it
        // The schema's zones and the Core's positions of the type agree - every position has exactly one zone.
        foreach (FanLocation loc in Enum.GetValues<FanLocation>().Where(l => AirflowTuneService.IsInCase(l, caseType)))
            Assert.Single(zones, z => z.Key == CaseSchema.ZoneOf(loc));
        Assert.Equal(zones.Select(z => z.Key), CaseSchema.PickOrder(caseType));
    }

    [Fact]
    public void Server_IsATopView_WithTheFanWallBetweenFrontAndComponents()
    {
        var zones = CaseSchema.Build([Fan("wall", FanLocation.FanWall)], CaseType.Server).Where(z => !z.IsTray).ToDictionary(z => z.Key);

        Assert.Equal(0, zones[FanLocation.CaseFrontIntake].Column);
        Assert.Equal(1, zones[FanLocation.FanWall].Column);
        Assert.Equal(3, zones[FanLocation.CaseRearExhaust].Column);
        Assert.Equal(AirflowDirection.Internal, zones[FanLocation.FanWall].Direction);
        Assert.Equal("→", zones[FanLocation.FanWall].Arrow);
        Assert.Equal("Lüfterwand", zones[FanLocation.FanWall].Label);
        Assert.Equal("wall", zones[FanLocation.FanWall].Fans.Single().FanId);
    }

    [Fact]
    public void Laptop_IsATopViewOfTheBase_WithItsOwnNamesArrowsAndUsualDirections()
    {
        var zones = CaseSchema.Build([Fan("cpu", FanLocation.CpuCooler)], CaseType.Laptop, pick: _ => { })
            .Where(z => !z.IsTray).ToDictionary(z => z.Key);

        // Hinge at the top: the rear vent along it, the side vent on the left, the underside as the bottom band.
        Assert.Equal((0, 1, 2), (zones[FanLocation.CaseRearExhaust].Row, zones[FanLocation.CaseRearExhaust].Column, zones[FanLocation.CaseRearExhaust].ColumnSpan));
        Assert.Equal((0, 0, 3), (zones[FanLocation.CaseSideIntake].Row, zones[FanLocation.CaseSideIntake].Column, zones[FanLocation.CaseSideIntake].RowSpan));
        Assert.Equal((3, 3), (zones[FanLocation.CaseBottomIntake].Row, zones[FanLocation.CaseBottomIntake].ColumnSpan));
        Assert.Equal("CPU-Lüfter", zones[FanLocation.CpuCooler].Label);
        Assert.Equal("cpu", zones[FanLocation.CpuCooler].Fans.Single().FanId);
        // Empty vents show a laptop's usual way: out at the rear and the side, in through the keyboard and the underside.
        Assert.Equal(("Hinten · Auslass", "↑"), (zones[FanLocation.CaseRearExhaust].Label, zones[FanLocation.CaseRearExhaust].Arrow));
        Assert.Equal(("Seite · Auslass", "←"), (zones[FanLocation.CaseSideIntake].Label, zones[FanLocation.CaseSideIntake].Arrow));
        Assert.Equal(("Tastatur · Einlass", "⊗"), (zones[FanLocation.CaseTopExhaust].Label, zones[FanLocation.CaseTopExhaust].Arrow));
        Assert.Equal(("Unterseite · Einlass", "⊙"), (zones[FanLocation.CaseBottomIntake].Label, zones[FanLocation.CaseBottomIntake].Arrow));
        Assert.Equal(AirflowDirection.Exhaust, zones[FanLocation.CaseSideIntake].Direction);

        var picked = new List<FanLocation>();
        CaseSchema.Build([], CaseType.Laptop, pick: picked.Add).Single(z => z.Key == FanLocation.CaseTopExhaust).Pick!.Execute(null);
        Assert.Equal(new[] { FanLocation.CaseTopIntake }, picked); // a click picks the usual direction, not the mount key
    }

    [Fact]
    public void Laptop_HasNoFrontRadiatorOrPsu_AndAMixedUndersideNamesItsSide()
    {
        IReadOnlyList<CaseZone> zones = CaseSchema.Build(
            [Fan("front", FanLocation.CaseFrontIntake), Fan("psu", FanLocation.Psu),
             Fan("in", FanLocation.CaseBottomIntake), Fan("out", FanLocation.CaseBottomExhaust)], CaseType.Laptop);

        CaseZone outside = zones.Single(z => z.IsOutside);
        Assert.Equal("Nicht im Schema (Laptop)", outside.Label);
        Assert.Equal(new[] { "front", "psu" }, outside.Fans.Select(f => f.FanId));
        Assert.Equal(new[] { "→", "" }, outside.Fans.Select(f => f.Arrow)); // the front fan keeps its tower arrow, the PSU has none

        CaseZone bottom = zones.Single(z => z.Key == FanLocation.CaseBottomIntake);
        Assert.Equal("Unterseite · gemischt", bottom.Label);
        Assert.Equal(new[] { "⊙", "⊗" }, bottom.Fans.Select(f => f.Arrow));
    }

    [Fact]
    public void FansAtAPositionTheTypeLacks_GatherNotInTheSchema_WhichIsNoPick()
    {
        IReadOnlyList<CaseZone> zones = CaseSchema.Build(
            [Fan("top", FanLocation.CaseTopExhaust), Fan("front", FanLocation.CaseFrontIntake)], CaseType.Server, pick: _ => { });

        CaseZone outside = Assert.Single(zones, z => z.IsOutside);
        Assert.True(outside.IsTray);
        Assert.Equal("Nicht im Schema (Server / Rack)", outside.Label);
        Assert.Equal("top", outside.Fans.Single().FanId);
        Assert.Null(outside.Pick);
        Assert.Equal("front", zones.Single(z => z.Key == FanLocation.CaseFrontIntake).Fans.Single().FanId);
    }

    [Fact]
    public void OnAnOpenBench_EveryCaseFanIsOutside()
    {
        IReadOnlyList<CaseZone> zones = CaseSchema.Build(
            [Fan("cpu", FanLocation.CpuCooler), Fan("front", FanLocation.CaseFrontIntake), Fan("wall", FanLocation.FanWall)], CaseType.Open);

        Assert.Equal(new[] { "front", "wall" }, zones.Single(z => z.IsOutside).Fans.Select(f => f.FanId));
        Assert.Equal("cpu", zones.Single(z => z.Key == FanLocation.CpuCooler).Fans.Single().FanId);
    }

    [Fact]
    public void ASideWithIntakeAndExhaust_IsMixed_AndEachFanKeepsItsArrow()
    {
        CaseZone front = CaseSchema.Build(
                [Fan("a", FanLocation.CaseFrontIntake), Fan("b", FanLocation.CaseFrontIntake), Fan("c", FanLocation.CaseFrontExhaust)], CaseType.Tower)
            .Single(z => z.Key == FanLocation.CaseFrontIntake);

        Assert.True(front.IsMixed);
        Assert.Equal("Vorn · gemischt", front.Label);
        Assert.Equal("", front.Arrow);
        Assert.Equal(AirflowDirection.Unknown, front.Direction);
        Assert.Equal(new[] { "→", "→", "←" }, front.Fans.Select(f => f.Arrow));
        Assert.Equal(new[] { AirflowDirection.Intake, AirflowDirection.Intake, AirflowDirection.Exhaust }, front.Fans.Select(f => f.Direction));
        Assert.All(front.Fans, f => Assert.True(f.ShowsArrow)); // the zone no longer says the direction

        CaseZone rear = CaseSchema.Build([Fan("r", FanLocation.CaseRearExhaust)], CaseType.Tower).Single(z => z.Key == FanLocation.CaseRearExhaust);
        Assert.False(rear.Fans.Single().ShowsArrow); // its label does
    }

    [Fact]
    public void InTheDialog_ThePickMakesItsZoneMixed_AndTheFanLeavesNotInTheSchema()
    {
        CaseFan self = Fan("me", FanLocation.CaseTopExhaust); // a tower position, the schema is a server's
        IReadOnlyList<CaseZone> before = CaseSchema.Build([Fan("front", FanLocation.CaseFrontIntake)], CaseType.Server,
            FanLocation.CaseTopExhaust, self, _ => { }, "neu", "bisher");
        CaseZone outside = before.Single(z => z.IsOutside);
        Assert.True(outside.IsSelected);
        Assert.Equal(("me", "bisher"), (outside.Fans.Single().FanId, outside.Fans.Single().Tag));

        IReadOnlyList<CaseZone> after = CaseSchema.Build([Fan("front", FanLocation.CaseFrontIntake)], CaseType.Server,
            FanLocation.CaseFrontExhaust, self, _ => { }, "neu", "bisher");
        CaseZone front = after.Single(z => z.Key == FanLocation.CaseFrontIntake);
        Assert.True(front.IsSelected);
        Assert.True(front.IsMixed);
        Assert.Equal("←", front.Fans.Single(f => f.IsSelf).Arrow); // the preview shows the picked direction
        Assert.True(after.Single(z => z.IsOutside).WasHere);
    }

    [Fact]
    public void EmptyZones_ShowTheirConventionalDirection()
    {
        var zones = CaseSchema.Build([], CaseType.Tower).ToDictionary(z => z.Key);

        Assert.All(zones.Values, z => Assert.True(z.IsEmpty));
        Assert.Equal(AirflowDirection.Exhaust, zones[FanLocation.CaseTopExhaust].Direction);
        Assert.Equal("↑", zones[FanLocation.CaseTopExhaust].Arrow);
        Assert.Equal(AirflowDirection.Intake, zones[FanLocation.CaseFrontIntake].Direction);
        Assert.Equal("→", zones[FanLocation.CaseFrontIntake].Arrow);
        Assert.Equal(AirflowDirection.Internal, zones[FanLocation.Psu].Direction);
        Assert.Equal(AirflowDirection.Unknown, zones[FanLocation.Other].Direction);
    }

    [Fact]
    public void AZone_TakesTheDirectionOfItsFans_NotItsConventionalOne()
    {
        IReadOnlyList<CaseZone> zones = CaseSchema.Build([Fan("a", FanLocation.CaseTopIntake), Fan("b", FanLocation.CaseTopIntake)], CaseType.Tower);
        CaseZone top = zones.Single(z => z.Key == FanLocation.CaseTopExhaust);

        Assert.Equal(AirflowDirection.Intake, top.Direction);
        Assert.Equal("↓", top.Arrow);
        Assert.False(top.IsMixed);
        Assert.Equal(new[] { "a", "b" }, top.Fans.Select(f => f.FanId));
        Assert.False(top.IsEmpty);
    }

    [Fact]
    public void ThePick_ShowsItsDirection_AndTheFanAsAPreview_WhileItsOldZoneSaysItWasThere()
    {
        CaseFan self = Fan("me", FanLocation.CaseFrontIntake);
        IReadOnlyList<CaseZone> zones = CaseSchema.Build([Fan("other", FanLocation.CaseRearExhaust)], CaseType.Tower,
            selected: FanLocation.CaseRearIntake, self: self, tagNew: "neu", tagUnchanged: "bisher");

        CaseZone rear = zones.Single(z => z.Key == FanLocation.CaseRearExhaust);
        Assert.True(rear.IsSelected);
        Assert.True(rear.IsMixed); // the picked intake next to the other's exhaust
        Assert.Equal(new[] { ("other", false, ""), ("me", true, "neu") }, rear.Fans.Select(f => (f.FanId, f.IsSelf, f.Tag)));
        Assert.Equal("←", rear.Fans.Single(f => f.IsSelf).Arrow); // the preview at the picked direction

        CaseZone front = zones.Single(z => z.Key == FanLocation.CaseFrontIntake);
        Assert.True(front.WasHere);
        Assert.True(front.IsEmpty);
        Assert.Equal(AirflowDirection.Intake, front.Direction);

        CaseZone unchanged = CaseSchema.Build([], CaseType.Tower, FanLocation.CaseFrontIntake, self, tagNew: "neu", tagUnchanged: "bisher")
            .Single(z => z.Key == FanLocation.CaseFrontIntake);
        Assert.Equal("bisher", unchanged.Fans.Single().Tag);
        Assert.False(unchanged.WasHere);
    }

    [Theory]
    [InlineData(FanLocation.CaseFrontIntake, FanLocation.CaseFrontExhaust)]
    [InlineData(FanLocation.CaseBottomIntake, FanLocation.CaseBottomExhaust)]
    [InlineData(FanLocation.CaseSideIntake, FanLocation.CaseSideExhaust)]
    [InlineData(FanLocation.CaseTopExhaust, FanLocation.CaseTopIntake)]
    [InlineData(FanLocation.CaseRearExhaust, FanLocation.CaseRearIntake)]
    public void Flip_PairsCaseDirections_OnOneMount(FanLocation a, FanLocation b)
    {
        Assert.Equal(b, CaseSchema.Flip(a));
        Assert.Equal(a, CaseSchema.Flip(b));
        Assert.Equal(CaseSchema.ZoneOf(a), CaseSchema.ZoneOf(b)); // one mount, one zone
        Assert.True(CaseSchema.CanFlip(a));
        Assert.NotEqual(AirflowTuneService.DirectionOf(a), AirflowTuneService.DirectionOf(b));
    }

    [Theory]
    [InlineData(FanLocation.CpuCooler)]
    [InlineData(FanLocation.Psu)]
    [InlineData(FanLocation.Unspecified)]
    [InlineData(FanLocation.Other)]
    public void Flip_LeavesNonCasePositionsAlone(FanLocation loc)
    {
        Assert.Equal(loc, CaseSchema.Flip(loc));
        Assert.False(CaseSchema.CanFlip(loc));
    }

    [Fact]
    public void Pick_RunsTheZonesCommand_WithItsKey()
    {
        FanLocation? picked = null;
        CaseZone side = CaseSchema.Build([], CaseType.Tower, pick: loc => picked = loc).Single(z => z.Key == FanLocation.CaseSideIntake);

        side.Pick!.Execute(null);

        Assert.Equal(FanLocation.CaseSideIntake, picked);
    }

    [Fact]
    public void ThePicker_HasOneTabStop_TheFirstZone_WhileThePickLiesOutsideTheSchema()
    {
        CaseFan self = Fan("me", FanLocation.CaseTopExhaust);

        IReadOnlyList<CaseZone> outside = CaseSchema.Build([], CaseType.Server, FanLocation.CaseTopExhaust, self, _ => { });
        Assert.Equal(new[] { FanLocation.CaseFrontIntake }, outside.Where(z => z.IsTabStop).Select(z => z.Key));

        IReadOnlyList<CaseZone> inside = CaseSchema.Build([], CaseType.Server, FanLocation.FanWall, self, _ => { });
        Assert.Equal(new[] { FanLocation.FanWall }, inside.Where(z => z.IsTabStop).Select(z => z.Key));
    }
}
