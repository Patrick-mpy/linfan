// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Ipc.Messages;

namespace LinFan.App.Services;

/// <summary>
/// Übersetzt die vom Daemon <b>codifiziert</b> gelieferten Status-/Phasen-/Fehler-Codes (Enums +
/// Rohparameter) in lokalisierte Anzeigetexte. Reine Presentation: der Daemon transportiert nur den
/// Grund (Code) und ggf. Messwerte; die Sprachwahl liegt allein in der App (<see cref="Localizer"/>).
/// Damit sind die früher fest deutschen Daemon-Meldungen jetzt zweisprachig.
/// </summary>
internal static class IpcStatusText
{
    /// <summary>Betriebszustand des Daemons für die Statuszeile.</summary>
    public static string Status(DaemonStatus status) => status switch
    {
        DaemonStatus.DryRun => Localizer.Instance["Ipc.Status.DryRun"],
        _ => Localizer.Instance["Ipc.Status.Active"],
    };

    /// <summary>
    /// Kalibrier-Phasentext für die Live-Detailzeile. Der Prozentwert der Mess-Phase ist nicht Teil des
    /// Vertrags - er wird wie im Daemon aus dem PWM-Rampenwert abgeleitet (pwm·100/255, ganzzahlig).
    /// Terminale Phasen (Done/Failed) werden nur außerhalb eines laufenden Laufs gesetzt und erscheinen
    /// nicht in der Detailzeile → leerer Text.
    /// </summary>
    public static string Phase(CalibrationPhase phase, int currentPwm) => phase switch
    {
        CalibrationPhase.Starting => Localizer.Instance["Ipc.Phase.Starting"],
        CalibrationPhase.Measuring => Localizer.Instance.Format("Ipc.Phase.Measuring", currentPwm * 100 / 255),
        _ => "",
    };

    /// <summary>Grund eines Kalibrier-Abbruchs/-Fehlers; bei Übertemperatur formatiert die App die Messwerte selbst.</summary>
    public static string Fail(CalibrationFailReason reason, double? overTempC, double? overLimitC) => reason switch
    {
        CalibrationFailReason.Canceled => Localizer.Instance["Ipc.Fail.Canceled"],
        CalibrationFailReason.OverTemperature => OverTemp(overTempC, overLimitC),
        CalibrationFailReason.NotControllable => Localizer.Instance["Ipc.Fail.NotControllable"],
        CalibrationFailReason.NoTacho => Localizer.Instance["Ipc.Fail.NoTacho"],
        CalibrationFailReason.NoTemperatureReading => Localizer.Instance["Ipc.Fail.NoTemperatureReading"],
        _ => Localizer.Instance["Ipc.Fail.Unknown"],
    };

    /// <summary>Grund eines Identify-Abbruchs/-Fehlers (teilt die Grund-Texte mit der Kalibrierung).</summary>
    public static string Fail(IdentifyFailReason reason, double? overTempC, double? overLimitC) => reason switch
    {
        IdentifyFailReason.OverTemperature => OverTemp(overTempC, overLimitC),
        IdentifyFailReason.NoTemperatureReading => Localizer.Instance["Ipc.Fail.NoTemperatureReading"],
        IdentifyFailReason.Canceled => Localizer.Instance["Ipc.Fail.Canceled"],
        _ => Localizer.Instance["Ipc.Fail.Unknown"],
    };

    /// <summary>
    /// Ergebnis-/Fortschrittstext einer Tacho-Kopplung. Die Ergebnis-Phasen (Matched/NoResponse/Ambiguous)
    /// haben eigene Texte; <see cref="TachMappingPhase.Failed"/> teilt die Grund-Texte mit Kalibrierung/Identify.
    /// <see cref="TachMappingPhase.Running"/> hat keinen Ergebnistext (die Zeile zeigt dann den Lauf-Hinweis).
    /// </summary>
    public static string TachMapping(TachMappingStatus status) => status.Phase switch
    {
        TachMappingPhase.Matched => Localizer.Instance["Ipc.Tach.Matched"],
        TachMappingPhase.NoResponse => Localizer.Instance["Ipc.Tach.NoResponse"],
        TachMappingPhase.Ambiguous => Localizer.Instance["Ipc.Tach.Ambiguous"],
        TachMappingPhase.Failed => Fail(status.FailReason ?? TachMappingFailReason.Unknown, status.OverTempC, status.OverLimitC),
        _ => "",
    };

    /// <summary>Grund eines Tacho-Kopplungs-Fehlers (teilt die Grund-Texte mit Kalibrierung/Identify).</summary>
    public static string Fail(TachMappingFailReason reason, double? overTempC, double? overLimitC) => reason switch
    {
        TachMappingFailReason.OverTemperature => OverTemp(overTempC, overLimitC),
        TachMappingFailReason.NoTemperatureReading => Localizer.Instance["Ipc.Fail.NoTemperatureReading"],
        TachMappingFailReason.Canceled => Localizer.Instance["Ipc.Fail.Canceled"],
        TachMappingFailReason.NotControllable => Localizer.Instance["Ipc.Fail.NotControllable"],
        TachMappingFailReason.Busy => Localizer.Instance["Ipc.Fail.Busy"],
        _ => Localizer.Instance["Ipc.Fail.Unknown"],
    };

    private static string OverTemp(double? tempC, double? limitC) =>
        Localizer.Instance.Format("Ipc.Fail.OverTemperature", tempC ?? double.NaN, limitC ?? double.NaN);
}
