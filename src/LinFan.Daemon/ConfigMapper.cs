// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;

namespace LinFan.Daemon;

/// <summary>
/// Übersetzt zwischen der Core-<see cref="AppConfig"/> und dem IPC-Vertrag (<see cref="IpcConfig"/>)
/// und führt eine von der GUI gesendete Konfiguration autoritativ in die bestehende zusammen.
/// Der Daemon bleibt die einzige schreibende Instanz; Felder, die die GUI nicht bearbeitet
/// (Kalibrierung, FailSafeTempC, …), bleiben unangetastet.
/// </summary>
internal static class ConfigMapper
{
    public static IpcConfig ToIpc(AppConfig config) => new(
        config.Curves.Select(ToIpcCurve).ToList(),
        config.Fans.Select(f => new IpcFanAssignment(
            f.FanId, f.Name, f.MinPwm, f.MaxPwm, f.AssignedCurveId, f.Location.ToString(), f.Hidden,
            f.Calibration is { } cal ? new IpcFanCalibration(cal.StartPwm, cal.MinRpm, cal.MaxRpm) : null,
            f.RpmSource)).ToList(),
        config.Sensors.Select(s => new IpcSensorName(s.SensorId, s.Name, s.Group, s.Hidden)).ToList(),
        config.Profiles.Select(p => new IpcProfile(
            p.Id, p.Name,
            p.Assignments.Select(a => new IpcProfileAssignment(a.FanId, a.CurveId)).ToList(),
            p.Curves.Select(ToIpcCurve).ToList())).ToList(),
        config.ActiveProfileId,
        config.OnboardingCompleted);

    /// <summary>
    /// Übernimmt Kurven, Zuordnungen, Sensoren und Profile aus <paramref name="incoming"/>. Lüfter werden
    /// per <c>FanId</c> gematcht, damit Kalibrierung &amp; nicht editierte Felder erhalten bleiben;
    /// unbekannte Lüfter aus <paramref name="current"/> bleiben unverändert bestehen.
    /// </summary>
    public static AppConfig Merge(AppConfig current, IpcConfig incoming)
    {
        var curves = incoming.Curves.Select(ToCoreCurve).ToList();

        var byId = current.Fans.ToDictionary(f => f.FanId);
        foreach (IpcFanAssignment a in incoming.Fans)
        {
            FanConfig baseFan = byId.TryGetValue(a.FanId, out FanConfig? existing)
                ? existing
                : new FanConfig { FanId = a.FanId, Name = a.Name };

            byId[a.FanId] = baseFan with
            {
                Name = a.Name,
                MinPwm = (byte)Math.Clamp(a.MinPwm, 0, 255),
                MaxPwm = (byte)Math.Clamp(a.MaxPwm, 0, 255),
                AssignedCurveId = a.AssignedCurveId,
                Location = Enum.TryParse(a.Location, out FanLocation loc) ? loc : FanLocation.Unspecified,
                Hidden = a.Hidden,
            };
        }

        // Sensor-Namen/Gruppe/Sichtbarkeit übernehmen (nur wenn die GUI welche schickt).
        IReadOnlyList<SensorConfig> sensors = incoming.Sensors.Count == 0
            ? current.Sensors
            : incoming.Sensors
                .Select(s => new SensorConfig
                {
                    SensorId = s.Id,
                    Name = (s.Name ?? "").Trim(),
                    Group = string.IsNullOrWhiteSpace(s.Group) ? null : s.Group.Trim(),
                    Hidden = s.Hidden,
                })
                .ToList();

        var profiles = incoming.Profiles.Select(p => new Profile
        {
            Id = p.Id,
            Name = p.Name,
            Curves = p.Curves.Select(ToCoreCurve).ToList(),
            Assignments = p.Assignments.Select(a => new ProfileAssignment(a.FanId, a.CurveId)).ToList(),
        }).ToList();

        return current with
        {
            Curves = curves,
            Fans = byId.Values.ToList(),
            Sensors = sensors,
            Profiles = profiles,
            ActiveProfileId = incoming.ActiveProfileId,
            // Eine ältere GUI kennt das Feld nicht (null) und darf den autoritativen Daemon-Stand nicht
            // zurücksetzen; nur ein expliziter Wert (Assistent abgeschlossen/übersprungen) überschreibt.
            OnboardingCompleted = incoming.OnboardingCompleted ?? current.OnboardingCompleted,
        };
    }

