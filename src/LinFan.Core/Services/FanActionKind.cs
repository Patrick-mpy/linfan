// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>Was der <see cref="ControlLoop"/> in einem Tick mit einem Lüfter getan hat.</summary>
public enum FanActionKind
{
    /// <summary>PWM wurde tatsächlich gesetzt.</summary>
    Applied,

    /// <summary>PWM wurde nur berechnet (kein Schreibzugriff / Dry-Run).</summary>
    DryRun,

    /// <summary>Wert gehalten (Temperaturänderung innerhalb der Hysterese).</summary>
    Held,

    /// <summary>Fester PWM-Wert (manueller Modus aus der GUI), übersteuert die Kurve.</summary>
    Manual,

    /// <summary>Übersprungen (z. B. Quell-Sensor nicht lesbar oder keine Kurve).</summary>
    Skipped,

    /// <summary>Schreiben fehlgeschlagen (z. B. fehlende Rechte).</summary>
    Failed,
}
