// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;

namespace LinFan.App.Controllers;

/// <summary>
/// A role curve tile of Settings › Airflow: the role, the curve the target profile has for it (or the template the
/// proposal would add) with a mini chart, its sources, and the fans the proposal puts on it.
/// </summary>
public sealed record AirflowRoleTile
{
    public required string Title { get; init; }

    /// <summary>Which positions the role covers („alle Einlass-Positionen").</summary>
    public required string Who { get; init; }

    public required string CurveName { get; init; }

    /// <summary>The target profile has the curve („vorhanden"); otherwise it would come from the template.</summary>
    public bool InProfile { get; init; }

    /// <summary>„vorhanden", „ausgeschaltet" (the profile has it, switched off) or „neu aus Vorlage".</summary>
    public required string StateText { get; init; }

    public StatusTone StateTone { get; init; }

    /// <summary>Why a switched-off curve matters; <c>null</c> otherwise.</summary>
    public string? StateTooltip { get; init; }

    /// <summary>Percent at 0..100 °C for the mini chart.</summary>
    public required double[] Samples { get; init; }

    public required string Detail { get; init; }

    /// <summary>The fans the proposal assigns to the role, or „kein Lüfter".</summary>
    public required string FansText { get; init; }

    /// <summary>No fan gets the role - the tile is shown faded.</summary>
    public bool IsUnused { get; init; }

    /// <summary>Opens the curve in the curve editor (only when the profile has it).</summary>
    public ICommand? Open { get; init; }
}
