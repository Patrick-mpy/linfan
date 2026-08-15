// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Editierbarer Teil der Konfiguration über die IPC-Grenze: Kurven und Lüfter-Zuordnungen.
/// Der Daemon ist die autoritative Instanz - er führt eingehende Werte in seine vollständige
/// Konfiguration zusammen (Kalibrierung u. Ä. bleiben dort erhalten). Bewusst nur das, was die
/// GUI bearbeitet; keine Core-Objekte über die Grenze.
/// <para>
/// <paramref name="OnboardingCompleted"/> spiegelt das First-Run-Signal: <c>false</c> = der Assistent
/// soll laufen, <c>true</c> = abgeschlossen/übersprungen, <c>null</c> = ältere Gegenstelle kennt das
/// Feld nicht (der Daemon überschreibt dann seinen eigenen Stand nicht).
/// </para>
/// </summary>
public sealed record IpcConfig(
    IReadOnlyList<IpcCurve> Curves,
    IReadOnlyList<IpcFanAssignment> Fans,
    IReadOnlyList<IpcSensorName> Sensors,
    IReadOnlyList<IpcProfile> Profiles,
    string? ActiveProfileId,
    bool? OnboardingCompleted = null)
{
    public static IpcConfig Empty { get; } = new(
        Array.Empty<IpcCurve>(), Array.Empty<IpcFanAssignment>(), Array.Empty<IpcSensorName>(),
        Array.Empty<IpcProfile>(), null);
}

/// <summary>Umschaltbares Setup: eigene Kurven + Lüfter→Kurve-Zuordnungen.</summary>
public sealed record IpcProfile(
    string Id,
    string Name,
    IReadOnlyList<IpcProfileAssignment> Assignments,
    IReadOnlyList<IpcCurve> Curves);

public sealed record IpcProfileAssignment(string FanId, string? CurveId);

/// <summary>Nutzer-Einstellungen eines Sensors: Anzeigename, Gruppe und Sichtbarkeit.</summary>
public sealed record IpcSensorName(string Id, string Name, string? Group = null, bool Hidden = false);

/// <summary>
/// Eine Kurve: Name, Quell-Sensoren, Aggregation, Hysterese, Glättung und Stützpunkte (°C → %).
/// <paramref name="SourceSensorId"/> bleibt für Abwärtskompatibilität mit älteren Daemons/GUIs erhalten;
/// die Schema-2-Quelle ist <paramref name="SourceSensorIds"/>. <paramref name="Aggregation"/> ist der
/// Name des Core-Enums (<c>Max</c>/<c>Avg</c>); ältere Gegenstellen kennen das Feld noch nicht (null).
/// </summary>
/// <param name="SmoothingSeconds">
/// Averaging window for the curve input in seconds, <c>0</c> = off. Nullable on purpose, like
/// <paramref name="Aggregation"/>: a peer that predates the field sends nothing, and the mappers resolve
/// <c>null</c> to the Core default instead of relying on how the JSON layer treats a missing value.
/// </param>
public sealed record IpcCurve(
    string Id,
    string Name,
    string SourceSensorId,
    double HysteresisC,
    IReadOnlyList<IpcCurvePoint> Points,
    IReadOnlyList<string>? SourceSensorIds = null,
    string? Aggregation = null,
    string? InterpolationMode = null,
    bool Enabled = true,
    double? SmoothingSeconds = null);

public sealed record IpcCurvePoint(double TemperatureC, double Percent);

/// <summary>Zuordnung + PWM-Grenzen eines Lüfters, samt Einbau-Position.</summary>
/// <param name="Calibration">Persistiertes Kalibrier-Ergebnis (Anlaufpunkt + Drehzahlbereich), vom Daemon
/// an die GUI gespiegelt, damit das „bereits kalibriert"-Badge auch nach einem Neustart erscheint. Im
/// Normalbetrieb (<c>SaveConfig</c>/Merge) fließt es nur Daemon → GUI und die GUI schickt es nicht zurück;
/// Ausnahme ist <c>ReplaceConfig</c> (Import/Restore), bei dem die GUI einen zuvor vom Daemon erzeugten Wert
/// originalgetreu zurückspiegelt. <c>null</c> = nicht kalibriert bzw. ältere Gegenstelle kennt das Feld nicht.</param>
/// <param name="RpmSource">Explizit zugeordneter Drehzahl-Sensor (Sensor-Id), der die Backend-Heuristik
/// überschreibt (manuelles Zuordnen / Auto-Kopplung). Wie <paramref name="Calibration"/> daemon-verwaltet:
/// nur Daemon → GUI gespiegelt (Merge bewahrt), außer bei <c>ReplaceConfig</c> (Restore). <c>null</c> =
/// keine Übersteuerung bzw. ältere Gegenstelle kennt das Feld nicht.</param>
public sealed record IpcFanAssignment(
    string FanId,
    string Name,
    int MinPwm,
    int MaxPwm,
    string? AssignedCurveId,
    string Location = "Unspecified",
    bool Hidden = false,
    IpcFanCalibration? Calibration = null,
    string? RpmSource = null);

/// <summary>
/// Persistiertes Kalibrier-Ergebnis eines Lüfters über die IPC-Grenze: Anlaufpunkt (<paramref name="StartPwm"/>)
/// und Drehzahlbereich. Die rohe Messreihe (<c>FanCalibration.Samples</c>) bleibt bewusst im Daemon - die GUI
/// braucht für Anzeige/Badge nur Anlaufpunkt und Bereich.
/// </summary>
public sealed record IpcFanCalibration(int StartPwm, int MinRpm, int MaxRpm);
