// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Leitet aus den Einbau-Positionen der Lüfter (<see cref="FanConfig.Location"/>) einen Airflow-Vorschlag
/// ab: eine grobe Gehäuse-Druckbilanz und rollenbasierte Default-Kurven + Zuordnungen. Reine,
/// seiteneffektfreie Funktion über eine <see cref="AppConfig"/> - kein Hardware-/IPC-/UI-Zugriff
/// (Muster wie <see cref="ProfileService"/> / <see cref="DefaultProfiles"/>). Die GUI rechnet damit lokal
/// einen Vorschlag je Profil, zeigt ihn an und schreibt ihn bei „Übernehmen" über den normalen Speicherpfad zurück.
/// </summary>
/// <remarks>
/// <para><b>Ausgeblendete Kanäle bleiben außen vor.</b> Lüfter/Sensoren mit <c>Hidden</c> sind für den
/// Nutzer „nicht vorhanden" (typisch: ein totes PWM-/Temp-Interface ohne Werte) - sie bekommen keinen
/// Vorschlag, zählen nicht in die Druckbilanz und taugen nicht als Kurvenquelle.</para>
/// <para><b>Sensor-Zuordnung ist eine Namens-/Gruppen-Heuristik.</b> <see cref="SensorConfig"/> trägt
/// keine Art (CPU/GPU/Temp): diese Information stammt aus der Hardware-Discovery, nicht aus der
/// persistierten Config. Source sensors are therefore guessed from keywords, name before group: a sensor's
/// group only counts for a component when no sensor matches that component by name (see <c>FindSensors</c>).
/// If nothing matches, the proposal falls back to the first available sensor and adds a hint. Komponenten-Kurven
/// (CPU/GPU) nehmen <i>alle</i> Treffer ihrer Komponente und mitteln sie (<see cref="SensorAggregation.Avg"/>):
/// Offset-Sensoren wie AMDs Tctl oder ein GPU-Hot-Spot liegen konstant deutlich über den übrigen Werten,
/// mit Max würde die Kurve nur diesen Offset abbilden.</para>
/// <para>Die Druckbilanz ist bewusst grob: ohne echte CFM-Werte dient die kalibrierte Max-RPM als Proxy,
/// und ohne Kalibrierung wird nur nach Anzahl gezählt.</para>
/// <para>The case type (<see cref="AppConfig.CaseType"/>) decides which case positions exist (<see cref="IsInCase"/>).
/// A fan at a position the type lacks keeps its role curve but stays out of the balance (a hint asks for a position);
/// an open bench and a laptop have no balance at all. Each fan counts by its own direction, so intake and exhaust may
/// share a side.</para>
/// <para>Jede <see cref="FanLocation"/> ist in <c>Spec(...)</c> <b>an einer Stelle</b> auf Richtung und
/// Rolle abgebildet; die Kurvenvorlage hängt an der <see cref="AirflowRole"/> (compiler-geprüfter
/// <c>switch</c>). Neue Positionen brauchen daher nur einen Eintrag, nicht fünf verstreute Zweige.</para>
/// <para><b>Keine Anzeigetexte im Core:</b> Hinweise und Begründungen sind Codes
/// (<see cref="AirflowHint"/> / <see cref="AirflowReason"/>), die die GUI übersetzt; die Namen der
/// vorgeschlagenen Kurven sind neutral englisch und können über <c>curveNames</c> (Key = stabile
/// Kurven-Id) lokalisiert übergeben werden - sie werden persistiert (Muster wie <see cref="DefaultProfiles"/>).</para>
/// </remarks>
public static class AirflowTuneService
{
    /// <summary>Schwelle für „nennenswert" mehr Einlass/Auslass (Verhältnis), darunter gilt es als ausgeglichen.</summary>
    private const double BalanceThreshold = 1.15;

    /// <summary>Intake share from which positive pressure counts as strong (about twice the exhaust flow).</summary>
    private const double StrongPositiveShare = 0.68;

    // Ids of the setup assistant's profiles - VariantFor gives a profile with one of them its variant again.
    private const string SilentProfileId = "silent";
    private const string BalancedProfileId = "balanced";
    private const string PerformanceProfileId = "performance";

    // ── Öffentliche API ─────────────────────────────────────────────────────────

    /// <summary>Bildet eine <see cref="FanLocation"/> auf ihre Luftstrom-Richtung ab (für die Druckbilanz).</summary>
    public static AirflowDirection DirectionOf(FanLocation location) => Spec(location).Direction;

    /// <summary>
    /// True if the location maps to a specific airflow role (not None/Default) - i.e. the analysis can
    /// derive a role-based curve from it. Trigger for the airflow-driven onboarding profiles.
    /// </summary>
    public static bool HasRoleSpecificCurve(FanLocation location) =>
        Spec(location).Role is not (AirflowRole.None or AirflowRole.Default);

    /// <summary>The side of the case a case position sits on; <c>null</c> for components, the fan wall and the rest.</summary>
    public static CaseSide? SideOf(FanLocation location) => Spec(location).Side;

