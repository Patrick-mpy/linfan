// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// The case schema shared by Settings › Airflow and the position dialog, one layout per <see cref="CaseType"/>: a tower
/// as a side view on a 4×6 grid (top and bottom edge, front and rear, a side column, the component stack), a server as a
/// top view (front | fan wall | components | rear), a laptop as a top view of its base (rear and side vents, the CPU and
/// GPU blowers, the keyboard, the underside), an open bench as the components alone. Below sits a tray for „Ohne
/// Position" / „Sonstige" and, while a fan has a position the type lacks, „Nicht im Schema". Every edge is <b>one</b>
/// mount whose direction can flip (intake ⇄ exhaust) - both variants share a zone, and a zone with both is „gemischt".
/// Pure: tables, the mount algebra and the zone builder, no drawing.
/// </summary>
public static class CaseSchema
{
    /// <summary>
    /// Where a zone sits on the grid (0-based) and whether its fans line up side by side. <see cref="Usual"/> is the
    /// direction the edge has in this case type - what an empty zone shows and a click picks; <c>null</c> = the key's.
    /// </summary>
    private sealed record Slot(FanLocation Key, int Row, int Column, int RowSpan, int ColumnSpan, bool Flat, FanLocation? Usual = null);

    // The keys are the mounts (ZoneOf) - for a tower also the conventional direction of each edge. The cells refer to the
    // grids in Controls/CaseSchemaView.axaml, one per case type - keep both in step.
    // Tower: 4 × 6 (columns 20/15/45/20, rows 14/18/18/18/18/14).
    private static readonly Slot[] TowerSlots =
    [
        new(FanLocation.CaseTopExhaust, 0, 0, 1, 4, Flat: true),
        new(FanLocation.CaseFrontIntake, 1, 0, 4, 1, Flat: false),
        new(FanLocation.CaseSideIntake, 1, 1, 4, 1, Flat: false),
        new(FanLocation.CpuCooler, 1, 2, 1, 1, Flat: true),
        new(FanLocation.GpuCooler, 2, 2, 1, 1, Flat: true),
        new(FanLocation.Radiator, 3, 2, 1, 1, Flat: true),
        new(FanLocation.Psu, 4, 2, 1, 1, Flat: true),
        new(FanLocation.CaseRearExhaust, 1, 3, 4, 1, Flat: false),
        new(FanLocation.CaseBottomIntake, 5, 0, 1, 4, Flat: true),
    ];

    // Server, top view: 4 × 4 (columns 19/17/45/19, rows even) - air from the front (left) to the rear (right).
    private static readonly Slot[] ServerSlots =
    [
        new(FanLocation.CaseFrontIntake, 0, 0, 4, 1, Flat: false),
        new(FanLocation.FanWall, 0, 1, 4, 1, Flat: false),
        new(FanLocation.CpuCooler, 0, 2, 1, 1, Flat: true),
        new(FanLocation.GpuCooler, 1, 2, 1, 1, Flat: true),
        new(FanLocation.Radiator, 2, 2, 1, 1, Flat: true),
        new(FanLocation.Psu, 3, 2, 1, 1, Flat: true),
        new(FanLocation.CaseRearExhaust, 0, 3, 4, 1, Flat: false),
    ];

    // Laptop, top view of the base with the hinge at the top: 3 × 4 (columns 16/42/42, rows 16/30/26/28) - the rear vent
    // along the hinge, the side vent on the left, the two blowers, the keyboard, the underside as a band at the bottom.
    private static readonly Slot[] LaptopSlots =
    [
        new(FanLocation.CaseRearExhaust, 0, 1, 1, 2, Flat: true),
        new(FanLocation.CaseSideIntake, 0, 0, 3, 1, Flat: false, Usual: FanLocation.CaseSideExhaust),
        new(FanLocation.CpuCooler, 1, 1, 1, 1, Flat: true),
        new(FanLocation.GpuCooler, 1, 2, 1, 1, Flat: true),
        new(FanLocation.CaseTopExhaust, 2, 1, 1, 2, Flat: true, Usual: FanLocation.CaseTopIntake),
        new(FanLocation.CaseBottomIntake, 3, 0, 1, 3, Flat: true),
    ];

