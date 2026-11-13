// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

// Profil-Verwaltung (Anlegen/Duplizieren/Umbenennen/Löschen, Laden).
public partial class CurveEditorController
{
    // --- Profile ---------------------------------------------------------------

    // The shown profile's assignments for fans not detected right now: no row edits them, but they belong to the profile
    // and go back with it (a fan of a controller that is unplugged at the moment keeps its curve).
    private IReadOnlyList<ProfileAssignment> _undetectedAssignments = [];

    /// <summary>
    /// True, wenn der Editor das laufende Setup zeigt: entweder ist das gewählte Profil das aktive, oder es
    /// gibt überhaupt keine Profile - dann (Altbestand vor der Profil-Migration) bearbeitet der Editor die
    /// laufende Konfiguration direkt.
    /// </summary>
    public bool SelectedProfileIsActive =>
        Profiles.Count == 0 || (SelectedProfile is not null && ReferenceEquals(SelectedProfile, ActiveProfile));

    /// <summary>
    /// Whether the profile shown can be activated - the one rule of the banner button and the profile pane's switch: not
    /// running already, and switching possible (<see cref="CanSwitchProfile"/>). Unsaved changes do not block it - the
    /// switch asks what to do with them.
    /// </summary>
    public bool CanActivateSelectedProfile =>
        SelectedProfile is not null && !SelectedProfileIsActive && CanSwitchProfile;

    /// <summary>„Dieses Profil aktivieren" in the banner above an inactive profile - see <see cref="RequestProfileSwitch"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanActivateSelectedProfile))]
    private void ActivateSelectedProfile()
    {
        if (SelectedProfile is { } shown)
            RequestProfileSwitch(shown.Id);
    }

    /// <summary>Tooltip of the activate button and the profile switch: why it is off, or what it does.</summary>
    public string ActivateProfileHint =>
        ProfileSwitchBlockedReason is { Length: > 0 } reason ? reason
        : HasUnsavedChanges ? Localizer.Instance["MainWindow.ActivateProfileTooltipDirty"]
        : Localizer.Instance["MainWindow.ActivateProfileTooltip"];

    /// <summary>„Du bearbeitest „Leise“ - aktiv ist „Ausgewogen“" - the banner above a profile that is not running; empty otherwise.</summary>
    public string EditingInactiveProfileText =>
        !SelectedProfileIsActive && SelectedProfile is { } shown && ActiveProfile is { } running
            ? Localizer.Instance.Format("CurveEditorCtrl.EditingInactiveProfile", shown.Name, running.Name)
            : "";

    /// <summary>
    /// Bindungsziel des Aktiv-Schalters im Profil-Editor. Nur einschaltbar: es ist immer genau ein Profil
    /// aktiv, ein „aus" hätte keinen Empfänger - der Schalter ist beim aktiven Profil daher gesperrt.
    /// </summary>
    public bool SelectedProfileActive
    {
        get => SelectedProfileIsActive;
        set
        {
            if (value && SelectedProfile is { } shown && !SelectedProfileIsActive)
                RequestProfileSwitch(shown.Id);
            // The switch shows the running state: back off while the switch asks first (or was refused).
            OnPropertyChanged();
        }
    }

    partial void OnActiveProfileChanged(ProfileRow? oldValue, ProfileRow? newValue)
    {
        foreach (ProfileRow p in Profiles)
            p.IsActive = ReferenceEquals(p, newValue);
        NotifyProfileActivationChanged();
        AirflowTargetMenu = BuildAirflowTargetMenu(); // the running mark moves
        NotifyAirflowCounts();

        if (_applyingActiveProfile || newValue is null)
            return;

        _ = _activateProfile?.Invoke(newValue.Id);
        BeginPendingSwitch(newValue.Id, viaSave: false); // until the snapshot shows it
        // The daemon persists the switch itself (ControlLoopService → ProfileService.Apply + Save), as with a
        // curve on/off. So the baseline and every history step follow it instead of showing it as unsaved - and
        // no undo switches the running profile back. Nothing here reaches MarkDirty, so it is no step either.
        RebaselineActiveProfile(newValue.Id);
        RefreshDirty();
    }

