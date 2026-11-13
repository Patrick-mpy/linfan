// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// The kind of case the fans sit in. It decides which case positions exist (<see cref="FanLocation"/>) and whether a
/// pressure balance makes sense - see <c>AirflowTuneService.IsInCase</c>.
/// </summary>
/// <remarks>Stored as its name over IPC and as an <c>int</c> in the config file: only append new values.</remarks>
public enum CaseType
{
    /// <summary>A standard PC case: front, rear, top, bottom and side mounts.</summary>
    Tower = 0,

    /// <summary>A server or rack case: air from front to rear, with a fan wall between the drives and the board.</summary>
    Server,

    /// <summary>An open bench without a case: only the component fans count, there is no pressure balance.</summary>
    Open,

    /// <summary>
    /// A laptop: blower fans on CPU and GPU that draw in through the underside or the keyboard and blow out at the rear or
    /// the side - vents, not a case to pressurize, so there is no pressure balance either.
    /// </summary>
    Laptop,
}
