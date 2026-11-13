// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Plausibilisiert eine geladene <see cref="AppConfig"/>, bevor sie den Regel-Loop steuert.
/// Wichtigster Punkt ist der sicherheitskritische <see cref="AppConfig.FailSafeTempC"/>: eine von
/// Hand editierte Datei darf den Watchdog nicht faktisch abschalten (z. B. <c>200</c> °C oder <c>0</c>).
/// Reine Hardware-Sicherheit (PWM-Grenzen, Kurven-Clamping) ist bereits durch <c>byte</c>-Typen und
/// das Clamping in <see cref="CurveEngine"/>/<see cref="ControlLoop"/> abgedeckt.
/// </summary>
public static class ConfigSanitizer
{
    public const double MinFailSafeC = 40.0;
    public const double MaxFailSafeC = 105.0;
    public const double DefaultFailSafeC = AppConfig.DefaultFailSafeTempC; // zentrale Quelle: AppConfig
    public const int MinPollIntervalMs = 200;

    /// <summary>
    /// The watchdog checks once per tick, so the interval is its reaction time: a value of an hour would leave
    /// over-temperature unchecked for that long.
    /// </summary>
    public const int MaxPollIntervalMs = 5000;
    public const double MaxSmoothingSeconds = TemperatureSmoother.MaxWindowSeconds; // zentrale Quelle: der Filter

    // The curve editor's ranges. The daemon enforces them on every curve it persists and relays, and the GUI
    // clamps relayed values to them as well: a curve one client saves reaches every other user's GUI, where an
    // out-of-range value (e.g. 1e30 into a decimal) used to end the process.
    public const double MaxHysteresisC = 20.0;
    public const double MinPointTempC = 0.0;
    public const double MaxPointTempC = 120.0;

    /// <summary>Liefert eine bereinigte Kopie und sammelt menschenlesbare Warnungen zu Korrekturen.</summary>
    public static AppConfig Sanitize(AppConfig config, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);
        var w = new List<string>();

        // First, so no warning below can carry a control character into the log.
        config = WithoutUnsafeText(config, w);

        double failSafe = config.FailSafeTempC;
        if (double.IsNaN(failSafe) || failSafe < MinFailSafeC || failSafe > MaxFailSafeC)
        {
            w.Add($"FailSafeTempC {failSafe:0.#} außerhalb [{MinFailSafeC:0}-{MaxFailSafeC:0}] °C - " +
                  $"auf sicheren Default {DefaultFailSafeC:0} °C gesetzt.");
            failSafe = DefaultFailSafeC;
        }

        int poll = config.PollIntervalMs;
        if (poll < MinPollIntervalMs)
        {
            w.Add($"PollIntervalMs {poll} < {MinPollIntervalMs} - auf {MinPollIntervalMs} gesetzt.");
            poll = MinPollIntervalMs;
        }
        else if (poll > MaxPollIntervalMs)
        {
            w.Add($"PollIntervalMs {poll} > {MaxPollIntervalMs} - auf {MaxPollIntervalMs} gesetzt.");
            poll = MaxPollIntervalMs;
        }

        CaseType caseType = config.CaseType;
        if (!Enum.IsDefined(caseType))
        {
            // A number in a hand-edited file; the type only shapes the airflow view, so the standard case is safe.
            w.Add($"Unbekannter Gehäuse-Typ {(int)caseType} - auf „Tower“ gesetzt.");
            caseType = CaseType.Tower;
        }

        IReadOnlyList<SensorConfig> sensors = DistinctById(
            WithIds(config.Sensors, s => s.SensorId, "Sensor", w), s => s.SensorId, "Sensor", w);
        // Unlike the other lists, a JSON null here reads as empty: the list only orders the display, and a hand edit
        // to it must not get the file set aside with all its curves.
        IReadOnlyList<string> sensorGroups = SanitizeSensorGroups(config.SensorGroups ?? [], w);
        IReadOnlyList<FanConfig> fans = WithUniqueTachometers(DistinctById(
            SanitizeFans(WithIds(config.Fans, f => f.FanId, "Lüfter", w), w), f => f.FanId, "Lüfter", w), w);
        IReadOnlyList<CurveConfig> curves = SanitizeCurves(config.Curves, w);
        IReadOnlyList<Profile> profiles = SanitizeProfiles(config.Profiles, w);

