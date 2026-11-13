// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// A role curve of an airflow proposal: the profile's own curve with that role's stable id (<see cref="InProfile"/>),
/// or a fresh template in the profile's aggressiveness variant that taking the proposal would add.
/// </summary>
public sealed record AirflowRoleCurve(CurveConfig Curve, bool InProfile);
