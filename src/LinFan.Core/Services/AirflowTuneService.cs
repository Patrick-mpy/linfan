// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Leitet aus den Einbau-Positionen der Lüfter (<see cref="FanConfig.Location"/>) einen Airflow-Vorschlag
/// ab: eine grobe Gehäuse-Druckbilanz und rollenbasierte Default-Kurven + Zuordnungen. Reine,
/// seiteneffektfreie Funktion über eine <see cref="AppConfig"/> – kein Hardware-/IPC-/UI-Zugriff
/// (Muster wie <see cref="ProfileService"/> / <see cref="DefaultProfiles"/>). Die GUI rechnet damit lokal
/// einen Vorschlag, zeigt ihn an und schreibt ihn bei „Übernehmen" über den normalen Speicherpfad zurück.
/// </summary>
/// <remarks>
/// <para><b>Sensor-Zuordnung ist eine Namens-Heuristik.</b> <see cref="SensorConfig"/> trägt keine Art
/// (CPU/GPU/Temp): diese Information stammt aus der Hardware-Discovery, nicht aus der persistierten Config.
/// Quell-Sensoren werden daher über Namens-Schlüsselwörter geraten; schlägt das fehl, fällt der Vorschlag
/// auf den ersten verfügbaren Sensor zurück und setzt einen Hinweis.</para>
/// <para>Die Druckbilanz ist bewusst grob: ohne echte CFM-Werte dient die kalibrierte Max-RPM als Proxy,
/// und ohne Kalibrierung wird nur nach Anzahl gezählt.</para>
/// <para>Jede <see cref="FanLocation"/> ist in <c>Spec(...)</c> <b>an einer Stelle</b> auf Richtung, Rolle
/// und Klartext abgebildet; die Kurvenvorlage hängt an der <see cref="AirflowRole"/> (compiler-geprüfter
/// <c>switch</c>). Neue Positionen brauchen daher nur einen Eintrag, nicht fünf verstreute Zweige.</para>
/// </remarks>
public static class AirflowTuneService
{
    /// <summary>Schwelle für „nennenswert" mehr Einlass/Auslass (Verhältnis), darunter gilt es als ausgeglichen.</summary>
    private const double BalanceThreshold = 1.15;

    // ── Öffentliche API ─────────────────────────────────────────────────────────

    /// <summary>Bildet eine <see cref="FanLocation"/> auf ihre Luftstrom-Richtung ab (für die Druckbilanz).</summary>
    public static AirflowDirection DirectionOf(FanLocation location) => Spec(location).Direction;

    /// <summary>
    /// Analysiert die Lüfter-Positionen und liefert einen Vorschlag. Liest nur – schreibt nichts.
    /// </summary>
    public static AirflowTuneResult Analyze(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var hints = new List<string>();
        SensorSources sensors = ResolveSensors(config.Sensors, hints);

        (PressureParts pressure, IReadOnlyList<string> pressureHints) = ComputePressure(config.Fans);
        hints.AddRange(pressureHints);

        // Welche Rollen-Kurven werden tatsächlich gebraucht? (Netzteil → AirflowRole.None bekommt keine.)
        var usedRoles = new HashSet<AirflowRole>();
        foreach (FanConfig fan in config.Fans)
        {
            AirflowRole role = Spec(fan.Location).Role;
            if (role != AirflowRole.None)
                usedRoles.Add(role);
        }

        var curves = usedRoles
            .OrderBy(r => r)
            .Select(r => BuildCurve(r, sensors))
            .ToList();

        if (config.Sensors.Count == 0 && curves.Count > 0)
            hints.Add("Keine Sensoren konfiguriert – Quell-Sensor der vorgeschlagenen Kurven bitte manuell wählen.");

        var fanSuggestions = config.Fans.Select(BuildSuggestion).ToList();

        return new AirflowTuneResult
        {
            Pressure = pressure.Balance,
            IntakeWeight = pressure.Intake,
            ExhaustWeight = pressure.Exhaust,
            Hints = hints,
            SuggestedCurves = curves,
            Fans = fanSuggestions,
        };
    }

