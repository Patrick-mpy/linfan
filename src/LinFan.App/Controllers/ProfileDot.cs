// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>The status dot of the header's profile button (and the "running" pill in its menu).</summary>
public enum ProfileDot
{
    /// <summary>The first connection attempt has no outcome yet.</summary>
    Connecting,

    /// <summary>The daemon regulates with the profile.</summary>
    Running,

    /// <summary>The daemon computes the curves but writes no PWM.</summary>
    DryRun,

    /// <summary>The daemon is out of reach.</summary>
    Offline,
}
