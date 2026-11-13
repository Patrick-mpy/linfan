// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// GUI → Daemon: ein Kommando. <see cref="Command"/> ist die Art:
/// <list type="bullet">
/// <item><c>"reload"</c> - Konfiguration neu einlesen.</item>
/// <item><c>"saveConfig"</c> - mitgeschickte <see cref="Config"/> übernehmen und persistieren.</item>
/// <item><c>"setManualPwm"</c> - Lüfter <see cref="Target"/> auf festen PWM <see cref="Value"/> (0-255).</item>
/// <item><c>"setFanAuto"</c> - Lüfter <see cref="Target"/> zurück auf Kurven-/Auto-Regelung.</item>
/// <item><c>"startCalibration"</c> - Lüfter <see cref="Target"/> kalibrieren.</item>
/// <item><c>"cancelCalibration"</c> - laufende Kalibrierung abbrechen.</item>
/// <item><c>"identify"</c> - Lüfter <see cref="Target"/> kurz auf 100 % drehen, alle anderen drosseln (zum Zuordnen).</item>
/// <item><c>"setFanTachometer"</c> - Lüfter <see cref="Target"/> den Drehzahl-Sensor <see cref="RpmSource"/> fest zuordnen (leer/<c>null</c> ⇒ Zuordnung löschen, zurück auf Backend-Heuristik).</item>
/// <item><c>"startTachMapping"</c> - Lüfter <see cref="Target"/> automatisch mit seinem Drehzahl-Sensor koppeln (antreiben, reagierenden Tacho zuordnen).</item>
/// <item><c>"cancelTachMapping"</c> - laufende automatische Kopplung abbrechen (bzw. Abschluss-Status quittieren).</item>
/// <item><c>"setActiveProfile"</c> - Profil <see cref="Target"/> aktivieren (Zuordnungen übernehmen).</item>
/// <item><c>"setCurveEnabled"</c> - Kurve <see cref="Target"/> an/aus (<see cref="Value"/> 1/0); aus ⇒ zugeordnete Lüfter auf Hardware-Auto.</item>
/// <item><c>"replaceConfig"</c> - mitgeschickte <see cref="Config"/> die bestehende <b>vollständig ersetzen</b> (kein Merge; für Import/Restore inkl. eingehender Kalibrierung).</item>
/// <item><c>"configOnChange"</c> - protocol, answered by the IPC server itself: send this connection the config only
/// when it changed (see <see cref="IpcSnapshot.ConfigRevision"/>). <see cref="IpcClient"/> asks for it on connect and
/// fills the config back in.</item>
/// <item><c>"resetConfig"</c> - factory reset of what the GUI edits: sensors, fans, profiles, curves and calibration are cleared and the hardware is discovered afresh; the daemon-owned over-temperature limit and poll interval stay.</item>
/// <item><c>"acceptHardware"</c> - keep the entries of the replaced device <see cref="Target"/> (an
/// <see cref="IpcHardwareChange.Key"/> of the last snapshot) for the device behind them now; <see cref="Value"/> 1 also
/// clears the own names of its sensors (0 or none: they stay).</item>
/// <item><c>"forgetHardware"</c> - remove the entries of the device <see cref="Target"/> (an
/// <see cref="IpcHardwareChange.Key"/>) and the profile assignments of its fans; refused while a calibration,
/// identification or coupling runs on one of them.</item>
/// </list>
/// </summary>
public sealed record IpcCommand(
    string Command,
    IpcConfig? Config = null,
    string? Target = null,
    int? Value = null,
    string? RpmSource = null)
{
    public const string Reload = "reload";
    public const string SaveConfig = "saveConfig";
    public const string SetManualPwm = "setManualPwm";
    public const string SetFanAuto = "setFanAuto";
    public const string StartCalibration = "startCalibration";
    public const string CancelCalibration = "cancelCalibration";
    public const string Identify = "identify";
    public const string SetFanTachometer = "setFanTachometer";
    public const string StartTachMapping = "startTachMapping";
    public const string CancelTachMapping = "cancelTachMapping";
    public const string SetActiveProfile = "setActiveProfile";
    public const string SetCurveEnabled = "setCurveEnabled";

    /// <summary>Config vollständig ersetzen (Import/Restore) - anders als <see cref="SaveConfig"/> kein Merge.</summary>
    public const string ReplaceConfig = "replaceConfig";

    /// <summary>Factory reset of what the GUI edits; the daemon-owned limits stay (see the list above).</summary>
    public const string ResetConfig = "resetConfig";

    /// <summary>Snapshots for this connection carry the config only when it changed (see the list above).</summary>
    public const string ConfigOnChange = "configOnChange";

    /// <summary>Keep a replaced device's entries for the new device (see the list above).</summary>
    public const string AcceptHardware = "acceptHardware";

    /// <summary>Remove a replaced or missing device's entries (see the list above).</summary>
    public const string ForgetHardware = "forgetHardware";
}
