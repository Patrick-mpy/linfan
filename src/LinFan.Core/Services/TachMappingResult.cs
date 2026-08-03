// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Ergebnis einer automatischen Sensor-Kopplung: welcher Drehzahl-Sensor (falls einer) zum Lüfter gehört.
/// </summary>
/// <param name="FanId">Der gekoppelte Lüfter.</param>
/// <param name="Tachometer">Der zugeordnete Drehzahl-Sensor bei <see cref="TachMappingOutcome.Matched"/>, sonst <c>null</c>.</param>
/// <param name="Outcome">Art des Ergebnisses (eindeutig / keine Reaktion / mehrdeutig).</param>
/// <param name="RiseRpm">Drehzahl-Anstieg des stärksten Sensors (Diagnose/Anzeige).</param>
/// <param name="Rises">Alle Sensoren mit ihrem Drehzahl-Anstieg, absteigend sortiert (Diagnose/Log — etwa um bei
/// „mehrdeutig" die gleich stark reagierenden Sensoren zu benennen). <c>null</c>, wenn keine Sensoren vorlagen.</param>
public sealed record TachMappingResult(
    FanId FanId, SensorId? Tachometer, TachMappingOutcome Outcome, int RiseRpm,
    IReadOnlyList<(SensorId Sensor, int Rise)>? Rises = null);