    /// <summary>
    /// Whether <paramref name="location"/> exists in a case of <paramref name="caseType"/>. „Other" and „no position" exist
    /// everywhere, the components and the PSU everywhere but in a laptop (its CPU and GPU fans only). A tower has every
    /// case mount but no fan wall, a server only front, rear and its fan wall, a laptop every edge but the front (rear and
    /// side vents, the keyboard on top, the underside), an open bench no case mount at all.
    /// </summary>
    public static bool IsInCase(FanLocation location, CaseType caseType)
    {
        if (location == FanLocation.FanWall)
            return caseType == CaseType.Server;
        if (SideOf(location) is not { } side)
            return caseType != CaseType.Laptop || location is not (FanLocation.Radiator or FanLocation.Psu);
        return caseType switch
        {
            CaseType.Tower => true,
            CaseType.Server => side is CaseSide.Front or CaseSide.Rear,
            CaseType.Laptop => side != CaseSide.Front,
            _ => false,
        };
    }

    // Server only: the position blows against the usual front-to-rear flow (front exhaust, rear intake).
    private static bool IsAgainstFlow(FanLocation location, CaseType caseType) =>
        caseType == CaseType.Server
        && (SideOf(location), DirectionOf(location)) is (CaseSide.Front, AirflowDirection.Exhaust) or (CaseSide.Rear, AirflowDirection.Intake);

    // Stable ids of the role curves: a proposal finds a profile's role curve by them (names can be renamed), and the
    // GUI keys the curve names and role texts on them.
    public const string CpuCurveId = "airflow-cpu";
    public const string GpuCurveId = "airflow-gpu";
    public const string IntakeCurveId = "airflow-intake";
    public const string ExhaustCurveId = "airflow-exhaust";
    public const string DefaultCurveId = "airflow-default";

    /// <summary>
    /// Analysiert die Lüfter-Positionen und liefert einen Vorschlag. Liest nur - schreibt nichts.
    /// </summary>
    /// <param name="config">Die zu analysierende Konfiguration.</param>
    /// <param name="curveNames">Optionale Anzeigenamen der vorgeschlagenen Kurven, Key = stabile
    /// Kurven-Id (<c>airflow-cpu</c> …) - die GUI reicht hier die lokalisierte Fassung durch, da die
    /// Namen persistiert werden. Ohne Eintrag gilt der neutrale englische Default.</param>
    /// <param name="sensorLabels">The hardware label per sensor id (see <see cref="WithSensorLabels"/>).</param>
    public static AirflowTuneResult Analyze(
        AppConfig config, IReadOnlyDictionary<string, string>? curveNames = null,
        IReadOnlyDictionary<string, string>? sensorLabels = null)
        => Analyze(config, AirflowAggressiveness.Balanced, curveNames, sensorLabels);

    /// <summary>
    /// Variant of <see cref="Analyze(AppConfig, IReadOnlyDictionary{string, string}?, IReadOnlyDictionary{string, string}?)"/> with an explicit
    /// aggressiveness: same roles, ids, and sensor sources - only the point tables differ. The default
    /// overload forwards with <see cref="AirflowAggressiveness.Balanced"/>; the setup assistant builds one profile per
    /// variant, the settings page picks the variant per target profile (<see cref="VariantFor"/>).
    /// </summary>
    public static AirflowTuneResult Analyze(
        AppConfig config,
        AirflowAggressiveness aggressiveness,
        IReadOnlyDictionary<string, string>? curveNames = null,
        IReadOnlyDictionary<string, string>? sensorLabels = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        config = WithSensorLabels(config, sensorLabels);

        // Ausgeblendete Kanäle sind für den Nutzer „nicht vorhanden" - komplett aus der Analyse halten.
        var visibleFans = config.Fans.Where(f => !f.Hidden).ToList();
        var visibleSensors = config.Sensors.Where(s => !s.Hidden).ToList();

        var hints = new List<AirflowHint>();
        SensorSources sensors = ResolveSensors(visibleSensors, hints);

        (PressureParts pressure, IReadOnlyList<AirflowHint> pressureHints) = ComputePressure(visibleFans, config.CaseType);
        hints.AddRange(pressureHints);
        CaseLayout layout = AnalyzeLayout(visibleFans, config.CaseType);
        if (layout.OutOfCase.Count > 0)
            hints.Add(AirflowHint.PositionNotInCase);
        if (layout.MixedSides.Count > 0)
            hints.Add(AirflowHint.MixedSide);
        if (layout.AgainstFlow.Count > 0)
            hints.Add(AirflowHint.AgainstCaseFlow);

        // Welche Rollen-Kurven werden tatsächlich gebraucht? (Netzteil → AirflowRole.None bekommt keine.)
        var usedRoles = new HashSet<AirflowRole>();
        foreach (FanConfig fan in visibleFans)
        {
            AirflowRole role = Spec(fan.Location).Role;
            if (role != AirflowRole.None)
                usedRoles.Add(role);
        }

        var curves = usedRoles
            .OrderBy(r => r)
            .Select(r => BuildCurve(r, aggressiveness, sensors, curveNames))
            .ToList();

        if (visibleSensors.Count == 0 && curves.Count > 0)
            hints.Add(AirflowHint.NoSensorsConfigured);

        var fanSuggestions = visibleFans.Select(BuildSuggestion).ToList();

        return new AirflowTuneResult
        {
            CaseType = config.CaseType,
            Verdict = pressure.Verdict,
            IntakeShare = pressure.Intake + pressure.Exhaust > 0 ? pressure.Intake / (pressure.Intake + pressure.Exhaust) : null,
            ByRpm = pressure.ByRpm,
            IntakeFans = pressure.IntakeFans,
            ExhaustFans = pressure.ExhaustFans,
            UncalibratedCaseFanIds = pressure.Uncalibrated,
            IntakeWeight = pressure.Intake,
            ExhaustWeight = pressure.Exhaust,
            OutOfCaseFanIds = layout.OutOfCase,
            MixedSides = layout.MixedSides,
            AgainstFlowFanIds = layout.AgainstFlow,
            Hints = hints,
            SuggestedCurves = curves,
            Fans = fanSuggestions,
        };
    }

