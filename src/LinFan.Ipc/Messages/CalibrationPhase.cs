// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Phase einer Kalibrierung (für die Live-Detailzeile der GUI). Codifiziert die früher als deutscher
/// String gesendete Phase. Der Prozentwert der Mess-Phase ist nicht Teil des Enums - die App leitet
/// ihn aus dem vorhandenen <see cref="IpcCalibration.CurrentPwm"/> ab (pwm·100/255).
/// </summary>
public enum CalibrationPhase
{
    /// <summary>Kalibrierung startet, vor der ersten Messung. Früher: „Start …".</summary>
    Starting,

    /// <summary>Rampe läuft, Drehzahl wird je Stufe gemessen. Früher: „Messe &lt;pct&gt; %".</summary>
    Measuring,

    /// <summary>
    /// Erfolgreich abgeschlossen. Früher: „Fertig". Terminal - spiegelt das <see cref="IpcCalibration.Done"/>-Flag;
    /// die App rendert die Phase nur während eines laufenden Laufs und nutzt sonst Done/FailReason.
    /// </summary>
    Done,

    /// <summary>
    /// Abgebrochen/fehlgeschlagen. Früher: „Fehler". Terminal - die Ursache trägt
    /// <see cref="IpcCalibration.FailReason"/>.
    /// </summary>
    Failed,
}
