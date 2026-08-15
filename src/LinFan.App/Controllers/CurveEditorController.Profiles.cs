// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

// Profil-Verwaltung (Anlegen/Duplizieren/Umbenennen/Löschen, Laden).
public partial class CurveEditorController
{
    // --- Profile ---------------------------------------------------------------

    /// <summary>
    /// True, wenn der Editor das laufende Setup zeigt: entweder ist das gewählte Profil das aktive, oder es
    /// gibt überhaupt keine Profile - dann (Altbestand vor der Profil-Migration) bearbeitet der Editor die
    /// laufende Konfiguration direkt.
    /// </summary>
    public bool SelectedProfileIsActive =>
        Profiles.Count == 0 || (SelectedProfile is not null && ReferenceEquals(SelectedProfile, ActiveProfile));

    /// <summary>
    /// Whether the running profile may be switched at all. Blocked while the editor holds unsaved changes:
    /// the daemon would switch to its <b>stored</b> copy of the profile, which is not what the screen shows -
    /// so the user applies first and then switches, instead of the two silently disagreeing.
    /// </summary>
    public bool CanChangeActiveProfile => !HasUnsavedChanges;

    /// <summary>Ob das gezeigte Profil aktiviert werden kann (nicht schon aktiv, nichts Ungespeichertes offen).</summary>
    public bool CanActivateSelectedProfile => SelectedProfile is not null && !SelectedProfileIsActive && CanChangeActiveProfile;

    /// <summary>
    /// Ob der Grund für den gesperrten Aktiv-Schalter eingeblendet wird: offene Änderungen. Bewusst nur für
    /// ein Profil, das nicht ohnehin läuft - beim laufenden ist der Schalter gesperrt, <i>weil</i> es aktiv
    /// ist, und das sagt er (eingeschaltet) bereits selbst.
    /// </summary>
    public bool ShowActivationBlockedHint => HasUnsavedChanges && !SelectedProfileIsActive;

    /// <summary>
    /// Bindungsziel des Aktiv-Schalters im Profil-Editor. Nur einschaltbar: es ist immer genau ein Profil
    /// aktiv, ein „aus" hätte keinen Empfänger - der Schalter ist beim aktiven Profil daher gesperrt.
    /// </summary>
    public bool SelectedProfileActive
    {
        get => SelectedProfileIsActive;
        set
        {
            if (value && CanActivateSelectedProfile)
                ActiveProfile = SelectedProfile;
            else
                OnPropertyChanged(); // abgelehnt → den Schalter zurückschnappen lassen
        }
    }

    partial void OnActiveProfileChanged(ProfileRow? oldValue, ProfileRow? newValue)
    {
        foreach (ProfileRow p in Profiles)
            p.IsActive = ReferenceEquals(p, newValue);
        NotifyProfileActivationChanged();

        if (_applyingActiveProfile || newValue is null)
            return;

        _ = _activateProfile?.Invoke(newValue.Id);
        // Der Daemon persistiert den Wechsel selbst (ControlLoopService → ProfileService.Apply + Save), wie
        // beim Kurven-An/Aus. Die Baseline zieht deshalb mit, statt den „Nicht gespeichert"-Hinweis zu zünden.
        RebaselineActiveProfile(newValue.Id);
        RefreshDirty();
    }

    partial void OnSelectedProfileChanged(ProfileRow? oldValue, ProfileRow? newValue)
    {
        if (_applyingProfile || newValue is null)
        {
            NotifyProfileActivationChanged();
            return;
        }
        // Aktuellen Editor-Stand ins bisherige Profil sichern, dann das neue Profil laden.
        if (oldValue is not null)
        {
            oldValue.Curves = CurrentCurveConfigs();
            oldValue.Assignments = CurrentAssignments();
        }
        ApplyProfileToEditor(newValue);
        NotifyProfileActivationChanged();
        // Die Auswahl allein ändert die Konfiguration nicht (beide Profile behalten ihren Stand) - der
        // Neuaufbau der Kurven-Collection hat aber MarkDirty ausgelöst, das hier wieder verfällt.
        RefreshDirty();
    }

    /// <summary>Meldet die von Auswahl/Aktivierung abgeleiteten Properties nach und zieht die Kurven-Badges mit.</summary>
    private void NotifyProfileActivationChanged()
    {
        OnPropertyChanged(nameof(SelectedProfileIsActive));
        OnPropertyChanged(nameof(SelectedProfileActive));
        OnPropertyChanged(nameof(CanActivateSelectedProfile));
        OnPropertyChanged(nameof(CanChangeActiveProfile));
        OnPropertyChanged(nameof(ShowActivationBlockedHint));
        bool active = SelectedProfileIsActive;
        foreach (CurveEditRow curve in Curves)
            curve.SetProfileActive(active);
    }

