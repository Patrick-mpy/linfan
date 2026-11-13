// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>The button an overview hint offers; <see cref="OverviewHint.Target"/> names what it acts on.</summary>
public enum HintAction
{
    None,

    /// <summary>Open the temperature tile of a group (target: the group key, "" = ungrouped).</summary>
    ShowTemperatureGroup,

    /// <summary>End a manual override (target: the fan id). Needs the daemon.</summary>
    BackToCurve,

    /// <summary>Switch a curve of the running set on (target: the curve id). Needs the daemon.</summary>
    EnableCurve,

    /// <summary>Open a curve in the editor (target: the curve id).</summary>
    OpenCurve,

    /// <summary>Scroll to the overview's fan table (calibrating lives in its rows' controls).</summary>
    ShowFans,

    /// <summary>Calibrate a fan, as its row in the fan table does (target: the fan id). Needs the daemon.</summary>
    Calibrate,

    /// <summary>Open the dialog about replaced and missing hardware.</summary>
    ReviewHardware,
}