    /// <summary>
    /// <b>Vollständiges Ersetzen</b> (Import/Restore) statt Merge: Kurven, Lüfter, Sensoren und Profile
    /// kommen ausschließlich aus <paramref name="incoming"/> — im <paramref name="current"/> vorhandene,
    /// aber nicht mitgelieferte Lüfter/Sensoren <b>entfallen</b>. Anders als <see cref="Merge"/> wird die
    /// <b>eingehende Kalibrierung übernommen</b> (ein Backup trägt sie; Restore auf gleicher Maschine soll
    /// sie wiederherstellen). Daemon-eigene, nicht über den IPC-Vertrag transportierte Felder
    /// (<see cref="AppConfig.FailSafeTempC"/>, <see cref="AppConfig.PollIntervalMs"/>,
    /// <see cref="AppConfig.SchemaVersion"/>) bleiben aus <paramref name="current"/> erhalten.
    /// </summary>
    public static AppConfig Replace(AppConfig current, IpcConfig incoming)
    {
        var fans = incoming.Fans.Select(a => new FanConfig
        {
            FanId = a.FanId,
            Name = a.Name,
            MinPwm = (byte)Math.Clamp(a.MinPwm, 0, 255),
            MaxPwm = (byte)Math.Clamp(a.MaxPwm, 0, 255),
            AssignedCurveId = a.AssignedCurveId,
            Location = Enum.TryParse(a.Location, out FanLocation loc) ? loc : FanLocation.Unspecified,
            Hidden = a.Hidden,
            // Restore trägt die Tacho-Zuordnung originalgetreu zurück (wie die Kalibrierung).
            RpmSource = string.IsNullOrWhiteSpace(a.RpmSource) ? null : a.RpmSource,
            Calibration = a.Calibration is { } cal
                ? new FanCalibration
                {
                    StartPwm = (byte)Math.Clamp(cal.StartPwm, 0, 255),
                    MinRpm = cal.MinRpm,
                    MaxRpm = cal.MaxRpm,
                }
                : null,
        }).ToList();

        var sensors = incoming.Sensors.Select(s => new SensorConfig
        {
            SensorId = s.Id,
            Name = (s.Name ?? "").Trim(),
            Group = string.IsNullOrWhiteSpace(s.Group) ? null : s.Group.Trim(),
            Hidden = s.Hidden,
        }).ToList();

        var profiles = incoming.Profiles.Select(p => new Profile
        {
            Id = p.Id,
            Name = p.Name,
            Curves = p.Curves.Select(ToCoreCurve).ToList(),
            Assignments = p.Assignments.Select(a => new ProfileAssignment(a.FanId, a.CurveId)).ToList(),
        }).ToList();

        return current with
        {
            Curves = incoming.Curves.Select(ToCoreCurve).ToList(),
            Fans = fans,
            Sensors = sensors,
            Profiles = profiles,
            ActiveProfileId = incoming.ActiveProfileId,
            OnboardingCompleted = incoming.OnboardingCompleted ?? current.OnboardingCompleted,
        };
    }

