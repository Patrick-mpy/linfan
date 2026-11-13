// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// A fan as the case schema shows it inside its zone: name, position, and its <see cref="ManualControl"/> for the
/// live speed and power (both kept current by the owning row, so the schema needs no rebuild per tick).
/// </summary>
public sealed class CaseFan
{
    public CaseFan(string fanId, string name, FanLocation location, ManualControl live, ICommand? open = null,
                   string tooltip = "", bool isSelf = false, string tag = "", string arrow = "")
    {
        Arrow = arrow;
        FanId = fanId;
        Name = name;
        Location = location;
        Live = live;
        Open = open;
        Tooltip = tooltip;
        IsSelf = isSelf;
        Tag = tag;
    }

    public string FanId { get; }

    public string Name { get; }

    public FanLocation Location { get; }

    /// <summary>
    /// The fan's own air direction at its edge (→ ← ↑ ↓, a laptop's ⊙ ⊗), shown where the zone does not say it: a mixed
    /// edge and „Nicht im Schema" (<see cref="WithArrow"/>). Elsewhere the zone's label carries the direction and this is
    /// empty - the arrow would only take room from the name.
    /// </summary>
    public string Arrow { get; }

    public bool ShowsArrow => Arrow.Length > 0;

    /// <summary>Colours <see cref="Arrow"/>: intake, exhaust or internal (fan wall).</summary>
    public AirflowDirection Direction => FanLocationOption.DirectionOf(Location);

    /// <summary>Live speed (<see cref="ManualControl.LiveRpm"/>) and power (<see cref="ManualControl.LivePercent"/>).</summary>
    public ManualControl Live { get; }

    /// <summary>What clicking the fan does (the airflow page: open „Position wählen"); <c>null</c> = not clickable.</summary>
    public ICommand? Open { get; }

    public string Tooltip { get; }

    /// <summary>The fan whose position is being chosen (the dialog's preview in the picked zone).</summary>
    public bool IsSelf { get; }

    /// <summary>„neu" / „bisher" next to the preview; empty otherwise.</summary>
    public string Tag { get; }

    public bool HasTag => Tag.Length > 0;

    /// <summary>The same fan as the dialog's preview at the picked <paramref name="location"/>, with its tag.</summary>
    public CaseFan AsSelf(FanLocation location, string tag) => new(FanId, Name, location, Live, Open, Tooltip, isSelf: true, tag);

    /// <summary>The same fan with its own arrow, as <paramref name="caseType"/>'s schema draws it (see <see cref="Arrow"/>).</summary>
    public CaseFan WithArrow(CaseType caseType) =>
        new(FanId, Name, Location, Live, Open, Tooltip, IsSelf, Tag, CaseSchema.Arrow(Location, caseType));
}
