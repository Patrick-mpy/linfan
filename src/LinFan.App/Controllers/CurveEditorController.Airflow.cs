// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

// Settings › Airflow: the case schema with the fans, the pressure balance, notices with actions, the proposal for a
// target profile (only what changes, ticked rows taken over into that profile or into a new one) and the role curves.
// Everything is recomputed with the dirty check after every edit (curves and profiles included), right away when a fan's
// position, visibility, assignment or calibration changes, and after every rebuild - there is no "analyse" step any more.
public partial class CurveEditorController
{
    /// <summary>A link on the airflow page leads to another settings section („Zu den Lüftern").</summary>
    public event EventHandler<SettingsSection>? SettingsSectionRequested;

    /// <summary>A link on the airflow page leads into the curves tab (a profile, or one of its curves).</summary>
    public event EventHandler<EditorJump>? EditorJumpRequested;

    private bool _airflowStale; // a refresh was asked for during a WithoutHistory scope

    /// <summary>The zones of the case schema (grid part).</summary>
    [ObservableProperty] private IReadOnlyList<CaseZone> _airflowZones = [];

    /// <summary>„Ohne Position" / „Sonstige" below the case.</summary>
    [ObservableProperty] private IReadOnlyList<CaseZone> _airflowTray = [];

    [ObservableProperty] private AirflowBalance _airflowBalance = AirflowBalance.Empty;

    [ObservableProperty] private IReadOnlyList<AirflowHintRow> _airflowHints = [];

    /// <summary>The profile the proposal is for; the running one unless the user picks another.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AirflowTargetName), nameof(AirflowTargetIsRunning))]
    private ProfileRow? _airflowTarget;

    /// <summary>The target menu: every profile, the running one marked.</summary>
    [ObservableProperty] private IReadOnlyList<MenuChoice> _airflowTargetMenu = [];

    /// <summary>The fans whose curve the proposal changes in the target profile.</summary>
    public ObservableCollection<AirflowChangeRow> AirflowChanges { get; } = new();

    /// <summary>„Name · Kurve" of the fans that stay as they are.</summary>
    [ObservableProperty] private IReadOnlyList<string> _airflowUnchanged = [];

    [ObservableProperty] private bool _showAirflowUnchanged;

    [ObservableProperty] private IReadOnlyList<AirflowRoleTile> _airflowRoles = [];

    /// <summary>Curves of the target that would lose all their fans („Danach ohne Lüfter: …"); empty if none.</summary>
    [ObservableProperty] private string _airflowEmptiedText = "";

    private AirflowProposal? _airflowProposal;

    // Unticked rows, per target profile and fan - so switching the target back and forth keeps the user's choice.
    private readonly HashSet<string> _airflowSkipped = new(StringComparer.Ordinal);

    public string AirflowTargetName => AirflowTarget?.Name ?? "";

    public bool AirflowTargetIsRunning => AirflowTarget is not null && ReferenceEquals(AirflowTarget, ActiveProfile);

    public bool HasAirflowChanges => AirflowChanges.Count > 0;

    public bool HasAirflowHints => AirflowHints.Count > 0;

    public bool HasAirflowUnchanged => AirflowUnchanged.Count > 0;

    public bool HasAirflowEmptied => AirflowEmptiedText.Length > 0;

    private int PickedAirflowChanges => AirflowChanges.Count(r => r.Apply);

    /// <summary>„1 Änderung" / „3 Änderungen · 2 gewählt".</summary>
    public string AirflowChangesText
    {
        get
        {
            int total = AirflowChanges.Count;
            string count = total == 1
                ? Localizer.Instance["Airflow.OneChange"]
                : Localizer.Instance.Format("Airflow.Changes", total);
            int picked = PickedAirflowChanges;
            return picked == total ? count : count + Localizer.Instance.Format("Airflow.Picked", picked);
        }
    }

