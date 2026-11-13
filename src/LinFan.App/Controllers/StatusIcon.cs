// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>The symbol of a status toast besides its severity; the view maps each to an icon.</summary>
public enum StatusIcon
{
    /// <summary>The severity's own symbol (check for a success, alert for an error).</summary>
    Default,

    /// <summary>A switch of the running profile.</summary>
    Swap,
}
