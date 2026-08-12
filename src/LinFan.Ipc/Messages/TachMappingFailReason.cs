// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Ursache, warum eine automatische Sensor-Kopplung abbrach (für eine lokalisierbare GUI-Meldung) —
/// parallel zu <see cref="CalibrationFailReason"/>/<see cref="IdentifyFailReason"/>.
/// </summary>
public enum TachMappingFailReason
{
    /// <summary>Watchdog-Abbruch wegen Übertemperatur (Messwerte in <see cref="IpcTachMapping.OverTempC"/>/<see cref="IpcTachMapping.OverLimitC"/>).</summary>
    OverTemperature,

    /// <summary>Keine lesbare Temperatur während des Antreibens → kein Watchdog möglich.</summary>
    NoTemperatureReading,

    /// <summary>Vom Nutzer abgebrochen.</summary>
    Canceled,

    /// <summary>Lüfter ist nicht steuerbar (read-only / ohne Rechte) — Normalzustand, kein echter Fehler.</summary>
    NotControllable,

    /// <summary>Sonstiger, nicht klassifizierter Fehler.</summary>
    Unknown,

    /// <summary>
    /// Another coupling run was still active, so this request was refused. Reported instead of staying
    /// silent: the GUI would otherwise wait out its timeout and write the fan off as failed.
    /// </summary>
    Busy,
}