    /// <summary>Legt ein <b>leeres</b> Profil an: genau eine Default-Kurve, keine Lüfter-Zuordnungen.</summary>
    [RelayCommand]
    private void AddProfile()
    {
        var row = new ProfileRow($"profile-{Guid.NewGuid():N}"[..16], Localizer.Instance["CurveEditorCtrl.NewProfileName"],
                                 new[] { BuildDefaultCurveConfig() }, Array.Empty<ProfileAssignment>());
        Profiles.Add(row);
        _applyingProfile = true;
        SelectedProfile = row;
        _applyingProfile = false;
        ApplyProfileToEditor(row); // die Default-Kurve in den Editor laden (Zuordnungen bleiben leer)
        Pane = CurveTabPane.Profile; // der Profil-Editor ist auch das Namensfeld des neuen Profils
        NotifyProfileActivationChanged(); // neu und damit nicht aktiv - Schalter/Badges nachziehen
    }

    /// <summary>Dupliziert das aktuelle Profil (Kurven + Zuordnungen) als „… (Kopie)".</summary>
    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is not { } source)
            return;
        var row = new ProfileRow($"profile-{Guid.NewGuid():N}"[..16], Localizer.Instance.Format("CurveEditorCtrl.CopySuffix", source.Name),
                                 CurrentCurveConfigs(), CurrentAssignments());
        Profiles.Add(row);
        _applyingProfile = true; // Kopie = aktueller Editor-Stand, kein Nachladen nötig
        SelectedProfile = row;
        _applyingProfile = false;
        Pane = CurveTabPane.Profile;
        NotifyProfileActivationChanged(); // die Kopie ist nicht aktiv, auch wenn die Vorlage es war
    }

    /// <summary>Baut eine Default-Kurve (erster sichtbarer Sensor als Quelle, Standard-Stützpunkte).</summary>
    private CurveConfig BuildDefaultCurveConfig()
    {
        SensorOption? source = VisibleSensors.FirstOrDefault() ?? Sensors.FirstOrDefault();
        return new CurveConfig
        {
            Id = $"curve-{Guid.NewGuid():N}"[..14],
            Name = Localizer.Instance["CurveEditorCtrl.NewCurveName"],
            SourceSensorIds = source is null ? Array.Empty<string>() : new[] { source.Id },
            Aggregation = SensorAggregation.Max,
            HysteresisC = 2.0,
            InterpolationMode = InterpolationMode.Linear,
            Points = DefaultCurvePoints.Select(p => new CurvePoint((double)p.Temp, (double)p.Percent)).ToList(),
        };
    }

    /// <summary>Löscht das aktuelle Profil. Auto-Save-Verhalten wie bei <see cref="DeleteCurve"/> (bedingt, nur wenn vorher sauber).</summary>
    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (SelectedProfile is not { } removed)
            return;

        bool wasClean = !HasUnsavedChanges; // VOR jeder Mutation lesen
        bool wasActive = ReferenceEquals(ActiveProfile, removed);

        Profiles.Remove(removed);
        ProfileRow? next = Profiles.FirstOrDefault();
        _applyingProfile = true;
        SelectedProfile = next;
        _applyingProfile = false;
        if (next is not null)
            ApplyProfileToEditor(next);

        // Deleting the running profile has to hand the fans to another one - the daemon needs an active
        // profile at all times. Goes through the normal path, so the switch reaches it right away.
        if (wasActive)
            ActiveProfile = next;
        NotifyProfileActivationChanged();

        if (wasClean)
            await Save();
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

        Dictionary<string, string?> map = assignments.ToDictionary(a => a.FanId, a => a.CurveId);
        foreach (FanAssignRow fan in Fans)
            fan.Selected = map.TryGetValue(fan.FanId, out string? curveId)
                ? Curves.FirstOrDefault(c => c.Id == curveId)
                : null;

        SelectedCurve = Curves.FirstOrDefault();
        RebuildSelectedCurveFans(); // auch wenn SelectedCurve unverändert null bleibt
        RefreshAirflowStatus();     // die Zeilen verweisen auf die eben ersetzten Kurven-Zeilen
    }

    private List<CurveConfig> CurrentCurveConfigs() => Curves.Select(c => c.ToConfig()).ToList();

    private List<ProfileAssignment> CurrentAssignments() =>
        Fans.Select(f => new ProfileAssignment(f.FanId, f.Selected?.Id)).ToList();
}