    /// <summary>
    /// Schränkt einen Vorschlag auf die übergebenen Lüfter ein (für die selektive Übernahme in der GUI -
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
    /// The aggressiveness a profile's role curves get: the onboarding profiles <c>silent</c> and <c>performance</c>
    /// keep their character, every other profile gets the balanced tables.
    /// </summary>
    public static AirflowAggressiveness VariantFor(string profileId) => profileId switch
    {
        SilentProfileId => AirflowAggressiveness.Silent,
        PerformanceProfileId => AirflowAggressiveness.Performance,
        _ => AirflowAggressiveness.Balanced,
    };

    /// <summary>
    /// The airflow proposal for <paramref name="target"/>: for each of <paramref name="fanIds"/> (the caller passes the
    /// fans it may assign - controllable ones; hidden or unknown ones are skipped) the curve it follows in the profile
    /// and the one its position suggests, plus the five role curves. A role curve the profile already has (same stable
    /// id) is taken as it is - the user may have tuned it -, a missing one comes from the template in the profile's
    /// variant (<see cref="VariantFor"/>). Reads only.
    /// </summary>
    /// <param name="config">Fans (positions, visibility) and sensors (template sources).</param>
    /// <param name="curveNames">Localized names for template curves, key = stable curve id (persisted).</param>
    /// <param name="sensorLabels">The hardware label per sensor id (see <see cref="WithSensorLabels"/>).</param>
    public static AirflowProposal Propose(
        AppConfig config,
        Profile target,
        IEnumerable<string> fanIds,
        IReadOnlyDictionary<string, string>? curveNames = null,
        IReadOnlyDictionary<string, string>? sensorLabels = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        config = WithSensorLabels(config, sensorLabels);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(fanIds);

        var fans = new Dictionary<string, FanConfig>(StringComparer.Ordinal);
        foreach (FanConfig fan in config.Fans)
            fans.TryAdd(fan.FanId, fan);
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (ProfileAssignment assignment in target.Assignments)
            current.TryAdd(assignment.FanId, assignment.CurveId);

        var changes = new List<AirflowChange>();
        foreach (string fanId in fanIds)
        {
            if (!fans.TryGetValue(fanId, out FanConfig? fan) || fan.Hidden)
                continue;
            AirflowRole role = Spec(fan.Location).Role;
            string? proposed = role == AirflowRole.None ? null : CurveFor(role, AirflowAggressiveness.Balanced).Id;
            changes.Add(new AirflowChange(fanId, current.GetValueOrDefault(fanId), proposed));
        }

        SensorSources sensors = ResolveSensors(config.Sensors.Where(s => !s.Hidden).ToList(), []);
        AirflowAggressiveness variant = VariantFor(target.Id);
        var roleCurves = CurveRoles
            .Select(role =>
            {
                string id = CurveFor(role, variant).Id;
                return target.Curves.FirstOrDefault(c => c.Id == id) is { } own
                    ? new AirflowRoleCurve(own, InProfile: true)
                    : new AirflowRoleCurve(BuildCurve(role, variant, sensors, curveNames), InProfile: false);
            })
            .ToList();

        return new AirflowProposal { Changes = changes, RoleCurves = roleCurves };
    }

    /// <summary>
    /// Takes <paramref name="picked"/> changes of <paramref name="proposal"/> into <paramref name="target"/>: each fan
    /// gets its proposed curve (or none), and a role curve the profile lacks is added from the template - only if a
    /// picked fan needs it. Curves the profile already has stay untouched, and so do fans that were not picked.
    /// </summary>
    public static Profile ApplyTo(Profile target, AirflowProposal proposal, IEnumerable<AirflowChange> picked)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(picked);

        var changes = picked.ToList();
        var needed = changes.Select(c => c.ProposedCurveId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var curves = target.Curves.ToList();
        var present = curves.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        curves.AddRange(proposal.RoleCurves
            .Where(r => needed.Contains(r.Curve.Id) && !present.Contains(r.Curve.Id))
            .Select(r => r.Curve));

        return target with
        {
            Curves = curves,
            Assignments = UpsertAssignments(target.Assignments, changes),
        };
    }

