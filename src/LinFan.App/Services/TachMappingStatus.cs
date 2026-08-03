// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Ipc.Messages;

namespace LinFan.App.Services;

/// <summary>
/// Zustand einer automatischen Tacho-Sensor-Kopplung für die GUI-Anzeige (gespiegelt aus dem
/// Daemon-Snapshot). <see cref="Phase"/> trägt Verlauf/Ergebnis (Running/Matched/NoResponse/Ambiguous/
/// Failed); der Fehlergrund kommt <b>codifiziert</b> (<see cref="TachMappingFailReason"/> statt fertigem
/// String). <see cref="OverTempC"/>/<see cref="OverLimitC"/> tragen die Messwerte bei Übertemperatur,
/// sonst <c>null</c>. <see cref="MatchedTachId"/> ist bei <see cref="TachMappingPhase.Matched"/> gesetzt.
/// </summary>
public sealed record TachMappingStatus(
    string FanId,
    TachMappingPhase Phase,
    bool Running,
    string? MatchedTachId = null,
    int RiseRpm = 0,
    TachMappingFailReason? FailReason = null,
    double? OverTempC = null,
    double? OverLimitC = null);
