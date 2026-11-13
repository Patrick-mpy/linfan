// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.App.Services;

/// <summary>
/// Steuerbefehle, die die GUI an den Daemon sendet (über IPC). Der <see cref="MainController"/> hängt an
/// dieser Abstraktion statt am konkreten <see cref="IpcLiveMonitor"/> - so bleibt die GUI-seitige
/// Befehlsrichtung testbar (Fake-Sink) und MVC-konform (kein Hardware-/Transport-Detail im Controller).
/// </summary>
public interface ICommandSink
{
    /// <summary>
    /// Sends an edited config; the daemon merges it into its own. <paramref name="baseMinPwm"/> holds each fan's MinPwm as
    /// the daemon reported it last: a sent MinPwm still equal to it counts as untouched, and the daemon keeps its own value
    /// (a calibration may have changed it meanwhile). A fan without an entry has no base - its sent value wins.
    /// </summary>
    Task<bool> SendConfigAsync(AppConfig config, IReadOnlyDictionary<string, byte> baseMinPwm);
    Task SendManualPwmAsync(string fanId, byte pwm);
    Task SendFanAutoAsync(string fanId);
    Task SendStartCalibrationAsync(string fanId);
    Task SendCancelCalibrationAsync();
    Task SendActiveProfileAsync(string profileId);

    /// <summary>Dreht einen Lüfter kurz auf 100 % (andere gedrosselt), um ihn physisch zu identifizieren.</summary>
    Task SendIdentifyAsync(string fanId);

    /// <summary>Startet die automatische Tacho-Kopplung eines Lüfters (antreiben, reagierenden Drehzahl-Sensor zuordnen).</summary>
    Task SendStartTachMappingAsync(string fanId);

    /// <summary>Bricht eine laufende automatische Tacho-Kopplung ab (bzw. quittiert einen Abschluss-Status).</summary>
    Task SendCancelTachMappingAsync();

    /// <summary>Ordnet einem Lüfter fest einen Drehzahl-Sensor zu (<paramref name="sensorId"/> leer/<c>null</c> ⇒ Zuordnung löschen).</summary>
    Task SendSetFanTachometerAsync(string fanId, string? sensorId);

    /// <summary>Schaltet eine Kurve live an/aus (aus ⇒ zugeordnete Lüfter fallen im Daemon auf Hardware-Auto).</summary>
    Task SendSetCurveEnabledAsync(string curveId, bool enabled);

    /// <summary>
    /// Replaces the daemon config <b>wholesale</b> with <paramref name="config"/> (import/restore) - unlike
    /// <see cref="SendConfigAsync"/> (merge) including the calibration sent along. Returns whether the command went out
    /// - not whether the daemon took it (it may refuse it, e.g. past its limits); the config it reports next tells. A
    /// config that cannot be put on the wire (a damaged backup) throws: that is the file's fault, not the connection's.
    /// </summary>
    Task<bool> SendReplaceConfigAsync(AppConfig config);

    /// <summary>Setzt die Daemon-Config auf Werkszustand zurück (leert alles; Hardware wird neu entdeckt).</summary>
    Task<bool> SendResetConfigAsync();

    /// <summary>
    /// Keeps the entries of a replaced device (<see cref="HardwareChangeReading.Key"/>) for the device behind them now;
    /// <paramref name="resetSensorNames"/> also clears its sensors' own names. Returns whether the command went out - the
    /// daemon answers only by no longer reporting the change.
    /// </summary>
    Task<bool> SendAcceptHardwareAsync(string key, bool resetSensorNames);

    /// <summary>
    /// Removes the entries of a replaced or missing device (<see cref="HardwareChangeReading.Key"/>) and its fans' profile
    /// assignments. Returns whether the command went out; the daemon refuses it while a device action runs on one of the
    /// fans (the change then stays).
    /// </summary>
    Task<bool> SendForgetHardwareAsync(string key);
}