    /// <summary>
    /// Übernimmt ein Kalibrier-Ergebnis in den Lüfter <paramref name="fanId"/> (legt ihn an, falls er
    /// in <paramref name="current"/> fehlt). <see cref="FanConfig.MinPwm"/> wird nur gesetzt, wenn ein
    /// Anlaufpunkt gefunden wurde (<c>MinRpm &gt; 0</c>); sonst ist <c>StartPwm == 255</c> = „nicht
    /// angelaufen" und würde den Lüfter dauerhaft auf Volllast zwingen — dann bleibt MinPwm unverändert
    /// und es wird nur die Messreihe (<see cref="FanConfig.Calibration"/>) gespeichert (Fail-Safe).
    /// </summary>
    public static AppConfig ApplyCalibration(AppConfig current, string fanId, FanCalibration cal)
    {
        var fans = current.Fans.ToList();
        int idx = fans.FindIndex(f => f.FanId == fanId);
        FanConfig baseFan = idx >= 0 ? fans[idx] : NewFan(fanId);
        FanConfig updated = cal.MinRpm > 0
            ? baseFan with { Calibration = cal, MinPwm = cal.StartPwm }
            : baseFan with { Calibration = cal };
        if (idx >= 0) fans[idx] = updated; else fans.Add(updated);

        return current with { Fans = fans };
    }

    /// <summary>
    /// Setzt (oder löscht mit <paramref name="rpmSource"/> = <c>null</c>/leer) die explizite Tacho-Zuordnung
    /// eines Lüfters (<see cref="FanConfig.RpmSource"/>) — aus manuellem Zuordnen oder der Auto-Kopplung.
    /// Legt den Lüfter an, falls er in <paramref name="current"/> fehlt.
    /// </summary>
    public static AppConfig ApplyTachometer(AppConfig current, string fanId, string? rpmSource)
    {
        string? normalized = string.IsNullOrWhiteSpace(rpmSource) ? null : rpmSource.Trim();
        var fans = current.Fans.ToList();
        int idx = fans.FindIndex(f => f.FanId == fanId);
        FanConfig baseFan = idx >= 0 ? fans[idx] : NewFan(fanId);
        FanConfig updated = baseFan with { RpmSource = normalized };
        if (idx >= 0) fans[idx] = updated; else fans.Add(updated);

        return current with { Fans = fans };
    }

    /// <summary>
    /// New fan entry created by a daemon-side result (calibration / tach coupling) because the GUI has never
    /// saved this fan. <b>Without</b> a name: <see cref="FanConfig.Name"/> is the user's <i>own</i> name, and
    /// empty means "none" — then the hardware label applies everywhere. Putting the FanId here would count as
    /// a user-defined name and leave the raw path ("/lpc/nct6797d/0/control/1") stuck as the display name.
    /// </summary>
    private static FanConfig NewFan(string fanId) => new() { FanId = fanId };

    private static IpcCurve ToIpcCurve(CurveConfig c)
    {
        // Schema-2-Quelle bevorzugen; ist nur das alte Einzelfeld gesetzt, daraus ableiten (Migration).
        IReadOnlyList<string> sources = CurveSourceResolver.ResolveSources(c.SourceSensorId, c.SourceSensorIds);

        return new IpcCurve(
            c.Id, c.Name,
            // Erster Sensor ins alte Feld spiegeln, damit ein älterer GUI-Client weiter eine Quelle sieht.
            sources.Count > 0 ? sources[0] : "",
            c.HysteresisC,
            c.Points.Select(p => new IpcCurvePoint(p.TemperatureC, p.Percent)).ToList(),
            sources,
            c.Aggregation.ToString(),
            c.InterpolationMode.ToString(),
            c.Enabled);
    }

    private static CurveConfig ToCoreCurve(IpcCurve c)
    {
        // Schema-2-Quelle bevorzugen; sonst (älterer GUI-Client) aus dem alten Einzelfeld migrieren.
        IReadOnlyList<string> sources = CurveSourceResolver.ResolveSources(c.SourceSensorId, c.SourceSensorIds);

        return new CurveConfig
        {
            Id = c.Id,
            Name = c.Name,
            Enabled = c.Enabled,
            SourceSensorIds = sources,
            Aggregation = CurveSourceResolver.ParseAggregation(c.Aggregation),
            HysteresisC = c.HysteresisC,
            InterpolationMode = CurveSourceResolver.ParseEnum(c.InterpolationMode, InterpolationMode.Linear),
            Points = c.Points.Select(p => new CurvePoint(p.TemperatureC, p.Percent)).ToList(),
        };
    }
}
