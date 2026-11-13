// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Daemon → GUI: vollständige Momentaufnahme, einmal pro Regel-Tick gepusht. Enthält neben den
/// Live-Werten auch die aktuelle (editierbare) <see cref="Config"/>, damit die GUI den Editor aus
/// der autoritativen Daemon-Konfiguration befüllt, statt selbst die Datei zu lesen.
/// <para>
/// <paramref name="FailSafe"/> is the fail-safe as the watchdog of the last tick sees it: the name of a
/// <c>LinFan.Core.Services.FailSafeState</c> member (<c>None</c> included), <c>null</c> from a daemon that predates
/// it. A name rather than a contract enum for the reason given on <see cref="IpcFan"/>.
/// </para>
/// <para>
/// <paramref name="ConfigProblem"/> says why the daemon runs without the configuration it found at start: the name of a
/// <c>LinFan.Core.Services.ConfigLoadProblem</c> member, <c>null</c> when it loaded fine or a change has been saved (or
/// a reload read the file) since.
/// <paramref name="ConfigSetAsidePath"/> is where an unusable file was moved to.
/// </para>
/// <para>
/// <paramref name="DaemonVersion"/> is the daemon's informational version (<c>X.Y.Z[-pre]+&lt;commit&gt;</c>), <c>null</c>
/// from a daemon that predates it - the GUI shows it when it differs from its own.
/// </para>
/// <para>
/// <paramref name="ConfigRevision"/> numbers the daemon's config within one run; it changes whenever the config does,
/// <c>null</c> from a daemon that predates it. A connection that asked for <see cref="IpcCommand.ConfigOnChange"/> gets
/// <paramref name="Config"/> only with a revision it has not had yet - <see cref="IpcClient"/> puts the last one back in,
/// so its readers always see the config.
/// </para>
/// <para>
/// <paramref name="HardwareChanges"/> lists the devices whose configured entries no longer match the hardware, until the
/// user resolves them; <c>null</c> when there are none or from a daemon that predates it.
/// </para>
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
    IpcTachMapping? TachMapping = null,
    string? FailSafe = null,
    string? ConfigProblem = null,
    string? ConfigSetAsidePath = null,
    string? DaemonVersion = null,
    long? ConfigRevision = null,
    IReadOnlyList<IpcHardwareChange>? HardwareChanges = null);

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
