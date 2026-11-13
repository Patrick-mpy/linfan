// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>How much an overview hint matters; the list and the status tile sort and colour by it.</summary>
public enum HintSeverity
{
    /// <summary>Worth knowing, nothing is wrong (the status stays "OK").</summary>
    Info,

    /// <summary>Something deserves a look.</summary>
    Notice,

    /// <summary>Temperatures or fans are at risk, or the daemon is out of reach.</summary>
    Critical,
}