        warnings = w;
        // Auch ohne Warnung kann die Schema-1→2-Migration (SourceSensorId → SourceSensorIds) eine neue
        // Kurven-Liste erzeugt haben - dann darf nicht die alte Instanz zurückgegeben werden.
        bool changed = w.Count > 0
                       || !ReferenceEquals(sensorGroups, config.SensorGroups) // trimming a name warns nothing
                       || !ReferenceEquals(curves, config.Curves)
                       || !ReferenceEquals(profiles, config.Profiles);
        return changed
            ? config with
            {
                FailSafeTempC = failSafe,
                PollIntervalMs = poll,
                CaseType = caseType,
                Sensors = sensors,
                SensorGroups = sensorGroups,
                Fans = fans,
                Curves = curves,
                Profiles = profiles,
            }
            : config;
    }

    /// <summary>
    /// Trims the group names and drops blank ones and repeats - the name is the group's identity and
    /// <see cref="SensorConfig.Group"/> matches it case-insensitively, so "CPU" and "cpu" are one group (the first
    /// spelling wins). A group no sensor is in stays: an empty group is a regular state. Returns the same instance
    /// when nothing changed. The warnings only count: the names are client text and stay out of the log.
    /// </summary>
    private static IReadOnlyList<string> SanitizeSensorGroups(IReadOnlyList<string> groups, List<string> w)
    {
        var kept = new List<string>(groups.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int blank = 0, repeated = 0;
        foreach (string? raw in groups)
        {
            string name = raw?.Trim() ?? "";
            if (name.Length == 0)
                blank++;
            else if (seen.Add(name))
                kept.Add(name);
            else
                repeated++;
        }
        if (blank > 0)
            w.Add($"Sensorgruppen: {blank} Eintrag/Einträge ohne Namen entfernt.");
        if (repeated > 0)
            w.Add($"Sensorgruppen: {repeated} doppelte(r) Name(n) entfernt (erster gewinnt).");

        return kept.SequenceEqual(groups, StringComparer.Ordinal) ? groups : kept;
    }

    /// <summary>
    /// Profiles get the same checks as the top-level lists: <see cref="ProfileService.EnsureProfiles"/> copies the
    /// active profile's curves and assignments into the live config, so whatever is left unchecked here reaches
    /// the control loop, every snapshot and the next start unchecked.
    /// </summary>
    private static IReadOnlyList<Profile> SanitizeProfiles(IReadOnlyList<Profile> profiles, List<string> w)
    {
        IReadOnlyList<Profile> kept = DistinctById(WithIds(profiles, p => p.Id, "Profil", w), p => p.Id, "Profil", w);
        List<Profile>? result = null;
        for (int i = 0; i < kept.Count; i++)
        {
            Profile p = kept[i];
            string label = $"Profil '{p.Id}', Zuordnung";
            IReadOnlyList<ProfileAssignment> assignments = DistinctById(
                WithIds(p.Assignments, a => a.FanId, label, w), a => a.FanId, label, w);
            IReadOnlyList<CurveConfig> curves = SanitizeCurves(p.Curves, w);
            if (!ReferenceEquals(assignments, p.Assignments) || !ReferenceEquals(curves, p.Curves))
                (result ??= new List<Profile>(kept))[i] = p with { Assignments = assignments, Curves = curves };
        }
        return result ?? kept;
    }

    /// <summary>
    /// Strips control and format characters (<see cref="UntrustedText"/>) from every label and drops every reference
    /// that holds one - a client's names reach the log, the <c>monitor-ipc</c> terminal and every other user's GUI.
    /// A recorded device needs nothing here: <see cref="HardwareDevice"/> cleans its text itself, also when it is read
    /// from a hand-edited file. Ids that hold one are dropped by <see cref="WithIds"/>. The warnings only count: the text
    /// stays out of the log.
    /// </summary>
    private static AppConfig WithoutUnsafeText(AppConfig config, List<string> w)
    {
        int labels = 0, references = 0;

        string? Label(string? text)
        {
            if (UntrustedText.IsClean(text))
                return text;
            labels++;
            return UntrustedText.Strip(text!);
        }

        string? Reference(string? id)
        {
            if (UntrustedText.IsClean(id))
                return id;
            references++;
            return null;
        }

        IReadOnlyList<CurveConfig> Curves(IReadOnlyList<CurveConfig> curves) => Each(curves, c =>
            UntrustedText.IsClean(c.Name) && UntrustedText.IsClean(c.SourceSensorId)
            && c.SourceSensorIds.All(UntrustedText.IsClean)
                ? c
                : c with
                {
                    Name = Label(c.Name)!,
                    SourceSensorId = Reference(c.SourceSensorId),
                    SourceSensorIds = c.SourceSensorIds.Where(id => Reference(id) is not null).ToList(),
                });

        AppConfig clean = config with
        {
            Sensors = Each(config.Sensors, s => UntrustedText.IsClean(s.Name) && UntrustedText.IsClean(s.Group)
                ? s
                : s with { Name = Label(s.Name)!, Group = Label(s.Group) }),
            SensorGroups = config.SensorGroups is { } groups && !groups.All(UntrustedText.IsClean)
                ? groups.Select(g => Label(g)!).ToList()
                : config.SensorGroups!,
            Fans = Each(config.Fans, f =>
                UntrustedText.IsClean(f.Name) && UntrustedText.IsClean(f.AssignedCurveId) && UntrustedText.IsClean(f.RpmSource)
                && UntrustedText.IsClean(f.Calibration?.Tachometer)
                    ? f
                    : f with
                    {
                        Name = Label(f.Name)!,
                        AssignedCurveId = Reference(f.AssignedCurveId),
                        RpmSource = Reference(f.RpmSource),
                        Calibration = UntrustedText.IsClean(f.Calibration?.Tachometer)
                            ? f.Calibration
                            : f.Calibration! with { Tachometer = Reference(f.Calibration.Tachometer) },
                    }),
            Curves = Curves(config.Curves),
            Profiles = Each(config.Profiles, p =>
            {
                IReadOnlyList<CurveConfig> curves = Curves(p.Curves);
                IReadOnlyList<ProfileAssignment> assignments = Each(p.Assignments, a =>
                    UntrustedText.IsClean(a.CurveId) ? a : a with { CurveId = Reference(a.CurveId) });
                return UntrustedText.IsClean(p.Name) && ReferenceEquals(curves, p.Curves)
                       && ReferenceEquals(assignments, p.Assignments)
                    ? p
                    : p with { Name = Label(p.Name)!, Curves = curves, Assignments = assignments };
            }),
            ActiveProfileId = Reference(config.ActiveProfileId),
        };

        if (labels == 0 && references == 0)
            return config;
        if (labels > 0)
            w.Add($"{labels} Bezeichnung(en) mit Steuer-/Formatzeichen bereinigt.");
        if (references > 0)
            w.Add($"{references} Verweis(e) mit Steuer-/Formatzeichen entfernt.");
        return clean;
    }

    /// <summary>Applies <paramref name="clean"/> to each item; the same list instance when no item changed.</summary>
    private static IReadOnlyList<T> Each<T>(IReadOnlyList<T> items, Func<T, T> clean) where T : class
    {
        List<T>? result = null;
        for (int i = 0; i < items.Count; i++)
        {
            T item = items[i];
            T cleaned = item is null ? item! : clean(item);
            if (!ReferenceEquals(cleaned, item))
                (result ??= new List<T>(items))[i] = cleaned;
        }
        return result ?? items;
    }

    /// <summary>
    /// Drops entries without a valid id: none, blank, or holding a control or format character. The ids are
    /// dictionary keys further on (profile apply, snapshot), and a null key throws there - on the load
    /// path that used to stop the daemon from starting at all. The model declares them non-null, but a JSON
    /// <c>null</c> from an IPC client or a hand-edited file still gets in. An id with a control character would reach
    /// the log and the terminal of whoever runs <c>monitor-ipc</c>.
    /// </summary>
    private static IReadOnlyList<T> WithIds<T>(
        IReadOnlyList<T> items, Func<T, string?> idOf, string label, List<string> w)
    {
        int invalid = items.Count(i => !IsValidId(idOf(i)));
        if (invalid == 0)
            return items;

        w.Add($"{label}: {invalid} Eintrag/Einträge ohne gültige Id entfernt.");
        return items.Where(i => IsValidId(idOf(i))).ToList();
    }

    private static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && UntrustedText.IsClean(id);

    /// <summary>
    /// Entfernt Einträge mit doppelter Id (erster gewinnt). Doppelte Fan-/Sensor-Ids können durch eine
    /// von Hand editierte Datei oder eine IPC-Gegenstelle entstehen. Ein <c>ToDictionary</c> weiter oben (Snapshot-Bau, GUI) würde
    /// daran werfen und die App abstürzen lassen - hier einmalig, autoritär und mit Warnung bereinigt.
    /// Bei doppelfreier Eingabe wird dieselbe Instanz zurückgegeben (keine Allokation, keine „geändert"-Flags).
    /// </summary>
    private static IReadOnlyList<T> DistinctById<T>(
        IReadOnlyList<T> items, Func<T, string> idOf, string label, List<string> w)
    {
        List<T>? kept = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            string id = idOf(items[i]);
            if (seen.Add(id))
            {
                kept?.Add(items[i]);
                continue;
            }

            if (kept is null) // erstes Duplikat → ab hier materialisieren (Prefix ist dup-frei)
            {
                kept = new List<T>(items.Count);
                for (int j = 0; j < i; j++)
                    kept.Add(items[j]);
            }
            w.Add($"{label} '{id}': doppelter Eintrag entfernt (erster gewinnt).");
        }
        return kept ?? items;
    }

    /// <summary>
    /// Keeps MaxPwm where a curve can still run the fan: at least the calibrated start point, and - without one - above
    /// 0, which reads as „no cap" like an omitted value. Below that every curve value would be clamped to a power at
    /// which the fan stands still, while the overview shows it on its curve. A start point of 0 sets no floor: a fan that
    /// starts at 0 needs none, and a calibration from before the run-out phase may hold a 0 read from a fan still
    /// coasting from its previous speed. Then enforces
    /// <c>MinPwm ≤ MaxPwm</c> (otherwise the fan would stay on MinPwm for good). An undefined <see cref="FanConfig.Location"/> (a number in a
    /// hand-edited file - IPC already parses names only) becomes <see cref="FanLocation.Unspecified"/>, so it is never
    /// relayed to a GUI. A calibration's series loses samples with a negative speed, and a missing one reads as empty.
    /// </summary>
    private static IReadOnlyList<FanConfig> SanitizeFans(IReadOnlyList<FanConfig> fans, List<string> w)
    {
        List<FanConfig>? fixedFans = null;
        for (int i = 0; i < fans.Count; i++)
        {
            FanConfig f = fans[i];
            if (f.Calibration is { } cal && (cal.Samples is null || cal.Samples.Any(s => s.Rpm < 0)))
            {
                // A JSON null reads as no series. A negative speed is no reading; the samples around it still are.
                List<CalibrationSample> kept = (cal.Samples ?? []).Where(s => s.Rpm >= 0).ToList();
                w.Add(cal.Samples is null
                    ? $"Lüfter {f.FanId}: Kalibrier-Messreihe fehlt - als leer gelesen."
                    : $"Lüfter {f.FanId}: {cal.Samples.Count - kept.Count} Kalibrier-Messpunkt(e) mit negativer Drehzahl entfernt.");
                f = f with { Calibration = cal with { Samples = kept } };
            }
            if (f.Calibration is { StartPwm: var start and > 0 and not FanCalibration.NoSafeStart })
            {
                if (f.MaxPwm < start)
                {
                    w.Add($"Lüfter {f.FanId}: MaxPwm {f.MaxPwm} unter dem Anlaufpunkt {start} - MaxPwm auf {start} angehoben.");
                    f = f with { MaxPwm = start };
                }
            }
            else if (f.MaxPwm == 0)
            {
                w.Add($"Lüfter {f.FanId}: MaxPwm 0 hielte den Lüfter trotz Kurve still - als „kein Deckel“ (255) gelesen.");
                f = f with { MaxPwm = 255 };
            }
            if (f.MaxPwm < f.MinPwm)
            {
                w.Add($"Lüfter {f.FanId}: MaxPwm {f.MaxPwm} < MinPwm {f.MinPwm} - MaxPwm auf {f.MinPwm} angehoben.");
                f = f with { MaxPwm = f.MinPwm };
            }
            if (!Enum.IsDefined(f.Location))
            {
                w.Add($"Lüfter {f.FanId}: unbekannte Position {(int)f.Location} - auf „nicht zugeordnet“ gesetzt.");
                f = f with { Location = FanLocation.Unspecified };
            }
            if (!ReferenceEquals(f, fans[i]))
            {
                fixedFans ??= new List<FanConfig>(fans);
                fixedFans[i] = f;
            }
        }
        return fixedFans ?? fans;
    }

    /// <summary>
    /// One speed sensor, one fan: a sensor coupled to several fans stays with the first of them, the later ones lose their
    /// coupling - otherwise each would show, and calibrate with, the same fan's speed. Such a config comes from before the
    /// daemon enforced the rule, from an import or from a hand edit.
    /// </summary>
    private static IReadOnlyList<FanConfig> WithUniqueTachometers(IReadOnlyList<FanConfig> fans, List<string> w)
    {
        List<FanConfig>? fixedFans = null;
        var coupled = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < fans.Count; i++)
        {
            FanConfig f = fans[i];
            if (string.IsNullOrWhiteSpace(f.RpmSource) || coupled.Add(f.RpmSource))
                continue;
            w.Add($"Lüfter {f.FanId}: Drehzahl-Sensor {f.RpmSource} gehört schon einem anderen Lüfter - Zuordnung entfernt.");
            fixedFans ??= new List<FanConfig>(fans);
            fixedFans[i] = f with { RpmSource = null };
        }
        return fixedFans ?? fans;
    }

    /// <summary>
    /// Entfernt nicht-endliche Stützpunkte (NaN/∞), die das Sortieren/Interpolieren stören würden, und
    /// migriert das alte Einzel-Quellfeld (Schema 1) auf die Mehrfach-Quelle (Schema 2): ein gesetztes
    /// <see cref="CurveConfig.SourceSensorId"/> wandert in ein leeres <see cref="CurveConfig.SourceSensorIds"/>.
    /// Die Migration ist eine stille Normalisierung (keine Warnung) - ohne sie verlöre eine Altkurve
    /// ihren Quell-Sensor.
    /// <para>
    /// Points and hysteresis are clamped to the editor's ranges. A clamped point can share its temperature with
    /// another one; <see cref="CurveEngine"/> handles that like a step.
    /// </para>
    /// </summary>
    private static IReadOnlyList<CurveConfig> SanitizeCurves(IReadOnlyList<CurveConfig> curves, List<string> w)
    {
        // Curve ids key the GUI's lookups and the loop's per-tick input cache; the loop takes the first of a
        // duplicate, so keeping the first here changes nothing that runs.
        curves = DistinctById(WithIds(curves, c => c.Id, "Kurve", w), c => c.Id, "Kurve", w);
        List<CurveConfig>? fixedCurves = null;
        for (int i = 0; i < curves.Count; i++)
        {
            CurveConfig c = curves[i];
            CurveConfig migrated = MigrateCurveSources(c);
            string[] sources = migrated.SourceSensorIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (sources.Length != migrated.SourceSensorIds.Count)
            {
                w.Add($"Kurve '{c.Name}': Quell-Sensoren ohne Id oder doppelt entfernt.");
                migrated = migrated with { SourceSensorIds = sources };
            }

            var clean = migrated.Points
                .Where(p => double.IsFinite(p.TemperatureC) && double.IsFinite(p.Percent))
                .ToList();

            bool pointsDropped = clean.Count != migrated.Points.Count;
            if (pointsDropped)
                w.Add($"Kurve '{c.Name}': {migrated.Points.Count - clean.Count} ungültige Stützpunkte (NaN/∞) entfernt.");

            bool pointsClamped = false;
            for (int j = 0; j < clean.Count; j++)
            {
                var bounded = new CurvePoint(
                    Math.Clamp(clean[j].TemperatureC, MinPointTempC, MaxPointTempC),
                    Math.Clamp(clean[j].Percent, 0.0, 100.0));
                if (bounded != clean[j])
                {
                    clean[j] = bounded;
                    pointsClamped = true;
                }
            }
            if (pointsClamped)
                w.Add($"Kurve '{c.Name}': Stützpunkte außerhalb {MinPointTempC:0}-{MaxPointTempC:0} °C bzw. 0-100 % geklemmt.");

            double smoothing = SanitizeSmoothing(migrated, w);
            bool smoothingFixed = smoothing != migrated.SmoothingSeconds;
            double hysteresis = SanitizeHysteresis(migrated, w);
            bool hysteresisFixed = hysteresis != migrated.HysteresisC;

            if (!ReferenceEquals(migrated, c) || pointsDropped || pointsClamped || smoothingFixed || hysteresisFixed)
            {
                fixedCurves ??= new List<CurveConfig>(curves);
                CurveConfig fixedCurve = migrated;
                if (pointsDropped || pointsClamped)
                    fixedCurve = fixedCurve with { Points = clean };
                if (smoothingFixed)
                    fixedCurve = fixedCurve with { SmoothingSeconds = smoothing };
                if (hysteresisFixed)
                    fixedCurve = fixedCurve with { HysteresisC = hysteresis };
                fixedCurves[i] = fixedCurve;
            }
        }
        return fixedCurves ?? curves;
    }

    /// <summary>
    /// Keeps the smoothing window sane. NaN or a negative value falls back to the default; anything beyond
    /// <see cref="MaxSmoothingSeconds"/> is clamped - a window far past a heatsink's time constant would
    /// make the curve feel broken rather than calm. Not safety-critical (the watchdog reads raw values
    /// regardless), so an out-of-range value is corrected rather than rejected.
    /// </summary>
    private static double SanitizeSmoothing(CurveConfig c, List<string> w)
    {
        double seconds = c.SmoothingSeconds;
        if (double.IsNaN(seconds) || seconds < 0)
        {
            w.Add($"Kurve '{c.Name}': Glättung {seconds:0.#} s ungültig - auf " +
                  $"{CurveConfig.DefaultSmoothingSeconds:0.#} s gesetzt.");
            return CurveConfig.DefaultSmoothingSeconds;
        }
        if (seconds > MaxSmoothingSeconds)
        {
            w.Add($"Kurve '{c.Name}': Glättung {seconds:0.#} s > {MaxSmoothingSeconds:0} s - geklemmt.");
            return MaxSmoothingSeconds;
        }
        return seconds;
    }

    /// <summary>
    /// Keeps the hysteresis within the editor's range: NaN or a negative value falls back to the default, anything
    /// above <see cref="MaxHysteresisC"/> is clamped.
    /// </summary>
    private static double SanitizeHysteresis(CurveConfig c, List<string> w)
    {
        double h = c.HysteresisC;
        if (double.IsNaN(h) || h < 0)
        {
            w.Add($"Kurve '{c.Name}': Hysterese {h:0.#} °C ungültig - auf {CurveConfig.DefaultHysteresisC:0.#} °C gesetzt.");
            return CurveConfig.DefaultHysteresisC;
        }
        if (h > MaxHysteresisC)
        {
            w.Add($"Kurve '{c.Name}': Hysterese {h:0.#} °C > {MaxHysteresisC:0} °C - geklemmt.");
            return MaxHysteresisC;
        }
        return h;
    }

    /// <summary>Schema-1→2-Migration einer Kurve: leeres SourceSensorIds + gesetztes SourceSensorId → [SourceSensorId].</summary>
    private static CurveConfig MigrateCurveSources(CurveConfig c)
    {
        if (c.SourceSensorIds.Count == 0 && !string.IsNullOrEmpty(c.SourceSensorId))
            return c with { SourceSensorIds = new[] { c.SourceSensorId }, SourceSensorId = null };
        return c;
    }
}
