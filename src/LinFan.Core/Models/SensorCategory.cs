// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// The hardware a sensor most likely belongs to, as <see cref="Services.SensorClassifier"/> guesses it - the basis
/// for suggesting sensor groups. Declared in the order the groups are suggested in.
/// </summary>
public enum SensorCategory
{
    Cpu,
    Gpu,
    Storage,
    Mainboard,
    Other,
}
