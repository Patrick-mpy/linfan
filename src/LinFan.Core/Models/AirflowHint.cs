// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Hinweis-Codes des Airflow-Auto-Tune (<see cref="AirflowTuneResult.Hints"/>). Core liefert nur den
/// Code — die GUI übersetzt ihn (resx), analog zu den IPC-Fehlercodes. Keine Anzeigetexte im Core.
/// </summary>
public enum AirflowHint
{
    /// <summary>Keine Sensoren konfiguriert — Quell-Sensor der vorgeschlagenen Kurven manuell wählen.</summary>
    NoSensorsConfigured,

    /// <summary>Keine Gehäuselüfter mit Ein-/Auslass-Position — Druckbilanz nicht bestimmbar.</summary>
    NoCaseFans,

    /// <summary>Nicht alle Gehäuselüfter kalibriert — Bilanz nur nach Lüfter-Anzahl geschätzt.</summary>
    CountEstimateOnly,

    /// <summary>Kein Einlasslüfter erkannt.</summary>
    NoIntakeFan,

    /// <summary>Kein Auslasslüfter erkannt.</summary>
    NoExhaustFan,

    /// <summary>Unterdruck: mehr Auslass als Einlass (zieht Staub durch Ritzen).</summary>
    NegativePressure,

    /// <summary>Kein CPU-Sensor per Namens-Heuristik erkannt — Quelle der CPU-Kurve prüfen.</summary>
    NoCpuSensorDetected,
}
