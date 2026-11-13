// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// The airflow proposal for one profile (<see cref="LinFan.Core.Services.AirflowTuneService.Propose"/>): per fan the
/// current and the suggested curve, plus all role curves - the profile's own where it has one, templates otherwise.
/// Nothing is written until the GUI takes picked changes over with
/// <see cref="LinFan.Core.Services.AirflowTuneService.ApplyTo"/> or <see cref="LinFan.Core.Services.AirflowTuneService.AsNewProfile"/>.
/// </summary>
public sealed record AirflowProposal
{
    /// <summary>One entry per requested fan, in the requested order - changed or not.</summary>
    public IReadOnlyList<AirflowChange> Changes { get; init; } = [];

    /// <summary>The five role curves in a fixed order (CPU/radiator, GPU, intake, exhaust, default).</summary>
    public IReadOnlyList<AirflowRoleCurve> RoleCurves { get; init; } = [];
}
