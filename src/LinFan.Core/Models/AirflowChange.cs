// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// One fan of an airflow proposal for a profile: the curve it follows there now and the one its position suggests
/// (<c>null</c> = hardware auto, e.g. the PSU). Unchanged entries are part of the proposal too, so the GUI can list
/// what stays the same.
/// </summary>
public sealed record AirflowChange(string FanId, string? CurrentCurveId, string? ProposedCurveId)
{
    public bool Changed => !string.Equals(CurrentCurveId, ProposedCurveId, StringComparison.Ordinal);
}
