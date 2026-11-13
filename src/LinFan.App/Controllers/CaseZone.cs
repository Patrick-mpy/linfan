// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// One area of the case schema (<see cref="CaseSchema"/>): where it sits in the grid, what it is called and which way
/// the air goes there, which fans are in it, and - in the position dialog - whether it is the pick.
/// </summary>
public sealed class CaseZone
{
    /// <summary>
    /// The zone's mount (<see cref="CaseSchema.ZoneOf"/>: an edge by its tower direction, a component, a tray slot); a pick
    /// hands over the edge's usual direction in the case type instead. „Nicht im Schema" is no position to pick and
    /// carries <see cref="FanLocation.Unspecified"/> like „Ohne Position" - tell the two apart by <see cref="IsOutside"/>.
    /// </summary>
    public required FanLocation Key { get; init; }
    public int Row { get; init; }
    public int Column { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;

    /// <summary>Fans side by side (top/bottom edge, component rows, tray) instead of stacked (front/side/rear).</summary>
    public bool IsFlat { get; init; }

    /// <summary>„Ohne Position" / „Sonstige" below the case.</summary>
    public bool IsTray { get; init; }

    public required string Label { get; init; }

    /// <summary>The direction of the air at this edge (↑ ↓ → ←); empty for components and the tray.</summary>
    public string Arrow { get; init; } = "";

    /// <summary>Colours the zone: intake, exhaust, a component, or unknown (no position, mixed).</summary>
    public AirflowDirection Direction { get; init; }

    /// <summary>Intake and exhaust fans at this edge at once: tinted half and half, each fan shows its own arrow.</summary>
    public bool IsMixed { get; init; }

    /// <summary>„Nicht im Schema": fans at a position the case type lacks. Never a pick.</summary>
    public bool IsOutside { get; init; }

    /// <summary>No fan in it (and not the pick): drawn as an empty, dashed slot.</summary>
    public bool IsEmpty { get; init; }

    public bool IsSelected { get; init; }

    /// <summary>The dialog's one tab stop: the picked zone, or the first pickable one while the pick lies outside the schema.</summary>
    public bool IsTabStop { get; init; }

    /// <summary>The dialog's fan sat here before and moves away: „bisher hier".</summary>
    public bool WasHere { get; init; }

    public IReadOnlyList<CaseFan> Fans { get; init; } = [];

    /// <summary>Picks this zone (the position dialog); <c>null</c> on the airflow page, where the fans are the targets.</summary>
    public ICommand? Pick { get; init; }

    /// <summary>The same zone as the dialog's tab stop.</summary>
    public CaseZone AsTabStop() => new()
    {
        Key = Key,
        Row = Row,
        Column = Column,
        RowSpan = RowSpan,
        ColumnSpan = ColumnSpan,
        IsFlat = IsFlat,
        IsTray = IsTray,
        Label = Label,
        Arrow = Arrow,
        Direction = Direction,
        IsMixed = IsMixed,
        IsOutside = IsOutside,
        IsEmpty = IsEmpty,
        IsSelected = IsSelected,
        IsTabStop = true,
        WasHere = WasHere,
        Fans = Fans,
        Pick = Pick,
    };

    /// <summary>The zone's label with the fans in it, for screen readers.</summary>
    public string AutomationName => Fans.Count == 0 ? Label : $"{Label}: {string.Join(", ", Fans.Select(f => f.Name))}";
}
