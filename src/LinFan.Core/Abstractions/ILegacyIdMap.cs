// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Abstractions;

/// <summary>
/// Optionale Backend-Fähigkeit: liefert eine Zuordnung von <b>alten, instabilen</b> persistierten
/// Sensor-/Lüfter-Ids auf die <b>aktuellen stabilen</b> Ids desselben Backends. Dient ausschließlich
/// der einmaligen Config-Migration (Schema 2 → 3) und wird nur von Backends implementiert, die früher
/// instabile Ids ausgegeben haben — konkret Linux (<c>hwmonN/temp1</c> → <c>chip/temp1</c>). Backends
/// mit von Anfang an stabilen Ids (z. B. Windows-LHM) implementieren das Interface nicht.
/// </summary>
/// <remarks>
/// Die Zuordnung ist <em>best effort</em>: sie beruht auf der <em>aktuellen</em> hwmon-Enumeration.
/// Hat sich diese seit dem Speichern bereits verschoben, kann ein Eintrag fehlen — solche Ids bleiben
/// unverändert und degradieren wie gehabt (NaN + einmalige Warnung, manuelle Neu-Zuordnung im
/// Geräte-Tab). Die Migration ist dadurch verlustfrei im Normalfall und nie gefährlich (der Watchdog
/// liest die heißeste Temperatur unabhängig von der Config über alle Kanäle).
/// </remarks>
public interface ILegacyIdMap
{
    /// <summary>
    /// Map alte Id → aktuelle stabile Id (beide als roher String, wie in der Config gespeichert).
    /// Umfasst Temperatur-, Drehzahl- und PWM-Kanäle. Leer, wenn keine Migration nötig/möglich ist.
    /// </summary>
    IReadOnlyDictionary<string, string> LegacyToStableIds();
}
