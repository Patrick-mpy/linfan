// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>Phase/Ergebnis einer automatischen Sensor-Kopplung (<see cref="IpcTachMapping"/>).</summary>
public enum TachMappingPhase
{
    /// <summary>Läuft gerade (Ziel wird angetrieben, Drehzahlen werden gemessen).</summary>
    Running,

    /// <summary>Fertig: genau ein Drehzahl-Sensor reagierte dominant → zugeordnet (<see cref="IpcTachMapping.MatchedTachId"/>).</summary>
    Matched,

    /// <summary>Fertig: kein Sensor reagierte spürbar (Lüfter ohne Tacho, z. B. AIO-Pumpe) - kein Fehler.</summary>
    NoResponse,

    /// <summary>Fertig: mehrere Sensoren reagierten ähnlich stark (Übersprechen) - nicht eindeutig, bitte manuell zuordnen.</summary>
    Ambiguous,

    /// <summary>Abgebrochen/fehlgeschlagen - Grund in <see cref="IpcTachMapping.FailReason"/>.</summary>
    Failed,
}
