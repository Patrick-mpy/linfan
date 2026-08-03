// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Ursache, warum eine Kalibrierung abbrach/fehlschlug (für eine lokalisierbare GUI-Meldung).
/// Codifiziert die früher als <c>Error</c>-String transportierten Exception-Meldungen.
/// </summary>
public enum CalibrationFailReason
{
    /// <summary>Nutzer-/Shutdown-Abbruch (OperationCanceledException). Früher: „Abgebrochen".</summary>
    Canceled,

    /// <summary>
    /// Watchdog-Abbruch wegen Übertemperatur (OverTemperatureException). Die Messwerte stehen in
    /// <see cref="IpcCalibration.OverTempC"/>/<see cref="IpcCalibration.OverLimitC"/>, damit die App
    /// „&lt;temp&gt; °C ≥ &lt;limit&gt; °C" selbst lokalisiert.
    /// </summary>
    OverTemperature,

    /// <summary>
    /// Lüfter ist nicht steuerbar (NotSupportedException, „nicht steuerbar (Root nötig)"). Einer von
    /// zwei NotSupportedException-Fällen — vom <see cref="NoTacho"/>-Fall nur über die Meldung trennbar
    /// (siehe Handoff: Producer muss die beiden NotSupportedException-Quellen unterscheiden).
    /// </summary>
    NotControllable,

    /// <summary>
    /// Kein Tachosignal — Drehzahl nicht messbar (NotSupportedException, „kein Tachosignal …"). Zweiter
    /// NotSupportedException-Fall, siehe <see cref="NotControllable"/>.
    /// </summary>
    NoTacho,

    /// <summary>
    /// Keine lesbare Temperatur während der Rampe → kein Watchdog möglich (InvalidOperationException).
    /// Früher: „Keine lesbare Temperatur während der Kalibrierung …".
    /// </summary>
    NoTemperatureReading,

    /// <summary>Sonstiger, nicht klassifizierter Fehler (generische Exception). Früher: <c>ex.Message</c>.</summary>
    Unknown,
}
