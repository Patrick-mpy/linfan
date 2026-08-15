// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Begründungs-Code eines Lüfter-Vorschlags (<see cref="AirflowFanSuggestion.Reason"/>). Core liefert
/// nur den Code - die GUI formatiert daraus den Anzeigetext (Position und Kurvenname hat sie selbst).
/// </summary>
public enum AirflowReason
{
    /// <summary>Aus der Einbau-Position abgeleitete Rollen-Kurve.</summary>
    LocationBasedCurve,

    /// <summary>Keine Position angegeben - neutrale Standardkurve.</summary>
    NoPositionDefaultCurve,

    /// <summary>Keine Software-Kurve - Kanal bleibt auf Hardware-Auto (z. B. Netzteil).</summary>
    HardwareAuto,
}
