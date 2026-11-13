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
            f.Calibration is { } cal ? ToIpcCalibration(cal) : null,
            f.RpmSource)).ToList(),
        config.Sensors.Select(s => new IpcSensorName(s.SensorId, s.Name, s.Group, s.Hidden)).ToList(),
        config.Profiles.Select(p => new IpcProfile(
            p.Id, p.Name,
            p.Assignments.Select(a => new IpcProfileAssignment(a.FanId, a.CurveId)).ToList(),
            p.Curves.Select(ToIpcCurve).ToList())).ToList(),
        config.ActiveProfileId,
        config.OnboardingCompleted,
        config.SensorGroups,
        config.CaseType.ToString());

    /// <summary>
    /// Übernimmt Kurven, Zuordnungen, Sensoren und Profile aus <paramref name="incoming"/>. Lüfter werden
    /// per <c>FanId</c> gematcht, damit Kalibrierung &amp; nicht editierte Felder erhalten bleiben;
    /// unbekannte Lüfter aus <paramref name="current"/> bleiben unverändert bestehen. <c>MinPwm</c> is merged three
    /// ways against <see cref="IpcFanAssignment.BaseMinPwm"/>: a value the sender did not change leaves the daemon's
    /// own in place.
    /// </summary>
    public static AppConfig Merge(AppConfig current, IpcConfig incoming)
    {
        var curves = incoming.Curves.Select(ToCoreCurve).ToList();

        var byId = current.Fans.ToDictionary(f => f.FanId);
        foreach (IpcFanAssignment a in incoming.Fans)
        {
            FanConfig? existing = byId.GetValueOrDefault(a.FanId);
            FanConfig baseFan = existing ?? new FanConfig { FanId = a.FanId, Name = a.Name };

            byte minPwm = ClampPwm(a.MinPwm);
            // The calibration sets MinPwm in the daemon while the GUI may still edit an older state; that save would
            // otherwise write the old, lower start point back over the calibrated one.
            if (existing is not null && a.BaseMinPwm is int sentFrom && ClampPwm(sentFrom) == minPwm)
                minPwm = existing.MinPwm;

            byId[a.FanId] = baseFan with
            {
                Name = a.Name,
                MinPwm = minPwm,
                MaxPwm = ClampPwm(a.MaxPwm),
                AssignedCurveId = a.AssignedCurveId,
                Location = CurveSourceResolver.ParseEnum(a.Location, FanLocation.Unspecified),
                Hidden = a.Hidden,
            };
        }

        // Names, groups and visibility come from the GUI, which sends the sensors it detects; an entry for one it does not
        // (a device that is off right now) stays, as unknown fans do - its name, group and visibility come back with it.
        // A sent sensor keeps what the daemon owns of its entry (the recorded device), as a fan does.
        var currentSensors = new Dictionary<string, SensorConfig>(StringComparer.Ordinal);
        foreach (SensorConfig s in current.Sensors)
            if (s.SensorId is not null)
                currentSensors.TryAdd(s.SensorId, s);
        var sent = incoming.Sensors
            .Select(s => (s.Id is not null && currentSensors.TryGetValue(s.Id, out SensorConfig? existing)
                    ? existing
                    : new SensorConfig { SensorId = s.Id! }) with
            {
                Name = (s.Name ?? "").Trim(),
                Group = string.IsNullOrWhiteSpace(s.Group) ? null : s.Group.Trim(),
                Hidden = s.Hidden,
            })
            .ToList();
        var sentIds = sent.Select(s => s.SensorId).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<SensorConfig> sensors = sent.Concat(current.Sensors.Where(s => !sentIds.Contains(s.SensorId))).ToList();

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
            // A GUI that predates the field sends null and must not wipe the groups another client set up.
            SensorGroups = incoming.SensorGroups ?? current.SensorGroups,
            // Likewise for the case type; a name this daemon does not know keeps its own as well.
            CaseType = CurveSourceResolver.ParseEnum(incoming.CaseType, current.CaseType),
            Profiles = profiles,
            ActiveProfileId = incoming.ActiveProfileId,
            // Eine ältere GUI kennt das Feld nicht (null) und darf den autoritativen Daemon-Stand nicht
            // zurücksetzen; nur ein expliziter Wert (Assistent abgeschlossen/übersprungen) überschreibt.
            OnboardingCompleted = incoming.OnboardingCompleted ?? current.OnboardingCompleted,
        };
    }

    /// <summary>
    /// <b>Vollständiges Ersetzen</b> (Import/Restore) statt Merge: Kurven, Lüfter, Sensoren und Profile
    /// kommen ausschließlich aus <paramref name="incoming"/> - im <paramref name="current"/> vorhandene,
    /// aber nicht mitgelieferte Lüfter/Sensoren <b>entfallen</b>. Anders als <see cref="Merge"/> wird die
    /// <b>eingehende Kalibrierung übernommen</b> (ein Backup trägt sie; Restore auf gleicher Maschine soll
    /// sie wiederherstellen). Daemon-eigene, nicht über den IPC-Vertrag transportierte Felder
    /// (<see cref="AppConfig.FailSafeTempC"/>, <see cref="AppConfig.PollIntervalMs"/>,
    /// <see cref="AppConfig.SchemaVersion"/>) bleiben aus <paramref name="current"/> erhalten.
    /// <para>
    /// So does the recorded device (<see cref="FanConfig.Device"/>, <see cref="SensorConfig.Device"/>) of every entry whose
    /// id <paramref name="current"/> already has - the first entry per id, as in the sanitizer: a backup carries none, and
    /// an import resolves no device change.
    /// </para>
    /// <para>
    /// The calibration comes with the sensor it measured with and its series, which the daemon compares the fan's speed
    /// with: an import of the same machine's backup keeps that comparison.
    /// </para>
    /// </summary>
    public static AppConfig Replace(AppConfig current, IpcConfig incoming)
    {
        var fans = incoming.Fans.Select(a => new FanConfig
        {
            FanId = a.FanId,
            Name = a.Name,
            MinPwm = ClampPwm(a.MinPwm),
            MaxPwm = ClampPwm(a.MaxPwm),
            AssignedCurveId = a.AssignedCurveId,
            Location = CurveSourceResolver.ParseEnum(a.Location, FanLocation.Unspecified),
            Hidden = a.Hidden,
            // Restore trägt die Tacho-Zuordnung originalgetreu zurück (wie die Kalibrierung).
            RpmSource = string.IsNullOrWhiteSpace(a.RpmSource) ? null : a.RpmSource.Trim(),
            Calibration = a.Calibration is { } cal ? ToCoreCalibration(cal) : null,
            Device = current.Fans.FirstOrDefault(f => f.FanId == a.FanId)?.Device,
        }).ToList();

        var sensors = incoming.Sensors.Select(s => new SensorConfig
        {
            SensorId = s.Id,
            Name = (s.Name ?? "").Trim(),
            Group = string.IsNullOrWhiteSpace(s.Group) ? null : s.Group.Trim(),
            Hidden = s.Hidden,
            Device = current.Sensors.FirstOrDefault(c => c.SensorId == s.Id)?.Device,
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
            SensorGroups = incoming.SensorGroups ?? Array.Empty<string>(),
            CaseType = CurveSourceResolver.ParseEnum(incoming.CaseType, CaseType.Tower),
            Profiles = profiles,
            ActiveProfileId = incoming.ActiveProfileId,
            OnboardingCompleted = incoming.OnboardingCompleted ?? current.OnboardingCompleted,
        };
    }

    /// <summary>
    /// Factory reset of what the GUI edits: curves, fans (with calibration and tach override), sensors, sensor groups
    /// and profiles are cleared, onboarding counts as done. Like <see cref="Replace"/>, the daemon-owned fields
    /// outside the IPC contract (<see cref="AppConfig.FailSafeTempC"/>, <see cref="AppConfig.PollIntervalMs"/>,
    /// <see cref="AppConfig.SchemaVersion"/>) stay from <paramref name="current"/>: an operator's stricter
    /// over-temperature limit must not fall back to the default because any client asked for a reset.
    /// </summary>
    public static AppConfig Reset(AppConfig current) => current with
    {
        Curves = Array.Empty<CurveConfig>(),
        Fans = Array.Empty<FanConfig>(),
        Sensors = Array.Empty<SensorConfig>(),
        SensorGroups = Array.Empty<string>(),
        CaseType = CaseType.Tower,
        Profiles = Array.Empty<Profile>(),
        ActiveProfileId = null,
        OnboardingCompleted = true,
    };

    /// <summary>
    /// Übernimmt ein Kalibrier-Ergebnis in den Lüfter <paramref name="fanId"/> (legt ihn an, falls er
    /// in <paramref name="current"/> fehlt). <see cref="FanConfig.MinPwm"/> wird nur gesetzt, wenn ein
    /// Anlaufpunkt gefunden wurde (<c>MinRpm &gt; 0</c>); sonst ist <c>StartPwm == 255</c> = „nicht
    /// angelaufen" und würde den Lüfter dauerhaft auf Volllast zwingen - dann bleibt MinPwm unverändert
    /// und es wird nur die Messreihe (<see cref="FanConfig.Calibration"/>) gespeichert (Fail-Safe).
    /// <para>
    /// A calibration that found a start point measured the device behind the id now (<paramref name="device"/>, from
    /// its descriptor), and its MinPwm belongs to that device: the entry records it from here on - also where it recorded
    /// another, which takes the fan out of a reported replacement. Without a start point MinPwm stays the old device's,
    /// so the recorded device stays too and the replacement is still reported; likewise without a device the backend
    /// names.
    /// </para>
    /// </summary>
    public static AppConfig ApplyCalibration(AppConfig current, string fanId, FanCalibration cal, HardwareDevice? device)
    {
        var fans = current.Fans.ToList();
        int idx = fans.FindIndex(f => f.FanId == fanId);
        FanConfig baseFan = idx >= 0 ? fans[idx] : NewFan(fanId);
        FanConfig updated = cal.MinRpm > 0
            ? baseFan with
            {
                Calibration = cal,
                MinPwm = cal.StartPwm,
                Device = device ?? baseFan.Device,
            }
            : baseFan with { Calibration = cal };
        if (idx >= 0) fans[idx] = updated; else fans.Add(updated);

        return current with { Fans = fans };
    }

    /// <summary>
    /// Sets (or, with a <c>null</c> or blank <paramref name="rpmSource"/>, clears) a fan's own speed-sensor coupling
    /// (<see cref="FanConfig.RpmSource"/>) - assigned by hand or measured by the tach coupling - and adds the fan if
    /// <paramref name="current"/> lacks it. One sensor, one fan: a fan that was coupled to the sensor loses that
    /// coupling, and <paramref name="previousFanId"/> names it.
    /// </summary>
    public static AppConfig ApplyTachometer(AppConfig current, string fanId, string? rpmSource, out string? previousFanId)
    {
        string? normalized = string.IsNullOrWhiteSpace(rpmSource) ? null : rpmSource.Trim();
        var fans = current.Fans.ToList();
        int idx = fans.FindIndex(f => f.FanId == fanId);
        FanConfig baseFan = idx >= 0 ? fans[idx] : NewFan(fanId);
        FanConfig updated = baseFan with { RpmSource = normalized };
        if (idx >= 0) fans[idx] = updated; else fans.Add(updated);

        previousFanId = null;
        for (int i = 0; normalized is not null && i < fans.Count; i++)
        {
            if (fans[i].FanId == fanId || fans[i].RpmSource != normalized)
                continue;
            previousFanId ??= fans[i].FanId;
            fans[i] = fans[i] with { RpmSource = null };
        }

        return current with { Fans = fans };
    }

    /// <summary>
    /// New fan entry created by a daemon-side result (calibration / tach coupling) because the GUI has never
    /// saved this fan. <b>Without</b> a name: <see cref="FanConfig.Name"/> is the user's <i>own</i> name, and
    /// empty means "none" - then the hardware label applies everywhere. Putting the FanId here would count as
    /// a user-defined name and leave the raw path ("/lpc/nct6797d/0/control/1") stuck as the display name.
    /// </summary>
    private static FanConfig NewFan(string fanId) => new() { FanId = fanId };

    private static byte ClampPwm(int value) => (byte)Math.Clamp(value, 0, 255);

    private static IpcFanCalibration ToIpcCalibration(FanCalibration cal) => new(
        cal.StartPwm, cal.MinRpm, cal.MaxRpm, cal.Tachometer,
        cal.Samples.Select(s => new IpcCalibrationSample(s.Pwm, s.Rpm)).ToList());

    /// <summary>
    /// A sample whose PWM is no raw value is dropped rather than clamped: clamped, it would claim a speed at a PWM it was
    /// not measured at. A JSON <c>null</c> in the series is no sample either.
    /// </summary>
    private static FanCalibration ToCoreCalibration(IpcFanCalibration cal) => new()
    {
        StartPwm = ClampPwm(cal.StartPwm),
        MinRpm = cal.MinRpm,
        MaxRpm = cal.MaxRpm,
        Tachometer = string.IsNullOrWhiteSpace(cal.Tachometer) ? null : cal.Tachometer.Trim(),
        Samples = (cal.Samples ?? [])
            .Where(s => s is not null && s.Pwm is >= 0 and <= 255)
            .Select(s => new CalibrationSample((byte)s.Pwm, s.Rpm))
            .ToList(),
    };

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
            c.Enabled,
            c.SmoothingSeconds);
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
            // Ältere GUI ohne das Feld → Core-Default (die Glättung bleibt an, statt still auf 0 zu fallen).
            SmoothingSeconds = c.SmoothingSeconds ?? CurveConfig.DefaultSmoothingSeconds,
            InterpolationMode = CurveSourceResolver.ParseEnum(c.InterpolationMode, InterpolationMode.Linear),
            Points = c.Points.Select(p => new CurvePoint(p.TemperatureC, p.Percent)).ToList(),
        };
    }
}