    /// <summary>
    /// Schränkt einen Vorschlag auf die übergebenen Lüfter ein (für die selektive Übernahme in der GUI –
    /// der Nutzer kreuzt einzelne Lüfter ab): behält nur deren Pro-Lüfter-Vorschläge und die davon
    /// referenzierten Kurven. Rein, ohne Seiteneffekte.
    /// </summary>
    public static AirflowTuneResult FilterToFans(AirflowTuneResult result, IEnumerable<string> fanIds)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(fanIds);

        var keep = fanIds.ToHashSet(StringComparer.Ordinal);
        var fans = result.Fans.Where(f => keep.Contains(f.FanId)).ToList();
        var usedCurveIds = fans.Select(f => f.SuggestedCurveId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var curves = result.SuggestedCurves.Where(c => usedCurveIds.Contains(c.Id)).ToList();

        return result with { Fans = fans, SuggestedCurves = curves };
    }

    /// <summary>
    /// Übernimmt einen Vorschlag in eine neue <see cref="AppConfig"/>: fügt die vorgeschlagenen Kurven ein
    /// (ersetzt gleiche Ids) und setzt je Lüfter die <see cref="FanConfig.AssignedCurveId"/>. Ändert
    /// <b>nur</b> Kurven und Zuordnungen – Kalibrierung, PWM-Grenzen, Sichtbarkeit, Position und Gruppe
    /// bleiben unangetastet. Existiert ein aktives Profil, wird es mitgeführt (damit ein späteres
    /// <see cref="ProfileService.Apply"/> den Vorschlag nicht verwirft). Idempotent.
    /// </summary>
    public static AppConfig Apply(AppConfig config, AirflowTuneResult result)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(result);

        var assignMap = result.Fans.ToDictionary(s => s.FanId, s => s.SuggestedCurveId);

        var fans = config.Fans
            .Select(f => assignMap.TryGetValue(f.FanId, out string? curveId) ? f with { AssignedCurveId = curveId } : f)
            .ToList();

        IReadOnlyList<Profile> profiles = config.Profiles;
        if (config.ActiveProfileId is { } activeId && profiles.Any(p => p.Id == activeId))
        {
            profiles = profiles
                .Select(p => p.Id == activeId
                    ? p with
                    {
                        Curves = MergeCurves(p.Curves, result.SuggestedCurves),
                        Assignments = UpsertAssignments(p.Assignments, result.Fans),
                    }
                    : p)
                .ToList();
        }

