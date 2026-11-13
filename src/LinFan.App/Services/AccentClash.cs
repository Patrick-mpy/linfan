// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>Which status colour an accent comes close to (<see cref="AccentPalette.ClashOf"/>) - status marks then stand out less.</summary>
public enum AccentClash
{
    None,

    /// <summary>Warning amber (and the exhaust colour of the airflow view).</summary>
    Warning,

    /// <summary>Error red.</summary>
    Danger,

    /// <summary>Success green.</summary>
    Success,
}