    partial void OnSelectedProfileChanged(ProfileRow? oldValue, ProfileRow? newValue)
    {
        if (_applyingProfile || newValue is null)
        {
            NotifyProfileActivationChanged();
            SyncNavItems(); // the menu expands the selected profile; the caller loads its curves (if needed)
            return;
        }
        // Opening a profile is not an edit: it rebuilds the editor, which must not become a history step.
        WithoutHistory(() =>
        {
            // Aktuellen Editor-Stand ins bisherige Profil sichern, dann das neue Profil laden.
            if (oldValue is not null)
            {
                oldValue.Curves = CurrentCurveConfigs();
                oldValue.Assignments = CurrentAssignments();
            }
            ApplyProfileToEditor(newValue);
            NotifyProfileActivationChanged();
        });
        SyncNavItems();
        // Selecting alone changes no configuration (both profiles keep their state); the check only brings the
        // change markers of the freshly loaded curve rows up to date.
        RefreshDirty();
    }

    /// <summary>Meldet die von Auswahl/Aktivierung abgeleiteten Properties nach und zieht die Kurven-Badges mit.</summary>
    private void NotifyProfileActivationChanged()
    {
        OnPropertyChanged(nameof(SelectedProfileIsActive));
        OnPropertyChanged(nameof(SelectedProfileActive));
        OnPropertyChanged(nameof(EditingInactiveProfileText));
        DeleteProfileCommand.NotifyCanExecuteChanged(); // the running profile cannot be deleted
        NotifySwitchAvailability(); // activating the profile shown, switching back to the previous one
        NotifyNavSelection();
        bool active = SelectedProfileIsActive;
        foreach (CurveEditRow curve in Curves)
            curve.SetProfileActive(active);
    }

    /// <summary>Legt ein <b>leeres</b> Profil an: genau eine Default-Kurve, keine Lüfter-Zuordnungen.</summary>
    [RelayCommand]
    private void AddProfile()
    {
        var row = new ProfileRow(NewProfileId(), Localizer.Instance["CurveEditorCtrl.NewProfileName"],
                                 new[] { BuildDefaultCurveConfig() }, Normalized([]));
        RecordStep(() =>
        {
            StashEditorIntoSelectedProfile(); // the profile shown so far keeps its unsaved edits
            Profiles.Add(row);
            _applyingProfile = true;
            SelectedProfile = row;
            _applyingProfile = false;
            ApplyProfileToEditor(row); // die Default-Kurve in den Editor laden (Zuordnungen bleiben leer)
            Pane = CurveTabPane.Profile; // der Profil-Editor ist auch das Namensfeld des neuen Profils
            NotifyProfileActivationChanged(); // neu und damit nicht aktiv - Schalter/Badges nachziehen
        });
    }