        return config with
        {
            Curves = MergeCurves(config.Curves, result.SuggestedCurves),
            Fans = fans,
            Profiles = profiles,
        };
    }

    // ── Position → (Richtung, Rolle, Klartext): die einzige Stelle pro Position ───

    /// <summary>Rolle eines Lüfters für die Kurvenwahl; <see cref="None"/> = bekommt keine Software-Kurve.</summary>
    private enum AirflowRole
    {
        None,
        Cpu,
        Gpu,
        Intake,
        Exhaust,
        Default,
    }

    /// <summary>Was eine <see cref="FanLocation"/> für den Tune bedeutet (Richtung, Rolle, Klartext-Label).</summary>
    private sealed record LocationSpec(AirflowDirection Direction, AirflowRole Role, string Label);

    /// <summary>
    /// Einzige Quelle pro Position: Richtung (für die Druckbilanz), Rolle (für die Kurve) und Klartext.
    /// Der <c>_</c>-Zweig fängt nur undefinierte Enum-Werte ab (z. B. hand-editierte Config) → sichere Defaults.
    /// </summary>
    private static LocationSpec Spec(FanLocation location) => location switch
    {
        FanLocation.CpuCooler => new(AirflowDirection.Internal, AirflowRole.Cpu, "CPU-Kühler"),
        FanLocation.Radiator => new(AirflowDirection.Internal, AirflowRole.Cpu, "Radiator (folgt der CPU-/Flüssigkeitstemperatur)"),
        FanLocation.GpuCooler => new(AirflowDirection.Internal, AirflowRole.Gpu, "GPU-Kühler"),
        FanLocation.CaseFrontIntake => new(AirflowDirection.Intake, AirflowRole.Intake, "Gehäuse-Einlass vorn"),
        FanLocation.CaseBottomIntake => new(AirflowDirection.Intake, AirflowRole.Intake, "Gehäuse-Einlass unten"),
        FanLocation.CaseSideIntake => new(AirflowDirection.Intake, AirflowRole.Intake, "Gehäuse-Einlass seitlich"),
        FanLocation.CaseTopIntake => new(AirflowDirection.Intake, AirflowRole.Intake, "Gehäuse-Einlass oben"),
        FanLocation.CaseRearIntake => new(AirflowDirection.Intake, AirflowRole.Intake, "Gehäuse-Einlass hinten"),
        FanLocation.CaseRearExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, "Gehäuse-Auslass hinten"),
        FanLocation.CaseTopExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, "Gehäuse-Auslass oben"),
        FanLocation.CaseFrontExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, "Gehäuse-Auslass vorn"),
        FanLocation.CaseBottomExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, "Gehäuse-Auslass unten"),
        FanLocation.CaseSideExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, "Gehäuse-Auslass seitlich"),
        FanLocation.Psu => new(AirflowDirection.Internal, AirflowRole.None, "Netzteil bleibt auf Hardware-Auto (keine Software-Kurve)."),
        FanLocation.Unspecified => new(AirflowDirection.Unknown, AirflowRole.Default, ""),
        FanLocation.Other => new(AirflowDirection.Unknown, AirflowRole.Default, ""),
        _ => new(AirflowDirection.Unknown, AirflowRole.Default, ""),
    };

    // ── Druckbilanz ─────────────────────────────────────────────────────────────

    /// <summary>Zwischenergebnis der Druckbilanz (innerhalb des Service).</summary>
    private readonly record struct PressureParts(PressureBalance Balance, double Intake, double Exhaust);

    private static (PressureParts, IReadOnlyList<string>) ComputePressure(IReadOnlyList<FanConfig> fans)
    {
        var hints = new List<string>();

        var caseFans = fans
            .Select(f => (Fan: f, Dir: DirectionOf(f.Location)))
            .Where(x => x.Dir is AirflowDirection.Intake or AirflowDirection.Exhaust)
            .ToList();

        if (caseFans.Count == 0)
        {
            hints.Add("Keine Gehäuselüfter mit Ein-/Auslass-Position – Druckbilanz nicht bestimmbar.");
            return (new PressureParts(PressureBalance.Unknown, 0, 0), hints);
        }

        // Gewicht = kalibrierte Max-RPM (grober CFM-Proxy), sonst Anzahl. Nur einheitlich anwenden:
        // sobald ein Gehäuselüfter unkalibriert ist, zählen wir alle nach Anzahl (1), um Einheiten nicht zu mischen.
        bool allCalibrated = caseFans.All(x => x.Fan.Calibration is { MaxRpm: > 0 });
        if (!allCalibrated)
            hints.Add("Ohne vollständige Kalibrierung nur nach Lüfter-Anzahl geschätzt (kein Drehzahl-Proxy).");

        double Weight((FanConfig Fan, AirflowDirection Dir) x) =>
            allCalibrated ? x.Fan.Calibration!.MaxRpm : 1.0;

        double intake = caseFans.Where(x => x.Dir == AirflowDirection.Intake).Sum(Weight);
        double exhaust = caseFans.Where(x => x.Dir == AirflowDirection.Exhaust).Sum(Weight);

        if (intake <= 0)
            hints.Add("Kein Einlasslüfter erkannt – Frischluftzufuhr prüfen.");
        if (exhaust <= 0)
            hints.Add("Kein Auslasslüfter erkannt – Wärmeabfuhr prüfen.");

        PressureBalance balance = Classify(intake, exhaust);
        if (balance == PressureBalance.Negative)
            hints.Add("Unterdruck: mehr Auslass als Einlass – mehr Einlass erwägen oder Auslass drosseln (zieht sonst Staub durch Ritzen).");

        return (new PressureParts(balance, intake, exhaust), hints);
    }

    private static PressureBalance Classify(double intake, double exhaust)
    {
        if (intake <= 0 && exhaust <= 0)
            return PressureBalance.Unknown;
        if (exhaust <= 0)
            return PressureBalance.Positive; // nur Einlass
        if (intake <= 0)
            return PressureBalance.Negative; // nur Auslass

        double ratio = intake / exhaust;
        if (ratio >= BalanceThreshold)
            return PressureBalance.Positive;
        if (ratio <= 1.0 / BalanceThreshold)
            return PressureBalance.Negative;
        return PressureBalance.Balanced;
    }

    // ── Pro-Lüfter-Vorschlag ────────────────────────────────────────────────────

    private static AirflowFanSuggestion BuildSuggestion(FanConfig fan)
    {
        LocationSpec spec = Spec(fan.Location);

        // Netzteil (und jede andere None-Rolle): auf Hardware-Auto lassen, keine Kurve.
        if (spec.Role == AirflowRole.None)
            return Suggestion(fan, spec.Direction, curveId: null, spec.Label);

        RoleCurve curve = CurveFor(spec.Role);
        string reason = fan.Location is FanLocation.Unspecified or FanLocation.Other
            ? "Keine Position angegeben – neutrale Standardkurve; für besseren Vorschlag Position setzen."
            : $"{spec.Label} → Kurve: {curve.Name}";

        return Suggestion(fan, spec.Direction, curve.Id, reason);
    }

    private static AirflowFanSuggestion Suggestion(FanConfig fan, AirflowDirection dir, string? curveId, string reason) =>
        new()
        {
            FanId = fan.FanId,
            Location = fan.Location,
            Direction = dir,
            SuggestedCurveId = curveId,
            Reason = reason,
        };

    // ── Rolle → Kurvenvorlage ───────────────────────────────────────────────────

    /// <summary>Vorlage einer rollenbasierten Kurve: stabile Id, Anzeigename, Stützpunkte.</summary>
    private sealed record RoleCurve(string Id, string Name, CurvePoint[] Points);

    /// <summary>Kurvenvorlage je Rolle. <see cref="AirflowRole.None"/> hat bewusst keine (wird nie erzeugt).</summary>
    private static RoleCurve CurveFor(AirflowRole role) => role switch
    {
        AirflowRole.Cpu => new("airflow-cpu", "Airflow · CPU/Radiator", CpuPoints),
        AirflowRole.Gpu => new("airflow-gpu", "Airflow · GPU", GpuPoints),
        AirflowRole.Intake => new("airflow-intake", "Airflow · Einlass", IntakePoints),
        AirflowRole.Exhaust => new("airflow-exhaust", "Airflow · Auslass", ExhaustPoints),
        AirflowRole.Default => new("airflow-default", "Airflow · Standard", DefaultPoints),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Rolle hat keine Kurvenvorlage (None bekommt keine Kurve)."),
    };

    // Stützpunkte je Rolle (monoton steigend, enden bei 100 % → bei hoher Temp volle Kühlung).
    private static readonly CurvePoint[] CpuPoints =
        [new(30, 20), new(50, 40), new(65, 65), new(78, 90), new(88, 100)];

    private static readonly CurvePoint[] GpuPoints =
        [new(35, 20), new(55, 45), new(70, 70), new(82, 95), new(90, 100)];

    private static readonly CurvePoint[] IntakePoints =
        [new(30, 15), new(50, 30), new(65, 50), new(80, 80), new(90, 100)];

    // Auslass liegt bewusst leicht über dem Einlass (zieht Wärme aktiv heraus, hält die Bilanz neutral/positiv).
    private static readonly CurvePoint[] ExhaustPoints =
        [new(30, 20), new(50, 38), new(65, 60), new(80, 90), new(90, 100)];

    private static readonly CurvePoint[] DefaultPoints =
        [new(30, 20), new(50, 35), new(65, 55), new(80, 90), new(90, 100)];

    private static CurveConfig BuildCurve(AirflowRole role, SensorSources sensors)
    {
        RoleCurve curve = CurveFor(role);
        return new CurveConfig
        {
            Id = curve.Id,
            Name = curve.Name,
            SourceSensorIds = sensors.For(role),
            Aggregation = SensorAggregation.Max,
            InterpolationMode = InterpolationMode.Linear,
            HysteresisC = 2.0,
            Points = curve.Points,
        };
    }

    // ── Sensor-Heuristik ────────────────────────────────────────────────────────

    private static readonly string[] CpuKeywords =
        ["cpu", "package", "tctl", "tdie", "coretemp", "k10temp", "core"];

    private static readonly string[] GpuKeywords =
        ["gpu", "amdgpu", "radeon", "nvidia", "geforce", "edge", "junction"];

    private static SensorSources ResolveSensors(IReadOnlyList<SensorConfig> sensors, List<string> hints)
    {
        string? cpu = FindSensor(sensors, CpuKeywords);
        string? gpu = FindSensor(sensors, GpuKeywords);
        string? primary = cpu ?? gpu ?? sensors.FirstOrDefault()?.SensorId;

        if (sensors.Count > 0 && cpu is null)
            hints.Add("Kein CPU-Sensor per Name erkannt – Quelle der CPU-Kurve bitte prüfen.");

        return new SensorSources(cpu, gpu, primary);
    }

    private static string? FindSensor(IReadOnlyList<SensorConfig> sensors, string[] keywords) =>
        sensors.FirstOrDefault(s =>
            keywords.Any(k => s.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))?.SensorId;

    /// <summary>Aufgelöste Quell-Sensoren (per Namens-Heuristik) und die Quellenliste je Rolle.</summary>
    private readonly record struct SensorSources(string? Cpu, string? Gpu, string? Primary)
    {
        public IReadOnlyList<string> For(AirflowRole role) => role switch
        {
            AirflowRole.Cpu => Cpu is not null ? [Cpu] : Fallback(),
            AirflowRole.Gpu => Gpu is not null ? [Gpu] : Fallback(),
            _ => CaseSources(), // Intake/Exhaust/Default → heißeste relevante Quellen, Max-Aggregation
        };

        private IReadOnlyList<string> CaseSources()
        {
            var combined = new[] { Cpu, Gpu }.Where(s => s is not null).Cast<string>().ToList();
            return combined.Count > 0 ? combined : Fallback();
        }

        private IReadOnlyList<string> Fallback() => Primary is not null ? [Primary] : [];
    }

    // ── Merge-Helfer (für Apply) ────────────────────────────────────────────────

    private static IReadOnlyList<CurveConfig> MergeCurves(IReadOnlyList<CurveConfig> existing, IReadOnlyList<CurveConfig> incoming)
    {
        var byId = incoming.ToDictionary(c => c.Id);
        var merged = existing.Select(c => byId.TryGetValue(c.Id, out CurveConfig? replacement) ? replacement : c).ToList();

        var existingIds = existing.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        merged.AddRange(incoming.Where(c => !existingIds.Contains(c.Id)));
        return merged;
    }

    private static IReadOnlyList<ProfileAssignment> UpsertAssignments(
        IReadOnlyList<ProfileAssignment> existing,
        IReadOnlyList<AirflowFanSuggestion> suggestions)
    {
        var map = suggestions.ToDictionary(s => s.FanId, s => s.SuggestedCurveId);
        var merged = existing
            .Select(a => map.TryGetValue(a.FanId, out string? curveId) ? a with { CurveId = curveId } : a)
            .ToList();

        var existingFans = existing.Select(a => a.FanId).ToHashSet(StringComparer.Ordinal);
        merged.AddRange(suggestions
            .Where(s => !existingFans.Contains(s.FanId))
            .Select(s => new ProfileAssignment(s.FanId, s.SuggestedCurveId)));
        return merged;
    }
}
