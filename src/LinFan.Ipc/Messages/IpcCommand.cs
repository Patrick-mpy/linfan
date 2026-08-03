// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// GUI → Daemon: ein Kommando. <see cref="Command"/> ist die Art:
/// <list type="bullet">
/// <item><c>"reload"</c> — Konfiguration neu einlesen.</item>
/// <item><c>"saveConfig"</c> — mitgeschickte <see cref="Config"/> übernehmen und persistieren.</item>
/// <item><c>"setManualPwm"</c> — Lüfter <see cref="Target"/> auf festen PWM <see cref="Value"/> (0–255).</item>
/// <item><c>"setFanAuto"</c> — Lüfter <see cref="Target"/> zurück auf Kurven-/Auto-Regelung.</item>
/// <item><c>"startCalibration"</c> — Lüfter <see cref="Target"/> kalibrieren.</item>
/// <item><c>"cancelCalibration"</c> — laufende Kalibrierung abbrechen.</item>
/// <item><c>"identify"</c> — Lüfter <see cref="Target"/> kurz auf 100 % drehen, alle anderen drosseln (zum Zuordnen).</item>
/// <item><c>"setFanTachometer"</c> — Lüfter <see cref="Target"/> den Drehzahl-Sensor <see cref="RpmSource"/> fest zuordnen (leer/<c>null</c> ⇒ Zuordnung löschen, zurück auf Backend-Heuristik).</item>
/// <item><c>"startTachMapping"</c> — Lüfter <see cref="Target"/> automatisch mit seinem Drehzahl-Sensor koppeln (antreiben, reagierenden Tacho zuordnen).</item>
/// <item><c>"cancelTachMapping"</c> — laufende automatische Kopplung abbrechen (bzw. Abschluss-Status quittieren).</item>
/// <item><c>"setActiveProfile"</c> — Profil <see cref="Target"/> aktivieren (Zuordnungen übernehmen).</item>
/// <item><c>"setCurveEnabled"</c> — Kurve <see cref="Target"/> an/aus (<see cref="Value"/> 1/0); aus ⇒ zugeordnete Lüfter auf Hardware-Auto.</item>
/// <item><c>"replaceConfig"</c> — mitgeschickte <see cref="Config"/> die bestehende <b>vollständig ersetzen</b> (kein Merge; für Import/Restore inkl. eingehender Kalibrierung).</item>
/// <item><c>"resetConfig"</c> — Konfiguration auf Werkszustand zurücksetzen (Sensoren/Lüfter/Profile/Kurven/Kalibrierung leeren; Hardware wird neu entdeckt).</item>
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

    /// <summary>Config vollständig ersetzen (Import/Restore) — anders als <see cref="SaveConfig"/> kein Merge.</summary>
    public const string ReplaceConfig = "replaceConfig";

    /// <summary>Config auf Werkszustand zurücksetzen (leert alles, Hardware wird neu entdeckt).</summary>
    public const string ResetConfig = "resetConfig";
}
