// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Ipc.Messages;

namespace LinFan.App.Services;

/// <summary>
/// Zustand einer Kalibrierung für die GUI-Anzeige (gespiegelt aus dem Daemon-Snapshot). Phase und
/// Fehlergrund kommen <b>codifiziert</b> (Enum statt fertigem String); die Anzeige-Texte erzeugt
/// <see cref="IpcStatusText"/> lokalisiert. <see cref="OverTempC"/>/<see cref="OverLimitC"/> tragen die
/// Messwerte für <see cref="CalibrationFailReason.OverTemperature"/>, sonst <c>null</c>.
/// </summary>
public sealed record CalibrationStatus(
    string FanId,
    CalibrationPhase Phase,
    int CurrentPwm,
    int CurrentRpm,
    bool Running,
    bool Done,
    int? StartPwm,
    CalibrationFailReason? FailReason,
    double? OverTempC = null,
    double? OverLimitC = null,
    string? FanName = null)
{
    /// <summary>Anzeigename des Lüfters, falls aufgelöst (sonst die Hardware-Id) - für lesbare Meldungen.</summary>
    public string DisplayName => string.IsNullOrEmpty(FanName) ? FanId : FanName;

    /// <summary>
    /// Kopfzeile für die geteilte <c>CalibrationCard</c>: während des Laufs nur „Kalibriere &lt;Name&gt;"
    /// (Phase/PWM/RPM liefert <see cref="Detail"/>), nach Abschluss die Ergebnis-/Fehlerzeile.
    /// </summary>
    public string Headline => FailReason is { } reason
        ? Localizer.Instance.Format("CalibrationStatus.Aborted", IpcStatusText.Fail(reason, OverTempC, OverLimitC))
        : Done
            ? Localizer.Instance.Format("CalibrationStatus.Done", StartPwm)
            : Localizer.Instance.Format("CalibrationStatus.Calibrating", DisplayName);

    /// <summary>Live-Detailzeile (Phase · PWM · RPM); nur während des Laufs gefüllt.</summary>
    public string Detail => Running ? $"{IpcStatusText.Phase(Phase, CurrentPwm)} · pwm {CurrentPwm} · {CurrentRpm} RPM" : "";

    /// <summary>Fortschritt in Prozent (0..100): die Rampe läuft den PWM-Bereich 0..255 ab.</summary>
    public double Progress => Math.Clamp(CurrentPwm / 255.0, 0, 1) * 100;

    /// <summary>Fortschrittsbalken nur während eines laufenden Laufs zeigen.</summary>
    public bool ShowProgress => Running;
}