    /// <summary>„1 Lüfter bleibt gleich" / „N Lüfter bleiben gleich".</summary>
    public string AirflowUnchangedText => AirflowUnchanged.Count == 1
        ? Localizer.Instance["Airflow.OneUnchanged"]
        : Localizer.Instance.Format("Airflow.Unchanged", AirflowUnchanged.Count);

    /// <summary>„„X" folgt bereits dem Vorschlag." - shown when nothing would change.</summary>
    public string AirflowFollowsText => Localizer.Instance.Format("Airflow.Follows", AirflowTargetName);

    public string AirflowApplyLabel => Localizer.Instance.Format("Airflow.ApplyTo", AirflowTargetName);

    /// <summary>Why taking the ticked changes over is locked, or what it does.</summary>
    public string AirflowApplyTooltip =>
        PickedAirflowChanges == 0 ? Localizer.Instance["Airflow.NothingPicked"]
        : _airflowCurveLimitReached ? Localizer.Instance.Format("Airflow.TooManyCurves", ConfigLimits.MaxCurvesPerSet)
        : Localizer.Instance.Format("Airflow.ApplyTooltip", AirflowTargetName);

    public string AirflowCreateTooltip =>
        PickedAirflowChanges == 0 ? Localizer.Instance["Airflow.NothingPicked"]
        : Profiles.Count >= ConfigLimits.MaxProfiles ? Localizer.Instance.Format("Airflow.TooManyProfiles", ConfigLimits.MaxProfiles)
        : _airflowCurveLimitReached ? Localizer.Instance.Format("Airflow.TooManyCurves", ConfigLimits.MaxCurvesPerSet)
        : Localizer.Instance["Airflow.CreateTooltip"];

    /// <summary>„„X" läuft gerade nicht - wirkt erst nach dem Profilwechsel. Landet im Editor; erst Speichern …".</summary>
    public string AirflowFootText => (AirflowTargetIsRunning ? "" : Localizer.Instance.Format("Airflow.NotRunning", AirflowTargetName))
                                     + Localizer.Instance["Airflow.SaveToApply"];

    // The ticked changes would add more role curves than a profile may hold (the daemon would refuse the whole config) -
    // worked out with the emptied curves whenever the proposal or a tick changes (RefreshAirflowAfter).
    private bool _airflowCurveLimitReached;

    private bool CanApplyAirflow => AirflowTarget is not null && PickedAirflowChanges > 0 && !_airflowCurveLimitReached;

    private bool CanCreateAirflowProfile => CanApplyAirflow && Profiles.Count < ConfigLimits.MaxProfiles;

    private IReadOnlyList<AirflowChange> PickedChanges() => AirflowChanges.Where(r => r.Apply).Select(r => r.Change).ToList();

    partial void OnAirflowTargetChanged(ProfileRow? value)
    {
        ShowAirflowUnchanged = false;
        RefreshAirflowProposal();
        AirflowTargetMenu = BuildAirflowTargetMenu();
    }

