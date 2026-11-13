// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>Shared labels of sensor and fan groupings.</summary>
public static class GroupLabels
{
    /// <summary>
    /// The label of sensors without a group and fans without a position, in the UI language. Group signatures that
    /// include it rebuild their headers after a language switch.
    /// </summary>
    public static string Ungrouped => Localizer.Instance["Group.Ungrouped"];
}