    // Open bench: 2 × 2, the components only.
    private static readonly Slot[] OpenSlots =
    [
        new(FanLocation.CpuCooler, 0, 0, 1, 1, Flat: true),
        new(FanLocation.GpuCooler, 0, 1, 1, 1, Flat: true),
        new(FanLocation.Radiator, 1, 0, 1, 1, Flat: true),
        new(FanLocation.Psu, 1, 1, 1, 1, Flat: true),
    ];

    private static Slot[] SlotsOf(CaseType caseType) => caseType switch
    {
        CaseType.Server => ServerSlots,
        CaseType.Laptop => LaptopSlots,
        CaseType.Open => OpenSlots,
        _ => TowerSlots,
    };

    private static readonly FanLocation[] TraySlots = [FanLocation.Unspecified, FanLocation.Other];

    /// <summary>The zones (their keys) of a case type in the order the arrow keys walk them: the grid, then the tray.</summary>
    public static IReadOnlyList<FanLocation> PickOrder(CaseType caseType) => [.. SlotsOf(caseType).Select(s => s.Key), .. TraySlots];

    /// <summary>
    /// Whether a fan at <paramref name="loc"/> has a zone in the case type's schema (else „Nicht im Schema") - the App's
    /// bridge to the domain rule (<see cref="AirflowTuneService.IsInCase"/>), like <see cref="FanLocationOption.DirectionOf"/>.
    /// </summary>
    public static bool IsInSchema(FanLocation loc, CaseType caseType) => AirflowTuneService.IsInCase(loc, caseType);

    // ── Mount algebra ──────────────────────────────────────────────────────

    /// <summary>The other direction of a case position; anything else stays as it is.</summary>
    public static FanLocation Flip(FanLocation loc) => loc switch
    {
        FanLocation.CaseFrontIntake => FanLocation.CaseFrontExhaust,
        FanLocation.CaseFrontExhaust => FanLocation.CaseFrontIntake,
        FanLocation.CaseBottomIntake => FanLocation.CaseBottomExhaust,
        FanLocation.CaseBottomExhaust => FanLocation.CaseBottomIntake,
        FanLocation.CaseSideIntake => FanLocation.CaseSideExhaust,
        FanLocation.CaseSideExhaust => FanLocation.CaseSideIntake,
        FanLocation.CaseTopExhaust => FanLocation.CaseTopIntake,
        FanLocation.CaseTopIntake => FanLocation.CaseTopExhaust,
        FanLocation.CaseRearExhaust => FanLocation.CaseRearIntake,
        FanLocation.CaseRearIntake => FanLocation.CaseRearExhaust,
        _ => loc,
    };

    /// <summary>Whether the position is a case edge with a direction (intake ⇄ exhaust).</summary>
    public static bool CanFlip(FanLocation loc) => Flip(loc) != loc;

    /// <summary>The zone a position belongs to: the edge's conventional direction, or the position itself.</summary>
    private static FanLocation Mount(FanLocation loc) => loc switch
    {
        FanLocation.CaseFrontExhaust => FanLocation.CaseFrontIntake,
        FanLocation.CaseBottomExhaust => FanLocation.CaseBottomIntake,
        FanLocation.CaseSideExhaust => FanLocation.CaseSideIntake,
        FanLocation.CaseTopIntake => FanLocation.CaseTopExhaust,
        FanLocation.CaseRearIntake => FanLocation.CaseRearExhaust,
        _ => loc,
    };

