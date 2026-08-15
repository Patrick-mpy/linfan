// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Einbau-Position eines Lüfters. Strukturiert (nicht Freitext), damit eine spätere Airflow-Logik
/// Einlass-/Auslass-Bilanz und Position automatisch auswerten kann (Über-/Unterdruck, Zonen).
/// Die Richtung (Intake/Exhaust) steckt bewusst im Namen - jede Gehäuse-Position gibt es als Einlass-
/// <b>und</b> Auslass-Variante, da ein Lüfter an derselben Stelle in beide Richtungen blasen kann.
/// </summary>
/// <remarks>
/// Wird als <c>int</c> serialisiert; neue Werte daher <b>nur anhängen</b>, nie umsortieren, sonst zeigen
/// bestehende Configs auf falsche Positionen.
/// </remarks>
public enum FanLocation
{
    /// <summary>Unbekannt / nicht zugeordnet.</summary>
    Unspecified = 0,

    CpuCooler,
    GpuCooler,
    Radiator,

    CaseFrontIntake,
    CaseBottomIntake,
    CaseSideIntake,

    CaseRearExhaust,
    CaseTopExhaust,

    Psu,
    Other,

    // Richtungs-Gegenstücke der Gehäuse-Positionen (ans Ende angehängt → stabile Int-Serialisierung).
    CaseFrontExhaust,
    CaseBottomExhaust,
    CaseSideExhaust,
    CaseTopIntake,
    CaseRearIntake,
}
