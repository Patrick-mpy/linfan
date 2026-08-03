// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Beschreibt einen Lüfter samt (optionalem) Tachosignal.</summary>
/// <param name="Id">Stabiler Bezeichner des PWM-Kanals.</param>
/// <param name="Name">Anzeigename; vom Nutzer überschreibbar.</param>
/// <param name="CanControl">Ob aktuell Schreibzugriff besteht (sonst read-only, z. B. ohne Root).</param>
/// <param name="Tachometer">Verknüpfter Drehzahl-Sensor, falls vorhanden.</param>
/// <param name="Source">Backend-spezifische Quelle (z. B. sysfs-Pfad) zur Diagnose.</param>
public sealed record FanDescriptor(
    FanId Id,
    string Name,
    bool CanControl,
    SensorId? Tachometer,
    string Source);