    /// <summary>
    /// Recomputes the airflow page: schema, balance, notices, the proposal and the role curves. Called after every
    /// rebuild of the editor and whenever something it depends on changes (see <see cref="OnFanRowChanged"/>).
    /// </summary>
    private void RefreshAirflow(AppConfig? config = null)
    {
        // A rebuild (WithoutHistory) changes many rows at once - recompute once when it ends, not per row.
        if (_nonEditDepth > 0)
        {
            _airflowStale = true;
            return;
        }
        _airflowStale = false;

        if (!_initialized)
        {
            ClearAirflow();
            return;
        }

        config ??= BuildConfig();
        AirflowTuneResult result = AirflowTuneService.Analyze(config, AirflowText.CurveNames(), SensorOption.Labels(Sensors));
        _airflowResult = result;

        var fans = Fans.Where(f => f.Visible).Select(SchemaFan).ToList();
        IReadOnlyList<CaseZone> zones = CaseSchema.Build(fans, CaseType);
        AirflowZones = zones.Where(z => !z.IsTray).ToList();
        AirflowTray = zones.Where(z => z.IsTray).ToList();
        AirflowBalance = AirflowText.Balance(result);
        AirflowHints = BuildAirflowHints(result);

        // Undo/redo, discard and a resync rebuild the profile rows: the target is found again by its id. Only a target
        // that is gone (deleted) falls back to the running profile.
        ProfileRow? target = AirflowTarget is { } current && Profiles.Contains(current)
            ? current
            : Profiles.FirstOrDefault(p => p.Id == AirflowTarget?.Id) ?? ActiveProfile ?? SelectedProfile ?? Profiles.FirstOrDefault();
        if (!ReferenceEquals(target, AirflowTarget))
        {
            AirflowTarget = target; // runs the proposal and rebuilds the menu
            return;
        }
        RefreshAirflowProposal(config);
        AirflowTargetMenu = BuildAirflowTargetMenu(); // profile names and the running profile may have changed
    }

    private void ClearAirflow()
    {
        _airflowResult = null;
        AirflowZones = [];
        AirflowTray = [];
        AirflowBalance = AirflowBalance.Empty;
        AirflowHints = [];
        AirflowTargetMenu = [];
        AirflowRoles = [];
        AirflowUnchanged = [];
        AirflowEmptiedText = "";
        _airflowCurveLimitReached = false;
        SetAirflowChanges([]);
        _airflowProposal = null;
        _airflowSkipped.Clear();
        AirflowTarget = null;
    }

    private CaseFan SchemaFan(FanAssignRow fan) => new(
        fan.FanId, fan.DisplayName, fan.Location.Value, fan.Manual,
        open: new RelayCommand(() => LocationDialogRequested?.Invoke(this, fan)),
        tooltip: Localizer.Instance.Format(fan.CanControl ? "Airflow.FanTooltip" : "Airflow.FanTooltipReadOnly",
            fan.DisplayName, fan.Location.Display));

    private IReadOnlyList<MenuChoice> BuildAirflowTargetMenu() => Profiles
        .Select(p => MenuChoice.Value(p.Name, new RelayCommand(() => AirflowTarget = p), ReferenceEquals(p, AirflowTarget),
            detail: ReferenceEquals(p, ActiveProfile) ? Localizer.Instance["Airflow.Running"] : ""))
        .ToList();

    private void RefreshAirflowProposal(AppConfig? config = null)
    {
        if (!_initialized || AirflowTarget is not { } target)
        {
            _airflowProposal = null;
            _airflowCurveLimitReached = false;
            SetAirflowChanges([]);
            AirflowUnchanged = [];
            AirflowRoles = [];
            AirflowEmptiedText = "";
            return;
        }

        Profile state = CurrentStateOf(target);
        var fanIds = Fans.Where(f => f.Visible && f.CanControl).Select(f => f.FanId);
        AirflowProposal proposal = AirflowTuneService.Propose(
            config ?? BuildConfig(), state, fanIds, AirflowText.CurveNames(), SensorOption.Labels(Sensors));
        _airflowProposal = proposal;

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CurveConfig curve in state.Curves)
            names.TryAdd(curve.Id, curve.Name);
        foreach (AirflowRoleCurve role in proposal.RoleCurves)
            names.TryAdd(role.Curve.Id, role.Curve.Name);
        string NameOf(string? curveId) => curveId is null
            ? Localizer.Instance["CurveEditorCtrl.NoCurveHardwareAuto"]
            : names.GetValueOrDefault(curveId, curveId);

