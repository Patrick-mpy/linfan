// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Daemon → GUI: vollständige Momentaufnahme, einmal pro Regel-Tick gepusht. Enthält neben den
/// Live-Werten auch die aktuelle (editierbare) <see cref="Config"/>, damit die GUI den Editor aus
/// der autoritativen Daemon-Konfiguration befüllt, statt selbst die Datei zu lesen.
/// </summary>
public sealed record IpcSnapshot(
    DaemonStatus Status,
    bool DryRun,
    double HottestTempC,
    IReadOnlyList<IpcSensor> Sensors,
    IReadOnlyList<IpcFan> Fans,
    IpcConfig? Config = null,
    IpcCalibration? Calibration = null,
    IpcIdentify? Identify = null,
    IpcTachMapping? TachMapping = null);

/// <summary>
/// Zustand einer (laufenden oder abgeschlossenen) Kalibrierung. <c>null</c> im Snapshot = inaktiv.
/// <para>
/// <paramref name="FailReason"/> trägt im Fehlerfall den Grund (statt eines fertigen Strings), damit die
/// App ihn lokalisiert. Bei <see cref="CalibrationFailReason.OverTemperature"/> liefern
/// <paramref name="OverTempC"/>/<paramref name="OverLimitC"/> die Messwerte für die Meldung
/// „&lt;temp&gt; °C ≥ &lt;limit&gt; °C"; sonst sind sie <c>null</c>. Der Prozentwert der Mess-Phase
/// ist aus <paramref name="CurrentPwm"/> ableitbar (pwm·100/255).
/// </para>
/// </summary>
public sealed record IpcCalibration(
    string FanId,
    CalibrationPhase Phase,
    int CurrentPwm,
    int CurrentRpm,
    bool Running,
    bool Done,
    int? StartPwm,
    CalibrationFailReason? FailReason,
    double? OverTempC = null,
    double? OverLimitC = null);

/// <summary>
/// Zustand einer Lüfter-Identifikation (Ziel kurz auf 100 %, andere gedrosselt). <c>null</c> = inaktiv.
/// <see cref="FailReason"/> trägt im Fehlerfall (z. B. Übertemperatur-Abbruch) den Grund; bei
/// <see cref="IdentifyFailReason.OverTemperature"/> liefern <see cref="OverTempC"/>/<see cref="OverLimitC"/>
/// die Messwerte, sonst <c>null</c>.
/// </summary>
public sealed record IpcIdentify(
    string FanId,
    bool Running,
    IdentifyFailReason? FailReason = null,
    double? OverTempC = null,
    double? OverLimitC = null);

/// <summary>
/// Zustand einer automatischen Sensor-Kopplung (Ziel-Lüfter hochtreiben, Rest drosseln, reagierenden
/// Tacho zuordnen). <c>null</c> im Snapshot = inaktiv. <see cref="Phase"/> trägt Verlauf/Ergebnis:
/// <see cref="TachMappingPhase.Matched"/> setzt <see cref="MatchedTachId"/>, <see cref="TachMappingPhase.Failed"/>
/// setzt <see cref="FailReason"/> (bei Übertemperatur zusätzlich <see cref="OverTempC"/>/<see cref="OverLimitC"/>).
/// <see cref="RiseRpm"/> ist der Drehzahl-Anstieg des stärksten Sensors (Diagnose/Anzeige).
/// </summary>
public sealed record IpcTachMapping(
    string FanId,
    TachMappingPhase Phase,
    bool Running,
    string? MatchedTachId = null,
    int RiseRpm = 0,
    TachMappingFailReason? FailReason = null,
    double? OverTempC = null,
    double? OverLimitC = null);