    /// <summary>
    /// The way the air moves at an edge (top: out = up; front: in = right) - the same in the tower's side view and the
    /// server's top view, where the fan wall moves it on to the rear. A laptop is seen from above with the hinge at the
    /// top: ⊙ comes up towards the viewer, ⊗ goes down into the base, the rear points up and the side vent left; a front
    /// fan (only ever „Nicht im Schema" there) keeps the tower's arrow.
    /// </summary>
    public static string Arrow(FanLocation loc, CaseType caseType) =>
        caseType == CaseType.Laptop && LaptopArrow(loc) is { Length: > 0 } arrow ? arrow : loc switch
        {
            FanLocation.CaseTopExhaust or FanLocation.CaseBottomIntake => "↑",
            FanLocation.CaseTopIntake or FanLocation.CaseBottomExhaust => "↓",
            FanLocation.CaseFrontIntake or FanLocation.CaseRearExhaust or FanLocation.FanWall => "→",
            FanLocation.CaseFrontExhaust or FanLocation.CaseRearIntake => "←",
            _ => "",
        };

    private static string LaptopArrow(FanLocation loc) => loc switch
    {
        FanLocation.CaseBottomIntake or FanLocation.CaseTopExhaust => "⊙",
        FanLocation.CaseTopIntake or FanLocation.CaseBottomExhaust => "⊗",
        FanLocation.CaseRearExhaust => "↑",
        FanLocation.CaseRearIntake => "↓",
        FanLocation.CaseSideExhaust => "←",
        FanLocation.CaseSideIntake => "→",
        _ => "",
    };

    /// <summary>
    /// The short zone label; it names position and direction (for the flippable edges). A laptop names its parts as one
    /// sees them: CPU / GPU fan, keyboard and underside for top and bottom.
    /// </summary>
    public static string Label(FanLocation loc, CaseType caseType) =>
        caseType == CaseType.Laptop && LaptopKey(loc) is { } key ? Localizer.Instance[key] : loc switch
        {
            FanLocation.CaseFrontIntake => Localizer.Instance["FanDiagram.FrontIntake"],
            FanLocation.CaseFrontExhaust => Localizer.Instance["FanDiagram.FrontExhaust"],
            FanLocation.CaseBottomIntake => Localizer.Instance["FanDiagram.BottomIntake"],
            FanLocation.CaseBottomExhaust => Localizer.Instance["FanDiagram.BottomExhaust"],
            FanLocation.CaseSideIntake => Localizer.Instance["FanDiagram.SideIntake"],
            FanLocation.CaseSideExhaust => Localizer.Instance["FanDiagram.SideExhaust"],
            FanLocation.CaseTopExhaust => Localizer.Instance["FanDiagram.TopExhaust"],
            FanLocation.CaseTopIntake => Localizer.Instance["FanDiagram.TopIntake"],
            FanLocation.CaseRearExhaust => Localizer.Instance["FanDiagram.RearExhaust"],
            FanLocation.CaseRearIntake => Localizer.Instance["FanDiagram.RearIntake"],
            FanLocation.CpuCooler => Localizer.Instance["FanDiagram.Cpu"],
            FanLocation.GpuCooler => Localizer.Instance["FanDiagram.Gpu"],
            FanLocation.Radiator => Localizer.Instance["FanDiagram.Radiator"],
            FanLocation.Psu => Localizer.Instance["FanDiagram.Psu"],
            FanLocation.FanWall => Localizer.Instance["FanDiagram.FanWall"],
            FanLocation.Other => Localizer.Instance["FanDiagram.Other"],
            _ => Localizer.Instance["FanDiagram.Unspecified"],
        };

    private static string? LaptopKey(FanLocation loc) => loc switch
    {
        FanLocation.CpuCooler => "FanDiagram.LaptopCpu",
        FanLocation.GpuCooler => "FanDiagram.LaptopGpu",
        FanLocation.CaseTopIntake => "FanDiagram.LaptopTopIntake",
        FanLocation.CaseTopExhaust => "FanDiagram.LaptopTopExhaust",
        FanLocation.CaseBottomIntake => "FanDiagram.LaptopBottomIntake",
        FanLocation.CaseBottomExhaust => "FanDiagram.LaptopBottomExhaust",
        _ => null,
    };