        var rows = new List<AirflowChangeRow>();
        var unchanged = new List<string>();
        foreach (AirflowChange change in proposal.Changes)
        {
            FanAssignRow fan = Fans.First(f => f.FanId == change.FanId);
            if (!change.Changed)
            {
                unchanged.Add(Localizer.Instance.Format("Airflow.UnchangedEntry", fan.DisplayName, NameOf(change.CurrentCurveId)));
                continue;
            }
            CurveConfig? existing = change.ProposedCurveId is { } proposed ? state.Curves.FirstOrDefault(c => c.Id == proposed) : null;
            rows.Add(new AirflowChangeRow(change, fan.DisplayName, CaseSchema.Label(fan.Location.Value, CaseType),
                FanLocationOption.DirectionOf(fan.Location.Value), NameOf(change.CurrentCurveId), NameOf(change.ProposedCurveId),
                isNewCurve: change.ProposedCurveId is not null && existing is null, isCurveOff: existing is { Enabled: false },
                apply: !_airflowSkipped.Contains(SkipKey(target, change.FanId))));
        }
        SetAirflowChanges(rows);
        AirflowUnchanged = unchanged;

        AirflowRoles = proposal.RoleCurves.Select(r => RoleTile(target, r, proposal)).ToList();
        RefreshAirflowAfter();
        NotifyAirflowCounts();
    }

    private AirflowRoleTile RoleTile(ProfileRow target, AirflowRoleCurve role, AirflowProposal proposal)
    {
        CurveConfig curve = role.Curve;
        var core = new Curve(curve.Name, curve.Points, curve.InterpolationMode);
        var fanNames = proposal.Changes
            .Where(c => c.ProposedCurveId == curve.Id)
            .Select(c => Fans.First(f => f.FanId == c.FanId).DisplayName)
            .ToList();
        // The proposal takes a role curve the profile has as it is - switched off too, which leaves its fans on hardware auto.
        bool off = role.InProfile && !curve.Enabled;
        return new AirflowRoleTile
        {
            Title = AirflowText.RoleTitle(curve.Id),
            Who = AirflowText.RoleWho(curve.Id),
            CurveName = curve.Name,
            InProfile = role.InProfile,
            StateText = Localizer.Instance[off ? "Airflow.CurveOff" : role.InProfile ? "Airflow.CurveExists" : "Airflow.CurveFromTemplate"],
            StateTone = off ? StatusTone.Warn : role.InProfile ? StatusTone.Ok : StatusTone.Muted,
            StateTooltip = off ? Localizer.Instance["Airflow.CurveOffTooltip"] : null,
            Samples = LoopRow.Sample(core),
            Detail = AirflowText.Sources(curve, id => Sensors.FirstOrDefault(s => s.Id == id)?.Name ?? id),
            FansText = fanNames.Count > 0 ? string.Join(", ", fanNames) : Localizer.Instance["Airflow.NoFan"],
            IsUnused = fanNames.Count == 0,
            Open = role.InProfile ? new RelayCommand(() => EditorJumpRequested?.Invoke(this, new EditorJump(target.Id, curve.Id))) : null,
        };
    }

    private static string SkipKey(ProfileRow target, string fanId) => target.Id + "\n" + fanId;

    private void SetAirflowChanges(IReadOnlyList<AirflowChangeRow> rows)
    {
        foreach (AirflowChangeRow row in AirflowChanges)
            row.PropertyChanged -= OnAirflowChangeTicked;
        AirflowChanges.Clear();
        foreach (AirflowChangeRow row in rows)
        {
            row.PropertyChanged += OnAirflowChangeTicked;
            AirflowChanges.Add(row);
        }
        NotifyAirflowCounts();
    }

    private void OnAirflowChangeTicked(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AirflowChangeRow.Apply) || sender is not AirflowChangeRow row || AirflowTarget is not { } target)
            return;
        string key = SkipKey(target, row.Change.FanId);
        if (row.Apply)
            _airflowSkipped.Remove(key);
        else
            _airflowSkipped.Add(key);
        RefreshAirflowAfter();
        NotifyAirflowCounts();
    }

    // What the ticked changes would leave behind: the target's curves that had fans and would have none, and whether the
    // profile would hold more curves than allowed.
    private void RefreshAirflowAfter()
    {
        if (AirflowTarget is not { } target || _airflowProposal is not { } proposal)
        {
            AirflowEmptiedText = "";
            _airflowCurveLimitReached = false;
            return;
        }
        Profile state = CurrentStateOf(target);
        Profile after = AirflowTuneService.ApplyTo(state, proposal, PickedChanges());
        _airflowCurveLimitReached = after.Curves.Count > ConfigLimits.MaxCurvesPerSet;
        var usedAfter = after.Assignments.Select(a => a.CurveId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var usedBefore = state.Assignments.Select(a => a.CurveId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var emptied = state.Curves.Where(c => usedBefore.Contains(c.Id) && !usedAfter.Contains(c.Id)).Select(c => c.Name).ToList();
        AirflowEmptiedText = emptied.Count == 0
            ? ""
            : Localizer.Instance.Format("Airflow.Emptied", string.Join(", ", emptied.Select(n => Localizer.Instance.Format("Airflow.EmptiedCurve", n))));
    }

    partial void OnAirflowEmptiedTextChanged(string value) => OnPropertyChanged(nameof(HasAirflowEmptied));

    partial void OnAirflowHintsChanged(IReadOnlyList<AirflowHintRow> value) => OnPropertyChanged(nameof(HasAirflowHints));

    partial void OnAirflowUnchangedChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasAirflowUnchanged));
        OnPropertyChanged(nameof(AirflowUnchangedText));
    }

    private void NotifyAirflowCounts()
    {
        OnPropertyChanged(nameof(HasAirflowChanges));
        OnPropertyChanged(nameof(AirflowChangesText));
        OnPropertyChanged(nameof(AirflowApplyLabel));
        OnPropertyChanged(nameof(AirflowApplyTooltip));
        OnPropertyChanged(nameof(AirflowCreateTooltip));
        OnPropertyChanged(nameof(AirflowFollowsText));
        OnPropertyChanged(nameof(AirflowFootText));
        OnPropertyChanged(nameof(AirflowTargetName));
        OnPropertyChanged(nameof(AirflowTargetIsRunning));
        ApplyAirflowToTargetCommand.NotifyCanExecuteChanged();
        CreateAirflowProfileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Writes the ticked changes into the target profile - the one shown in the editor through the editor, any other
    /// straight into its row. One history step; it takes effect with Save (and, for a profile that is not running,
    /// once it is switched to).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyAirflow))]
    private void ApplyAirflowToTarget()
    {
        if (AirflowTarget is not { } target || _airflowProposal is not { } proposal)
            return;
        Profile updated = AirflowTuneService.ApplyTo(CurrentStateOf(target), proposal, PickedChanges());
        ForgetAirflowTicks(target);
        RecordStep(() => // its dirty check recomputes the page
        {
            if (ReferenceEquals(target, SelectedProfile))
            {
                ReloadEditor(updated.Curves, updated.Assignments);
            }
            else
            {
                target.Curves = updated.Curves;
                target.Assignments = Normalized(updated.Assignments);
            }
        });
        SetStatus(Localizer.Instance.Format("Airflow.Applied", target.Name), autoHide: true);
    }

    /// <summary>
    /// Creates a copy of the target profile with the ticked changes („X · Airflow"); the target stays as it is. Curves
    /// that lose all their fans are left out of the copy. The new profile is not running; it becomes the target.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateAirflowProfile))]
    private void CreateAirflowProfile()
    {
        if (AirflowTarget is not { } source || _airflowProposal is not { } proposal)
            return;
        Profile created = AirflowTuneService.AsNewProfile(CurrentStateOf(source), proposal, PickedChanges(),
            NewProfileId(), UniqueAirflowProfileName(source.Name));
        ProfileRow row = RowOf(created);
        ForgetAirflowTicks(source);
        RecordStep(() =>
        {
            Profiles.Add(row);
            NotifyProfileActivationChanged();
        });
        AirflowTarget = row; // runs the proposal for it
        SetStatus(Localizer.Instance.Format("Airflow.Created", row.Name), autoHide: true);
    }

    private string UniqueAirflowProfileName(string sourceName)
    {
        return UniqueName(Localizer.Instance.Format("Airflow.NewProfileName", sourceName),
            name => Profiles.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void ForgetAirflowTicks(ProfileRow target) => _airflowSkipped.RemoveWhere(k => k.StartsWith(target.Id + "\n", StringComparison.Ordinal));

    /// <summary>„Im Editor öffnen" when the target already follows the proposal.</summary>
    [RelayCommand]
    private void OpenAirflowTarget()
    {
        if (AirflowTarget is { } target)
            EditorJumpRequested?.Invoke(this, new EditorJump(target.Id));
    }

    // The analysis the airflow page shows - kept to rebuild its notices alone when only their actions change.
    private AirflowTuneResult? _airflowResult;

    // Notices with the action that resolves each - built from the analysis and the rows.
    private IReadOnlyList<AirflowHintRow> BuildAirflowHints(AirflowTuneResult result)
    {
        var hints = new List<AirflowHintRow>();
        var visible = Fans.Where(f => f.Visible).ToList();

        foreach (FanAssignRow fan in visible.Where(f => f.Location.Value == FanLocation.Unspecified))
        {
            hints.Add(new AirflowHintRow(
                Localizer.Instance.Format(fan.CanControl ? "Airflow.HintNoPosition" : "Airflow.HintNoPositionReadOnly", fan.DisplayName),
                IsWarn: true, Localizer.Instance["Airflow.SetPosition"],
                new RelayCommand(() => LocationDialogRequested?.Invoke(this, fan)), Localizer.Instance["Airflow.SetPositionTooltip"]));
        }

        var uncalibrated = result.UncalibratedCaseFanIds
            .Select(id => visible.FirstOrDefault(f => f.FanId == id && f.CanControl))
            .OfType<FanAssignRow>()
            .ToList();
        if (uncalibrated.Count > 0)
        {
            FanAssignRow first = uncalibrated[0];
            string text = uncalibrated.Count == 1
                ? Localizer.Instance.Format("Airflow.HintUncalibratedOne", first.DisplayName)
                : Localizer.Instance.Format("Airflow.HintUncalibrated", uncalibrated.Count);
            // While calibrating cannot start (offline, dry run, another device action) the notice offers no action.
            hints.Add(_deviceActionsBlocked
                ? new AirflowHintRow(text, IsWarn: false)
                : new AirflowHintRow(text, IsWarn: false,
                    uncalibrated.Count == 1 ? Localizer.Instance["MainWindow.Calibrate"] : Localizer.Instance.Format("Airflow.CalibrateFan", first.DisplayName),
                    first.CalibrateCommand, Localizer.Instance["Airflow.CalibrateTooltip"]));
        }

        if (result.IntakeFans == 0 && result.ExhaustFans > 0 && visible.FirstOrDefault(IsExhaust) is { } exhaust)
            hints.Add(DirectionHint("Airflow.HintNoIntake", exhaust));
        if (result.ExhaustFans == 0 && result.IntakeFans > 0 && visible.FirstOrDefault(IsIntake) is { } intake)
            hints.Add(DirectionHint("Airflow.HintNoExhaust", intake));

        if (result.Hints.Contains(AirflowHint.NoCaseFans))
        {
            hints.Add(new AirflowHintRow(Localizer.Instance["Airflow.HintNoCaseFans"], IsWarn: false,
                Localizer.Instance["Airflow.ToFans"], new RelayCommand(() => SettingsSectionRequested?.Invoke(this, SettingsSection.Fans))));
        }

        // The case type: positions it lacks, sides with both directions, a server fan against the flow.
        string caseName = CaseTypeText.Name(CaseType);
        List<FanAssignRow> outside = RowsOf(result.OutOfCaseFanIds);
        if (outside.Count > 0)
        {
            // What it costs differs: a tower or a server leaves the fan out of the balance, an open bench counts component
            // fans only, a laptop has no balance to leave.
            string suffix = CaseType switch
            {
                CaseType.Open => "Open",
                CaseType.Laptop => "Laptop",
                _ => "",
            };
            string text = outside.Count == 1
                ? Localizer.Instance.Format("Airflow.HintNotInCaseOne" + suffix, outside[0].DisplayName, caseName)
                : Localizer.Instance.Format("Airflow.HintNotInCase" + suffix, outside.Count, caseName);
            hints.Add(new AirflowHintRow(text, IsWarn: true, Localizer.Instance["Airflow.SetPosition"],
                new RelayCommand(() => LocationDialogRequested?.Invoke(this, outside[0])),
                Localizer.Instance.Format("Airflow.SetPositionInCaseTooltip", caseName)));
        }
        foreach (AirflowMixedSide side in result.MixedSides)
        {
            // The fewer ones are the likelier mistake - that is the fan the action opens.
            List<FanAssignRow> minority = RowsOf(side.IntakeFanIds.Count <= side.ExhaustFanIds.Count ? side.IntakeFanIds : side.ExhaustFanIds);
            if (minority.Count == 0)
                continue;
            hints.Add(DirectionHintFor(Localizer.Instance.Format("Airflow.HintMixedSide",
                CaseSchema.SideName(side.Side, CaseType), side.IntakeFanIds.Count, side.ExhaustFanIds.Count), isWarn: false, minority[0]));
        }
        List<FanAssignRow> against = RowsOf(result.AgainstFlowFanIds);
        if (against.Count > 0)
        {
            hints.Add(DirectionHintFor(against.Count == 1
                ? Localizer.Instance.Format("Airflow.HintAgainstFlowOne", against[0].DisplayName)
                : Localizer.Instance.Format("Airflow.HintAgainstFlow", against.Count), isWarn: true, against[0]));
        }

        // The role curves' sources are guessed from sensor names - say so when none looks like the CPU.
        if (result.Hints.Contains(AirflowHint.NoCpuSensorDetected))
            hints.Add(new AirflowHintRow(AirflowText.DescribeHint(AirflowHint.NoCpuSensorDetected), IsWarn: false));
        if (result.Hints.Contains(AirflowHint.NoSensorsConfigured))
            hints.Add(new AirflowHintRow(AirflowText.DescribeHint(AirflowHint.NoSensorsConfigured), IsWarn: true));
        return hints;

        AirflowHintRow DirectionHint(string key, FanAssignRow fan) => DirectionHintFor(Localizer.Instance[key], isWarn: true, fan);

        AirflowHintRow DirectionHintFor(string text, bool isWarn, FanAssignRow fan) => new(
            text, isWarn, Localizer.Instance["Airflow.CheckDirection"],
            new RelayCommand(() => LocationDialogRequested?.Invoke(this, fan)), Localizer.Instance["Airflow.CheckDirectionTooltip"]);

        List<FanAssignRow> RowsOf(IEnumerable<string> ids) =>
            ids.Select(id => visible.FirstOrDefault(f => f.FanId == id)).OfType<FanAssignRow>().ToList();

        // Only fans the balance counts - one at a position the type lacks is not what the notice is about.
        bool IsIntake(FanAssignRow f) => IsCaseFan(f, AirflowDirection.Intake);
        bool IsExhaust(FanAssignRow f) => IsCaseFan(f, AirflowDirection.Exhaust);
        bool IsCaseFan(FanAssignRow f, AirflowDirection direction) =>
            FanLocationOption.DirectionOf(f.Location.Value) == direction && CaseSchema.IsInSchema(f.Location.Value, CaseType);
    }
}