    /// <summary>Dupliziert das aktuelle Profil (Kurven + Zuordnungen) als „… (Kopie)".</summary>
    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is not { } source)
            return;
        RecordStep(() =>
        {
            StashEditorIntoSelectedProfile(); // the original keeps its unsaved edits as well
            var row = new ProfileRow(NewProfileId(), Localizer.Instance.Format("CurveEditorCtrl.CopySuffix", source.Name),
                                     source.Curves, source.Assignments);
            Profiles.Add(row);
            _applyingProfile = true; // Kopie = aktueller Editor-Stand, kein Nachladen nötig
            SelectedProfile = row;
            _applyingProfile = false;
            Pane = CurveTabPane.Profile;
            NotifyProfileActivationChanged(); // die Kopie ist nicht aktiv, auch wenn die Vorlage es war
        });
    }

    /// <summary>The curve a new profile starts with: the first visible sensor as its source, the Balanced preset points.</summary>
    private CurveConfig BuildDefaultCurveConfig() => new()
    {
        Id = NewCurveId(),
        Name = Localizer.Instance["CurveEditorCtrl.NewCurveName"],
        SourceSensorIds = DefaultSourceIds(),
        Aggregation = SensorAggregation.Max,
        HysteresisC = CurveConfig.DefaultHysteresisC,
        InterpolationMode = InterpolationMode.Linear,
        // The same table as the "Ausgewogen" template: the default curve comes from Core, at 100 % by 86 °C.
        Points = CurveTemplates.Points(CurveTemplate.Balanced).ToList(),
    };

    /// <summary>
    /// Not deletable: the running profile (the daemon always needs one - deleting it would have to switch live,
    /// which no undo could take back) and the last one. Nothing at all while a switch waits for its confirmation: if the
    /// daemon keeps the previous profile, the editor has to be able to show that one as running.
    /// </summary>
    private bool CanDeleteProfile() =>
        SelectedProfile is { } shown && !ReferenceEquals(shown, ActiveProfile) && Profiles.Count > 1 && _pendingSwitchId is null;

    /// <summary>
    /// Deletes the profile shown - without asking, as a history step of its own with „Rückgängig" in its toast and
    /// as a plain draft change (saved with „Übernehmen"). The editor then shows the running profile.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } removed || !CanDeleteProfile())
            return;

        string name = removed.Name;
        RecordStep(() =>
        {
            Profiles.Remove(removed);
            ProfileRow? next = ActiveProfile ?? Profiles.FirstOrDefault();
            _applyingProfile = true;
            SelectedProfile = next;
            _applyingProfile = false;
            if (next is not null)
                ApplyProfileToEditor(next);
            Pane = CurveTabPane.Profile;
            NotifyProfileActivationChanged();
        });
        ShowDeletedToast(Localizer.Instance.Format("CurveEditorCtrl.ProfileDeleted", name));
    }

    /// <summary>Lädt die Kurven + Zuordnungen eines Profils in den Editor.</summary>
    private void ApplyProfileToEditor(ProfileRow profile) =>
        ReloadEditor(profile.Curves, profile.Assignments);

    /// <summary>Lädt Kurven + Lüfter-Zuordnungen in den Editor (für Profilwechsel und Airflow-Übernahme).</summary>
    private void ReloadEditor(IReadOnlyList<CurveConfig> curves, IReadOnlyList<ProfileAssignment> assignments)
    {
        Curves.Clear();
        bool profileActive = SelectedProfileIsActive;
        foreach (CurveConfig c in curves)
        {
            CurveEditRow row = CurveEditRow.From(c, Sensors, Fans);
            row.SetProfileActive(profileActive); // Aktiv-Badge: nur Kurven des laufenden Profils regeln wirklich
            Curves.Add(row);
        }

        List<ProfileAssignment> normalized = Normalized(assignments);
        foreach ((FanAssignRow fan, ProfileAssignment a) in Fans.Zip(normalized))
            fan.Selected = Curves.FirstOrDefault(c => c.Id == a.CurveId);
        _undetectedAssignments = normalized.Skip(Fans.Count).ToList();

        SelectedCurve = Curves.FirstOrDefault();
        RebuildSelectedCurveFans(); // auch wenn SelectedCurve unverändert null bleibt
        RefreshAirflow();           // the proposal compares with the curves just loaded
    }

    /// <summary>
    /// „2 Kurven · 3 Lüfter" for the profile menu - for the profile on screen from the editor's state, which its row
    /// only gets when another profile is opened. A fan counts once, if its curve belongs to the profile.
    /// </summary>
    public string ProfileSummary(ProfileRow profile)
    {
        Localizer l = Localizer.Instance;
        Profile state = CurrentStateOf(profile);
        var curveIds = state.Curves.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        int fans = state.Assignments
            .Where(a => a.CurveId is { } id && curveIds.Contains(id))
            .Select(a => a.FanId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int curves = state.Curves.Count;
        return l.Format("ProfileMenu.Summary",
            curves == 1 ? l["ProfileMenu.OneCurve"] : l.Format("ProfileMenu.Curves", curves),
            fans == 1 ? l["ProfileMenu.OneFan"] : l.Format("ProfileMenu.Fans", fans));
    }

    private List<CurveConfig> CurrentCurveConfigs() => Curves.Select(c => c.ToConfig()).ToList();

    private List<ProfileAssignment> CurrentAssignments() =>
        Fans.Select(f => new ProfileAssignment(f.FanId, f.Selected?.Id)).Concat(_undetectedAssignments).ToList();

    /// <summary>
    /// A profile's assignments in the shape <see cref="CurrentAssignments"/> writes them: one per detected fan in the
    /// fans' order (null - no curve - for a fan the profile does not list, which is what the editor shows for it), then
    /// the entries of fans not detected right now, as they are. Every profile the editor takes in gets this shape;
    /// otherwise merely opening one that lists fewer fans or another order would count as an unsaved change. The first
    /// entry per fan wins, as in the daemon.
    /// </summary>
    private List<ProfileAssignment> Normalized(IEnumerable<ProfileAssignment> assignments)
    {
        var known = Fans.Select(f => f.FanId).ToHashSet(StringComparer.Ordinal);
        var byFan = new Dictionary<string, string?>(StringComparer.Ordinal);
        var undetected = new List<ProfileAssignment>();
        foreach (ProfileAssignment a in assignments)
        {
            if (a.FanId is null || !byFan.TryAdd(a.FanId, a.CurveId))
                continue;
            if (!known.Contains(a.FanId))
                undetected.Add(a);
        }
        return Fans.Select(f => new ProfileAssignment(f.FanId, byFan.GetValueOrDefault(f.FanId)))
            .Concat(undetected)
            .ToList();
    }

    /// <summary>A profile from outside the editor (the daemon's config, a service result) as the editor holds it.</summary>
    private ProfileRow RowOf(Profile p) => new(p.Id, p.Name, p.Curves, Normalized(p.Assignments));
}
