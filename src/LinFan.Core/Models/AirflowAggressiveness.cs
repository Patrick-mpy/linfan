// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Aggressiveness variant of the airflow role curves - maps 1:1 to the onboarding profiles
/// (silent/balanced/performance). Selects the point table per role in
/// <see cref="Services.AirflowTuneService"/>; ids and sensor sources stay identical across variants.
/// </summary>
public enum AirflowAggressiveness
{
    Silent,
    Balanced,
    Performance,
}
