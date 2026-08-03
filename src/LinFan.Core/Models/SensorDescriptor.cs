// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Beschreibt einen auslesbaren Kanal (Temperatur oder Drehzahl).</summary>
/// <param name="Id">Stabiler Bezeichner.</param>
/// <param name="Name">Anzeigename (Chip + Label/Kanal); vom Nutzer überschreibbar.</param>
/// <param name="Kind">Temperatur oder Drehzahl.</param>
/// <param name="Unit">Einheit, z. B. <c>°C</c> oder <c>RPM</c>.</param>
/// <param name="Source">Backend-spezifische Quelle (z. B. sysfs-Pfad) zur Diagnose.</param>
public sealed record SensorDescriptor(
    SensorId Id,
    string Name,
    SensorKind Kind,
    string Unit,
    string Source);
