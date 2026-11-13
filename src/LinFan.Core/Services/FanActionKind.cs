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

    /// <summary>
    /// A fan the loop drove was to go back to firmware auto, but the channel has no auto mode (or setting it failed):
    /// set to full speed instead, as <see cref="LinFan.Core.Abstractions.IFanController.RestoreDefaults"/> does for
    /// such a channel - once without an auto mode, after a transient failure until auto can be set again.
    /// </summary>
    FullSpeed,
}
