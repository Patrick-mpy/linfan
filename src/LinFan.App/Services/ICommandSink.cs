// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.App.Services;

/// <summary>
/// Steuerbefehle, die die GUI an den Daemon sendet (über IPC). Der <see cref="MainController"/> hängt an
/// dieser Abstraktion statt am konkreten <see cref="IpcLiveMonitor"/> — so bleibt die GUI-seitige
/// Befehlsrichtung testbar (Fake-Sink) und MVC-konform (kein Hardware-/Transport-Detail im Controller).
/// </summary>
public interface ICommandSink
{
    Task<bool> SendConfigAsync(AppConfig config);
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
    /// Ersetzt die Daemon-Config <b>vollständig</b> durch <paramref name="config"/> (Import/Restore) — anders
    /// als <see cref="SendConfigAsync"/> (Merge) inkl. der mitgeschickten Kalibrierung. Liefert, ob es gelang.
    /// </summary>
    Task<bool> SendReplaceConfigAsync(AppConfig config);

    /// <summary>Setzt die Daemon-Config auf Werkszustand zurück (leert alles; Hardware wird neu entdeckt).</summary>
    Task<bool> SendResetConfigAsync();
}
