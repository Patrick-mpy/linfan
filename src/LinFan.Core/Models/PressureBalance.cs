// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Grobe Einschätzung der Gehäuse-Druckbilanz (Einlass- vs. Auslass-Luftstrom). Leichter
/// <see cref="Positive"/> Druck gilt allgemein als günstig (weniger Staub durch Ritzen).
/// </summary>
public enum PressureBalance
{
    /// <summary>Keine Gehäuselüfter mit Position erkannt - Bilanz nicht bestimmbar.</summary>
    Unknown = 0,

    /// <summary>Unterdruck: deutlich mehr Auslass als Einlass (zieht Staub durch Ritzen).</summary>
    Negative,

    /// <summary>Annähernd ausgeglichen.</summary>
    Balanced,

    /// <summary>Überdruck: mehr Einlass als Auslass (günstig gegen Staub).</summary>
    Positive,
}