    /// <summary>
    /// A position's full name in the case type: the general one („Gehäuse oben · Auslass"), in a laptop its own („Tastatur ·
    /// Einlass", „Hinten · Auslass") - the dialog's summary and the menu's accessible names.
    /// </summary>
    public static string FullName(FanLocation loc, CaseType caseType) =>
        loc == FanLocation.Unspecified ? Localizer.Instance["FanSections.NoPosition"]
        : caseType == CaseType.Laptop && (LaptopKey(loc) is not null || CanFlip(loc)) ? Label(loc, caseType)
        : FanLocationOption.For(loc).Display;

    /// <summary>
    /// A side of the case by its short name („Vorn"; a laptop's top and bottom: „Tastatur", „Unterseite") - for „Vorn ·
    /// gemischt" and the airflow notices.
    /// </summary>
    public static string SideName(CaseSide side, CaseType caseType) => Localizer.Instance[side switch
    {
        CaseSide.Front => "FanPositionMenu.Front",
        CaseSide.Rear => "FanPositionMenu.Rear",
        CaseSide.Top => caseType == CaseType.Laptop ? "FanPositionMenu.LaptopTop" : "FanPositionMenu.Top",
        CaseSide.Bottom => caseType == CaseType.Laptop ? "FanPositionMenu.LaptopBottom" : "FanPositionMenu.Bottom",
        CaseSide.Side => "FanPositionMenu.Side",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
    }];

    /// <summary>The zone a fan at <paramref name="loc"/> shows up in.</summary>
    public static FanLocation ZoneOf(FanLocation loc) => TraySlots.Contains(loc) ? loc : Mount(loc);

