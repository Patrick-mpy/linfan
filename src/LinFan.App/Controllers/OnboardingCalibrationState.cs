// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Status eines einzelnen Lüfters innerhalb der Onboarding-Sequenz „koppeln → kalibrieren" — speist die
/// Status-Anzeige (Icon + Kurztext) in der Lüfter-Liste des Einrichtungs-Schritts. Der Ablauf pro Lüfter
/// ist: <see cref="Coupling"/> (Tacho suchen) → bei Erfolg <see cref="Running"/> (kalibrieren) → <see cref="Done"/>;
/// bei fehlendem/uneindeutigem Tacho wird die Kalibrierung übersprungen (<see cref="NoTacho"/>/<see cref="Ambiguous"/>).
/// </summary>
public enum OnboardingCalibrationState
{
    /// <summary>Noch nicht an der Reihe.</summary>
    Pending,

    /// <summary>Der Drehzahl-Sensor wird gerade automatisch gekoppelt (Vorstufe der Kalibrierung).</summary>
    Coupling,

    /// <summary>Wird gerade kalibriert.</summary>
    Running,

    /// <summary>Erfolgreich kalibriert.</summary>
    Done,

    /// <summary>Kein Drehzahl-Sensor reagierte (z. B. AIO-Pumpe) — Kalibrierung übersprungen, kein Fehler.</summary>
    NoTacho,

    /// <summary>Mehrere Sensoren reagierten ähnlich — nicht eindeutig; Kalibrierung übersprungen, später manuell zuordnen.</summary>
    Ambiguous,

    /// <summary>Kopplung oder Kalibrierung fehlgeschlagen/abgebrochen (Fehler/Zeitüberschreitung).</summary>
    Failed,
}
