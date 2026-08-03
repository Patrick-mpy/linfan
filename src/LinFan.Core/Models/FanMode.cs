// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Steuermodus eines Lüfters.</summary>
public enum FanMode
{
    /// <summary>Hardware/Firmware regelt automatisch (Fail-Safe-Ziel).</summary>
    Auto,

    /// <summary>Software setzt den PWM-Wert direkt.</summary>
    Manual,
}