    /// <summary>
    /// A new profile from <paramref name="source"/> with the <paramref name="picked"/> changes (see
    /// <see cref="ApplyTo"/>); <paramref name="source"/> itself stays as it is. A curve that had fans before and has
    /// none afterwards is left out - a curve that was already empty stays.
    /// </summary>
    public static Profile AsNewProfile(
        Profile source, AirflowProposal proposal, IEnumerable<AirflowChange> picked, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Profile changed = ApplyTo(source, proposal, picked);
        var usedBefore = UsedCurveIds(source.Assignments);
        var usedAfter = UsedCurveIds(changed.Assignments);
        return changed with
        {
            Id = id,
            Name = name,
            Curves = changed.Curves.Where(c => usedAfter.Contains(c.Id) || !usedBefore.Contains(c.Id)).ToList(),
        };
    }

    private static HashSet<string> UsedCurveIds(IEnumerable<ProfileAssignment> assignments) =>
        assignments.Select(a => a.CurveId).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Builds the three onboarding profiles (silent/balanced/performance - ids and order match
    /// <see cref="DefaultProfiles.Build"/>) from the airflow analysis: each profile carries the role
    /// curves in the matching aggressiveness variant plus role-based assignments, restricted to
    /// <paramref name="assignableFanIds"/> (the controllable fans - read-only channels would only
    /// produce a failing SetPwm per tick). The analysis runs on the full config first, so the pressure
    /// balance still counts every visible fan. Curve ids are the stable <c>airflow-*</c> ids in all
    /// three profiles: only one profile is active at a time and <see cref="ProfileService.Apply"/>
    /// swaps the curve set wholesale, so assignments stay valid across profile switches. Fans whose
    /// role has no curve (PSU) get a <c>null</c> assignment - hardware auto, a regular state.
    /// </summary>
    /// <param name="curveNames">Localized curve names, key = stable curve id (persisted; see
    /// <see cref="Analyze(AppConfig, IReadOnlyDictionary{string, string}?, IReadOnlyDictionary{string, string}?)"/>).</param>
    /// <param name="sensorLabels">The hardware label per sensor id (see <see cref="WithSensorLabels"/>).</param>
    public static IReadOnlyList<Profile> BuildProfiles(
        AppConfig config,
        IEnumerable<string> assignableFanIds,
        IReadOnlyDictionary<string, string>? curveNames = null,
        string silentName = "Silent",
        string balancedName = "Balanced",
        string performanceName = "Performance",
        IReadOnlyDictionary<string, string>? sensorLabels = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(assignableFanIds);
        config = WithSensorLabels(config, sensorLabels);

        var assignable = assignableFanIds.ToList();
        return new[]
        {
            BuildProfile(SilentProfileId, silentName, AirflowAggressiveness.Silent, config, assignable, curveNames),
            BuildProfile(BalancedProfileId, balancedName, AirflowAggressiveness.Balanced, config, assignable, curveNames),
            BuildProfile(PerformanceProfileId, performanceName, AirflowAggressiveness.Performance, config, assignable, curveNames),
        };
    }

    private static Profile BuildProfile(
        string id,
        string name,
        AirflowAggressiveness aggressiveness,
        AppConfig config,
        IReadOnlyList<string> assignableFanIds,
        IReadOnlyDictionary<string, string>? curveNames)
    {
        AirflowTuneResult result = FilterToFans(Analyze(config, aggressiveness, curveNames), assignableFanIds);
        return new Profile
        {
            Id = id,
            Name = name,
            Curves = result.SuggestedCurves,
            Assignments = result.Fans.Select(f => new ProfileAssignment(f.FanId, f.SuggestedCurveId)).ToList(),
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

    /// <summary>The roles that have a curve, in the order the GUI lists them.</summary>
    private static readonly AirflowRole[] CurveRoles =
        [AirflowRole.Cpu, AirflowRole.Gpu, AirflowRole.Intake, AirflowRole.Exhaust, AirflowRole.Default];

    /// <summary>Was eine <see cref="FanLocation"/> für den Tune bedeutet (Richtung, Rolle, Gehäuseseite).</summary>
    private sealed record LocationSpec(AirflowDirection Direction, AirflowRole Role, CaseSide? Side = null);

    /// <summary>
    /// Einzige Quelle pro Position: Richtung (für die Druckbilanz), Rolle (für die Kurve) und Seite (für die Gehäuse-Typen).
    /// Der <c>_</c>-Zweig fängt nur undefinierte Enum-Werte ab (z. B. hand-editierte Config) → sichere Defaults.
    /// </summary>
    private static LocationSpec Spec(FanLocation location) => location switch
    {
        FanLocation.CpuCooler => new(AirflowDirection.Internal, AirflowRole.Cpu),
        FanLocation.Radiator => new(AirflowDirection.Internal, AirflowRole.Cpu),
        FanLocation.GpuCooler => new(AirflowDirection.Internal, AirflowRole.Gpu),
        FanLocation.CaseFrontIntake => new(AirflowDirection.Intake, AirflowRole.Intake, CaseSide.Front),
        FanLocation.CaseBottomIntake => new(AirflowDirection.Intake, AirflowRole.Intake, CaseSide.Bottom),
        FanLocation.CaseSideIntake => new(AirflowDirection.Intake, AirflowRole.Intake, CaseSide.Side),
        FanLocation.CaseTopIntake => new(AirflowDirection.Intake, AirflowRole.Intake, CaseSide.Top),
        FanLocation.CaseRearIntake => new(AirflowDirection.Intake, AirflowRole.Intake, CaseSide.Rear),
        FanLocation.CaseRearExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, CaseSide.Rear),
        FanLocation.CaseTopExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, CaseSide.Top),
        FanLocation.CaseFrontExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, CaseSide.Front),
        FanLocation.CaseBottomExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, CaseSide.Bottom),
        FanLocation.CaseSideExhaust => new(AirflowDirection.Exhaust, AirflowRole.Exhaust, CaseSide.Side),
        // Moves the air on inside the case: cools like an intake, but brings no air in - no part of the balance.
        FanLocation.FanWall => new(AirflowDirection.Internal, AirflowRole.Intake),
        FanLocation.Psu => new(AirflowDirection.Internal, AirflowRole.None),
        FanLocation.Unspecified => new(AirflowDirection.Unknown, AirflowRole.Default),
        FanLocation.Other => new(AirflowDirection.Unknown, AirflowRole.Default),
        _ => new(AirflowDirection.Unknown, AirflowRole.Default),
    };

    // ── Druckbilanz ─────────────────────────────────────────────────────────────

    /// <summary>Zwischenergebnis der Druckbilanz (innerhalb des Service).</summary>
    private readonly record struct PressureParts(
        PressureVerdict Verdict, double Intake, double Exhaust, bool ByRpm,
        int IntakeFans, int ExhaustFans, IReadOnlyList<string> Uncalibrated);

    private static (PressureParts, IReadOnlyList<AirflowHint>) ComputePressure(IReadOnlyList<FanConfig> fans, CaseType caseType)
    {
        var hints = new List<AirflowHint>();

        // Without a case there is nothing to balance - and nothing to miss, so no hints either. A laptop's blowers draw in
        // and blow out directly through their vents: no case volume to pressurize either.
        if (caseType is CaseType.Open or CaseType.Laptop)
            return (new PressureParts(PressureVerdict.NotApplicable, 0, 0, false, 0, 0, []), hints);

        // Only the case fans of the type count: a position the case does not have is a hint (AnalyzeLayout), not air.
        var caseFans = CaseFans(fans, caseType)
            .Select(f => (Fan: f, Dir: DirectionOf(f.Location)))
            .ToList();

        if (caseFans.Count == 0)
        {
            // A server cooled by its fan wall alone (1U/2U) is set up, not missing fans - there is just no balance.
            if (caseType == CaseType.Server && fans.Any(f => f.Location == FanLocation.FanWall))
                return (new PressureParts(PressureVerdict.FanWallOnly, 0, 0, false, 0, 0, []), hints);
            hints.Add(AirflowHint.NoCaseFans);
            return (new PressureParts(PressureVerdict.Unknown, 0, 0, false, 0, 0, []), hints);
        }

        // Gewicht = kalibrierte Max-RPM (grober CFM-Proxy), sonst Anzahl. Nur einheitlich anwenden:
        // sobald ein Gehäuselüfter unkalibriert ist, zählen wir alle nach Anzahl (1), um Einheiten nicht zu mischen.
        var uncalibrated = caseFans.Where(x => x.Fan.Calibration is not { MaxRpm: > 0 }).Select(x => x.Fan.FanId).ToList();
        bool allCalibrated = uncalibrated.Count == 0;
        if (!allCalibrated)
            hints.Add(AirflowHint.CountEstimateOnly);

        double Weight((FanConfig Fan, AirflowDirection Dir) x) =>
            allCalibrated ? x.Fan.Calibration!.MaxRpm : 1.0;

        double intake = caseFans.Where(x => x.Dir == AirflowDirection.Intake).Sum(Weight);
        double exhaust = caseFans.Where(x => x.Dir == AirflowDirection.Exhaust).Sum(Weight);

        if (intake <= 0)
            hints.Add(AirflowHint.NoIntakeFan);
        if (exhaust <= 0)
            hints.Add(AirflowHint.NoExhaustFan);

        PressureVerdict verdict = Verdict(intake, exhaust);
        if (verdict is PressureVerdict.Negative or PressureVerdict.OnlyExhaust)
            hints.Add(AirflowHint.NegativePressure);

        int intakeFans = caseFans.Count(x => x.Dir == AirflowDirection.Intake);
        return (new PressureParts(verdict, intake, exhaust, allCalibrated,
            intakeFans, caseFans.Count - intakeFans, uncalibrated), hints);
    }

    // The intake and exhaust fans whose position the case type has.
    private static IEnumerable<FanConfig> CaseFans(IEnumerable<FanConfig> fans, CaseType caseType) => fans
        .Where(f => DirectionOf(f.Location) is AirflowDirection.Intake or AirflowDirection.Exhaust && IsInCase(f.Location, caseType));

    /// <summary>What the positions say beyond the balance (see <see cref="AnalyzeLayout"/>).</summary>
    private sealed record CaseLayout(
        IReadOnlyList<string> OutOfCase, IReadOnlyList<AirflowMixedSide> MixedSides, IReadOnlyList<string> AgainstFlow);

    // Positions the case type does not have, sides with both directions, and - in a server - fans against the flow. In a
    // server a mixed side always holds a fan against the flow, and that is the more precise thing to say - only once. A
    // laptop's vents serve its blowers directly: with no case volume and no balance there is no mixed side to report.
    private static CaseLayout AnalyzeLayout(IReadOnlyList<FanConfig> fans, CaseType caseType)
    {
        var outOfCase = fans.Where(f => !IsInCase(f.Location, caseType)).Select(f => f.FanId).ToList();
        var caseFans = CaseFans(fans, caseType).ToList();
        List<AirflowMixedSide> mixed = caseType is CaseType.Server or CaseType.Laptop ? new() : caseFans
            .GroupBy(f => SideOf(f.Location)!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new AirflowMixedSide(g.Key,
                g.Where(f => DirectionOf(f.Location) == AirflowDirection.Intake).Select(f => f.FanId).ToList(),
                g.Where(f => DirectionOf(f.Location) == AirflowDirection.Exhaust).Select(f => f.FanId).ToList()))
            .Where(m => m.IntakeFanIds.Count > 0 && m.ExhaustFanIds.Count > 0)
            .ToList();
        var against = caseFans.Where(f => IsAgainstFlow(f.Location, caseType)).Select(f => f.FanId).ToList();
        return new CaseLayout(outOfCase, mixed, against);
    }

    // Balanced while neither side outweighs the other by BalanceThreshold; above it, a share of StrongPositiveShare or more
    // is strong.
    private static PressureVerdict Verdict(double intake, double exhaust)
    {
        if (intake <= 0 && exhaust <= 0)
            return PressureVerdict.Unknown;
        if (intake <= 0)
            return PressureVerdict.OnlyExhaust;
        if (exhaust <= 0)
            return PressureVerdict.OnlyIntake;
        double ratio = intake / exhaust;
        if (ratio <= 1.0 / BalanceThreshold)
            return PressureVerdict.Negative;
        if (ratio < BalanceThreshold)
            return PressureVerdict.Balanced;
        return intake / (intake + exhaust) >= StrongPositiveShare ? PressureVerdict.StrongPositive : PressureVerdict.SlightPositive;
    }

    // ── Pro-Lüfter-Vorschlag ────────────────────────────────────────────────────

    private static AirflowFanSuggestion BuildSuggestion(FanConfig fan)
    {
        LocationSpec spec = Spec(fan.Location);

        // Netzteil (und jede andere None-Rolle): auf Hardware-Auto lassen, keine Kurve.
        if (spec.Role == AirflowRole.None)
            return Suggestion(fan, spec.Direction, curveId: null, AirflowReason.HardwareAuto);

        // Only the id is needed here, and it is identical across aggressiveness variants.
        RoleCurve curve = CurveFor(spec.Role, AirflowAggressiveness.Balanced);
        AirflowReason reason = fan.Location is FanLocation.Unspecified or FanLocation.Other
            ? AirflowReason.NoPositionDefaultCurve
            : AirflowReason.LocationBasedCurve;

        return Suggestion(fan, spec.Direction, curve.Id, reason);
    }

    private static AirflowFanSuggestion Suggestion(FanConfig fan, AirflowDirection dir, string? curveId, AirflowReason reason) =>
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

    /// <summary>
    /// Kurvenvorlage je Rolle - Namen neutral englisch (Default; die GUI kann lokalisierte Namen über
    /// <c>curveNames</c> hereinreichen). <see cref="AirflowRole.None"/> hat bewusst keine (wird nie erzeugt).
    /// Id and name are identical across aggressiveness variants; only the point table differs.
    /// </summary>
    private static RoleCurve CurveFor(AirflowRole role, AirflowAggressiveness aggressiveness) => role switch
    {
        AirflowRole.Cpu => new(CpuCurveId, "Airflow · CPU/Radiator", CpuPoints.For(aggressiveness)),
        AirflowRole.Gpu => new(GpuCurveId, "Airflow · GPU", GpuPoints.For(aggressiveness)),
        AirflowRole.Intake => new(IntakeCurveId, "Airflow · Intake", IntakePoints.For(aggressiveness)),
        AirflowRole.Exhaust => new(ExhaustCurveId, "Airflow · Exhaust", ExhaustPoints.For(aggressiveness)),
        AirflowRole.Default => new(DefaultCurveId, "Airflow · Default", DefaultPoints.For(aggressiveness)),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Rolle hat keine Kurvenvorlage (None bekommt keine Kurve)."),
    };

    /// <summary>Point tables of one role in the three aggressiveness variants.</summary>
    private sealed record RolePoints(CurvePoint[] Silent, CurvePoint[] Balanced, CurvePoint[] Performance)
    {
        public CurvePoint[] For(AirflowAggressiveness aggressiveness) => aggressiveness switch
        {
            AirflowAggressiveness.Silent => Silent,
            AirflowAggressiveness.Performance => Performance,
            _ => Balanced,
        };
    }

    // Stützpunkte je Rolle (monoton steigend, enden bei 100 %). Die 100-%-Marke liegt bewusst unter dem
    // FailSafeTempC-Default (90 °C), damit die Kurve volle Drehzahl erreicht, bevor der Watchdog abrupt
    // auf Hardware-Auto zurückfällt: Max-Kurven bei 86 °C; die Avg-Komponenten-Kurven früher (82 °C),
    // weil der Mittelwert dem heißesten Sensor bei Offset-Spreizung (Tctl/Hot-Spot) hinterherläuft.
    // This holds for EVERY aggressiveness variant: silent starts later/lower (0 % like the
    // DefaultProfiles silent curve) but must reach 100 % at the same point; performance gets there
    // much earlier. At every temperature silent ≤ balanced ≤ performance (test-enforced).
    private static readonly RolePoints CpuPoints = new(
        Silent: [new(40, 0), new(55, 15), new(68, 35), new(76, 60), new(82, 100)],
        Balanced: [new(30, 20), new(50, 40), new(65, 65), new(78, 90), new(82, 100)],
        Performance: [new(30, 30), new(45, 55), new(60, 80), new(72, 100)]);

    private static readonly RolePoints GpuPoints = new(
        Silent: [new(40, 0), new(58, 20), new(70, 45), new(78, 70), new(82, 100)],
        Balanced: [new(35, 20), new(55, 45), new(70, 70), new(76, 90), new(82, 100)],
        Performance: [new(30, 30), new(50, 60), new(65, 85), new(75, 100)]);

    private static readonly RolePoints IntakePoints = new(
        Silent: [new(35, 0), new(55, 10), new(68, 30), new(80, 60), new(86, 100)],
        Balanced: [new(30, 15), new(50, 30), new(65, 50), new(80, 80), new(86, 100)],
        Performance: [new(30, 25), new(45, 45), new(60, 70), new(75, 100)]);

    // Auslass liegt bewusst leicht über dem Einlass (zieht Wärme aktiv heraus, hält die Bilanz neutral/positiv).
    private static readonly RolePoints ExhaustPoints = new(
        Silent: [new(35, 0), new(55, 15), new(68, 35), new(80, 65), new(86, 100)],
        Balanced: [new(30, 20), new(50, 38), new(65, 60), new(80, 90), new(86, 100)],
        Performance: [new(30, 30), new(45, 50), new(60, 75), new(75, 100)]);

    private static readonly RolePoints DefaultPoints = new(
        Silent: [new(35, 0), new(55, 10), new(68, 30), new(80, 60), new(86, 100)],
        Balanced: [new(30, 20), new(50, 35), new(65, 55), new(80, 90), new(86, 100)],
        Performance: [new(30, 30), new(45, 50), new(60, 75), new(75, 100)]);

    private static CurveConfig BuildCurve(
        AirflowRole role,
        AirflowAggressiveness aggressiveness,
        SensorSources sensors,
        IReadOnlyDictionary<string, string>? curveNames)
    {
        RoleCurve curve = CurveFor(role, aggressiveness);
        return new CurveConfig
        {
            Id = curve.Id,
            Name = curveNames is not null && curveNames.TryGetValue(curve.Id, out string? name) ? name : curve.Name,
            SourceSensorIds = sensors.For(role),
            // Komponenten-Kurven mitteln ihre eigenen Sensoren (Offset-Sensoren wie Tctl/Hot-Spot
            // überzeichnen sonst); Gehäuse-Kurven folgen der heißesten relevanten Quelle.
            Aggregation = role is AirflowRole.Cpu or AirflowRole.Gpu ? SensorAggregation.Avg : SensorAggregation.Max,
            InterpolationMode = InterpolationMode.Linear,
            HysteresisC = 2.0,
            Points = curve.Points,
        };
    }

    // ── Sensor-Heuristik ────────────────────────────────────────────────────────

    // The driver names the sensor classifier knows, plus words that turn up in sensor labels.
    internal static readonly string[] CpuKeywords =
        [.. SensorClassifier.CpuDrivers, "package", "tctl", "tdie", "core"];

    internal static readonly string[] GpuKeywords =
        [.. SensorClassifier.GpuDrivers, "geforce", "edge", "junction"];

    /// <summary>
    /// Super I/O "CPUTIN" (nct67xx, Linux and Windows) is a board sensor next to the socket. Its name matches "cpu", but
    /// under load it runs far below Tctl and would pull the CPU average down, as the SoC did - it is no CPU source while
    /// a real one exists. Without one it is still closer to the CPU than the fallback (the GPU or any first sensor).
    /// </summary>
    private const string CpuSocketBoardSensor = "CPUTIN";

    /// <summary>
    /// A sensor's <see cref="SensorConfig.Name"/> is the user's own; without one its hardware label names it. The CPU and
    /// GPU sensors are found by name before group (<see cref="FindSensors"/>), so a sensor without an own name is matched
    /// by its label - without the labels it would fall back to its group, and e.g. the SoC would be averaged into the CPU
    /// curve again. The labels are the backend's, which the config does not hold; the caller passes them.
    /// </summary>
    private static AppConfig WithSensorLabels(AppConfig config, IReadOnlyDictionary<string, string>? sensorLabels) =>
        sensorLabels is null || sensorLabels.Count == 0
            ? config
            : config with
            {
                Sensors = config.Sensors
                    .Select(s => string.IsNullOrWhiteSpace(s.Name) && sensorLabels.TryGetValue(s.SensorId, out string? label)
                        ? s with { Name = label }
                        : s)
                    .ToList(),
            };

    private static SensorSources ResolveSensors(IReadOnlyList<SensorConfig> sensors, List<AirflowHint> hints)
    {
        // GPU zuerst und von den CPU-Kandidaten abziehen: GPU-Sensoren heißen oft selbst „… GPU Core"
        // und würden sonst über das CPU-Schlüsselwort „core" mit einsortiert.
        IReadOnlyList<string> gpu = FindSensors(sensors, GpuKeywords, excluded: new HashSet<string>());
        var gpuIds = gpu.ToHashSet(StringComparer.Ordinal);
        List<string> socketBoard = sensors
            .Where(s => !gpuIds.Contains(s.SensorId)
                        && s.Name.Contains(CpuSocketBoardSensor, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.SensorId)
            .ToList();
        IReadOnlyList<string> cpu = FindSensors(
            sensors, CpuKeywords, excluded: gpuIds.Concat(socketBoard).ToHashSet(StringComparer.Ordinal));

        if (sensors.Count > 0 && cpu.Count == 0)
            hints.Add(AirflowHint.NoCpuSensorDetected);
        if (cpu.Count == 0)
            cpu = socketBoard;

        string? primary = cpu.Count > 0 ? cpu[0] : gpu.Count > 0 ? gpu[0] : sensors.FirstOrDefault()?.SensorId;

        return new SensorSources(cpu, gpu, primary);
    }

    /// <summary>
    /// The sensors whose name contains one of the keywords - or, only if no sensor matches by name, those whose
    /// group does. <paramref name="excluded"/> is removed before that decision, so a component whose only name
    /// matches belong to the other one (a GPU's "… GPU Core" for the CPU keyword "core") still gets the fallback.
    /// </summary>
    /// <remarks>
    /// Name before group because the component curves average their sources: automatic grouping files every
    /// sensor of the CPU package under "CPU", including e.g. "AMD Ryzen 7 5800X SoC", which runs 20-30 K below
    /// Tctl under load. Averaged in, it pulls the input down so far that the curve reaches 100 % only next to the
    /// fail-safe limit instead of with headroom below it. The group fallback keeps curves possible for sensors
    /// whose names carry no keyword (e.g. a Super I/O "temp3" the user filed under "CPU").
    /// </remarks>
    private static IReadOnlyList<string> FindSensors(
        IReadOnlyList<SensorConfig> sensors, string[] keywords, IReadOnlySet<string> excluded)
    {
        var candidates = sensors.Where(s => !excluded.Contains(s.SensorId)).ToList();
        var byName = candidates.Where(s => ContainsAny(s.Name, keywords)).ToList();
        IEnumerable<SensorConfig> matches = byName.Count > 0
            ? byName
            : candidates.Where(s => s.Group is not null && ContainsAny(s.Group, keywords));
        return matches.Select(s => s.SensorId).ToList();
    }

    private static bool ContainsAny(string text, string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>Aufgelöste Quell-Sensoren (per Namens-/Gruppen-Heuristik) und die Quellenliste je Rolle.</summary>
    private readonly record struct SensorSources(IReadOnlyList<string> Cpu, IReadOnlyList<string> Gpu, string? Primary)
    {
        public IReadOnlyList<string> For(AirflowRole role) => role switch
        {
            AirflowRole.Cpu => Cpu.Count > 0 ? Cpu : Fallback(),
            AirflowRole.Gpu => Gpu.Count > 0 ? Gpu : Fallback(),
            _ => CaseSources(), // Intake/Exhaust/Default → heißeste relevante Quellen, Max-Aggregation
        };

        private IReadOnlyList<string> CaseSources()
        {
            var combined = Cpu.Concat(Gpu).ToList();
            return combined.Count > 0 ? combined : Fallback();
        }

        private IReadOnlyList<string> Fallback() => Primary is not null ? [Primary] : [];
    }

    // ── Merge-Helfer (für ApplyTo) ──────────────────────────────────────────────

    private static IReadOnlyList<ProfileAssignment> UpsertAssignments(
        IReadOnlyList<ProfileAssignment> existing,
        IReadOnlyList<AirflowChange> changes)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (AirflowChange change in changes)
            map[change.FanId] = change.ProposedCurveId;
        var merged = existing
            .Select(a => map.TryGetValue(a.FanId, out string? curveId) ? a with { CurveId = curveId } : a)
            .ToList();

        var existingFans = existing.Select(a => a.FanId).ToHashSet(StringComparer.Ordinal);
        merged.AddRange(map
            .Where(kv => !existingFans.Contains(kv.Key))
            .Select(kv => new ProfileAssignment(kv.Key, kv.Value)));
        return merged;
    }
}
