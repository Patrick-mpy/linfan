// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Ursache, warum eine Lüfter-Identifikation abbrach (für eine lokalisierbare GUI-Meldung).
/// Codifiziert die früher als <c>Error</c>-String transportierten Exception-Meldungen.
/// </summary>
public enum IdentifyFailReason
{
    /// <summary>
    /// Watchdog-Abbruch wegen Übertemperatur (OverTemperatureException). Messwerte in
    /// <see cref="IpcIdentify.OverTempC"/>/<see cref="IpcIdentify.OverLimitC"/>.
    /// </summary>
    OverTemperature,

    /// <summary>
    /// Keine lesbare Temperatur während des Pulses → kein Watchdog möglich (InvalidOperationException).
    /// Früher: „Keine lesbare Temperatur während der Identifikation …".
    /// </summary>
    NoTemperatureReading,

    /// <summary>
    /// Abbruch (OperationCanceledException). Aktuell meldet der Producer einen Abbruch als
    /// <c>Identify=null</c> (stille Beendigung) - dieser Code ist für eine künftige explizite
    /// Abbruch-Meldung reserviert und parallel zu <see cref="CalibrationFailReason.Canceled"/>.
    /// </summary>
    Canceled,

    /// <summary>Sonstiger, nicht klassifizierter Fehler (generische Exception). Früher: <c>ex.Message</c>.</summary>
    Unknown,
}