    // ── Zones ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The zones of <paramref name="caseType"/>'s schema with the fans in them. A zone takes the direction most of its
    /// fans have (an empty one the edge's usual one in the type); one with intake and exhaust fans is „gemischt". Fans at
    /// a position the type lacks gather in „Nicht im Schema" (tray, only while there is one). For the position dialog:
    /// <paramref name="selected"/> is the pick - that zone shows the picked direction and <paramref name="self"/> as a
    /// preview („bisher" when unchanged, else „neu"); self's old zone, if it is left, says „bisher hier".
    /// <paramref name="pick"/> makes the zones clickable (a click hands over the edge's usual direction) - all but „Nicht im
    /// Schema", which is no position to pick.
    /// </summary>
    public static IReadOnlyList<CaseZone> Build(
        IReadOnlyList<CaseFan> fans,
        CaseType caseType,
        FanLocation? selected = null,
        CaseFan? self = null,
        Action<FanLocation>? pick = null,
        string tagNew = "",
        string tagUnchanged = "")
    {
        Slot[] slots = SlotsOf(caseType);
        var zones = new List<CaseZone>(slots.Length + TraySlots.Length + 1);
        foreach (Slot slot in slots)
            zones.Add(Zone(slot.Key, slot.Usual ?? slot.Key, slot.Row, slot.Column, slot.RowSpan, slot.ColumnSpan, slot.Flat, tray: false));
        for (int i = 0; i < TraySlots.Length; i++)
            zones.Add(Zone(TraySlots[i], TraySlots[i], 0, i, 1, 1, flat: true, tray: true));
        if (fans.Any(Outside) || self is { } me && (Outside(me) || selected is { } s && !IsInSchema(s, caseType)))
            zones.Add(OutsideZone());

        // The dialog needs one tab stop to reach the zones; while the pick is outside, the first zone takes it.
        if (pick is not null && !zones.Any(z => z.IsSelected && !z.IsOutside))
            zones[0] = zones[0].AsTabStop();
        return zones;

        bool Outside(CaseFan fan) => !IsInSchema(fan.Location, caseType);

        CaseZone Zone(FanLocation key, FanLocation usual, int row, int column, int rowSpan, int columnSpan, bool flat, bool tray)
        {
            var members = fans.Where(f => !Outside(f) && ZoneOf(f.Location) == key).ToList();
            bool isSelected = selected is { } s && IsInSchema(s, caseType) && ZoneOf(s) == key;
            bool wasHere = self is { } me && !Outside(me) && selected is { } sel && ZoneOf(me.Location) == key && ZoneOf(sel) != key;

            FanLocation effective =
                isSelected ? selected!.Value
                : wasHere && members.Count == 0 ? self!.Location
                : Majority(members) ?? usual;

            var shown = new List<CaseFan>(members);
            if (isSelected && self is not null)
                shown.Add(self.AsSelf(selected!.Value, selected.Value == self.Location ? tagUnchanged : tagNew));
            bool mixed = !tray && IsMixed(shown);
            if (mixed)
                shown = shown.Select(f => f.WithArrow(caseType)).ToList();

            return new CaseZone
            {
                Key = key,
                Row = row,
                Column = column,
                RowSpan = rowSpan,
                ColumnSpan = columnSpan,
                IsFlat = flat,
                IsTray = tray,
                // Only an edge carries fans of both directions, and every edge has its side.
                Label = mixed
                    ? Localizer.Instance.Format("FanDiagram.Mixed", SideName(AirflowTuneService.SideOf(key)!.Value, caseType))
                    : Label(effective, caseType),
                Arrow = mixed ? "" : Arrow(effective, caseType),
                Direction = tray || mixed ? AirflowDirection.Unknown : FanLocationOption.DirectionOf(effective),
                IsMixed = mixed,
                IsEmpty = shown.Count == 0 && !isSelected,
                IsSelected = isSelected,
                IsTabStop = isSelected,
                WasHere = wasHere,
                Fans = shown,
                Pick = pick is null ? null : new CommunityToolkit.Mvvm.Input.RelayCommand(() => pick(usual)),
            };
        }

        // The dialog's fan stays here (as „bisher") while its pick is a position the type lacks - there is no zone to
        // show it in otherwise; picking any zone moves it out.
        CaseZone OutsideZone()
        {
            var shown = fans.Where(Outside).ToList();
            bool isSelected = self is not null && selected is { } s && !IsInSchema(s, caseType);
            bool wasHere = self is { } me && Outside(me) && !isSelected;
            if (isSelected)
                shown.Add(self!.AsSelf(selected!.Value, selected.Value == self.Location ? tagUnchanged : tagNew));
            shown = shown.Select(f => f.WithArrow(caseType)).ToList(); // the zone names no direction
            return new CaseZone
            {
                Key = FanLocation.Unspecified,
                Column = TraySlots.Length,
                IsFlat = true,
                IsTray = true,
                IsOutside = true,
                Label = Localizer.Instance.Format("FanDiagram.Outside", CaseTypeText.Name(caseType)),
                Direction = AirflowDirection.Unknown,
                IsEmpty = shown.Count == 0 && !isSelected,
                IsSelected = isSelected,
                WasHere = wasHere,
                Fans = shown,
            };
        }
    }

    // Intake and exhaust fans in one zone - a side may carry both, each fan shows its own direction.
    private static bool IsMixed(IReadOnlyList<CaseFan> fans) =>
        fans.Any(f => f.Direction == AirflowDirection.Intake) && fans.Any(f => f.Direction == AirflowDirection.Exhaust);

    // The most common position among the fans of a zone (ties: the one that comes first).
    private static FanLocation? Majority(IReadOnlyList<CaseFan> members) =>
        members.Count == 0
            ? null
            : members.GroupBy(f => f.Location).OrderByDescending(g => g.Count()).First().Key;
}
