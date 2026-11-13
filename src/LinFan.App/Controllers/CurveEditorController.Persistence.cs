// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

// Dirty-Erkennung, Speichern/Verwerfen, Config-Bau und Statuszeile.
public partial class CurveEditorController
{
    /// <summary>
    /// Einziger Einstieg der edit-getriebenen Dirty-Erkennung („eine Bearbeitung ist passiert"). Bewusst
    /// getrennt von den reconcile-Aufrufen (Revert/Save), die <see cref="RefreshDirty"/> direkt rufen.
    ///
    /// Coalescing des teuren Vergleichs: Ein Kurven-Punkt-Drag feuert hier pro Maus-Sample (Temp UND Prozent),
    /// ein voller <c>Serialize(BuildConfig())</c> je Sample war der Review-Hotpath. Aus dem <b>sauberen</b>
    /// Zustand heraus MUSS jede Bearbeitung sofort geprüft werden (clean→dirty muss synchron greifen - daran
    /// hängen die Edit-Tests und die Ungespeichert-Leiste). Ist der Editor bereits <b>dirty</b>, bleibt er dirty, solange
    /// weiter editiert wird; die einzige Rück-Transition (Edit exakt zurück auf die Baseline) wird nur
    /// vorgemerkt und beim nächsten Live-Tick (<see cref="UpdateLive"/>, ~1 s) nachgezogen - also nicht pro
    /// Sample serialisiert. Bedeutung und Zeitpunkt des Speicherns bleiben unverändert.
    /// </summary>
    private void MarkDirty()
    {
        if (_savedConfigJson is null)
            return; // before Initialize / while Resync rebuilds: no baseline to compare against
        if (_nonEditDepth > 0)
        {
            // A rebuild or a live update, not a user edit: no history step. Whoever opened the scope
            // reconciles the dirty state once at its end (or the next tick does).
            _dirtyCheckDeferred = true;
            return;
        }

        DropActionToast();

        // The history serializes only when this edit starts a new step; that capture then serves the
        // dirty check as well, so a discrete edit costs one serialization and a drag sample none.
        AppConfig? config = null;
        string? json = null;
        _history.NoteEdit(() => json ??= Serialize(config = BuildConfig()), CurrentSelection());
        if (json is not null || !HasUnsavedChanges)
            RefreshDirty(config, json);
        else
            _dirtyCheckDeferred = true;
    }

    /// <summary>
    /// Compares the editor's state with the baseline and updates <see cref="HasUnsavedChanges"/> and the change
    /// markers. Returns the state it serialized for that (null without a baseline).
    /// </summary>
    private string? RefreshDirty(AppConfig? config = null, string? json = null)
    {
        _dirtyCheckDeferred = false;
        if (_savedConfigJson is null)
            return null;
        if (config is null || json is null)
        {
            config = BuildConfig();
            json = Serialize(config);
        }
        HasUnsavedChanges = json != _savedConfigJson;
        ApplyDiff(HasUnsavedChanges && SavedConfig is { } saved ? ConfigDiff.Compute(saved, config) : ConfigDiff.None);
        // Settings › Airflow follows every edit from here - curve and profile edits, undo, discard - with the same config.
        RefreshAirflow(config);
        return json;
    }

    private static string Serialize(AppConfig config) => JsonSerializer.Serialize(config);

    private static AppConfig? Deserialize(string json) => JsonSerializer.Deserialize<AppConfig>(json);

    /// <summary>The baseline as object, for the diff - deserialized once per baseline, not per check.</summary>
    private AppConfig? SavedConfig =>
        _savedConfig ??= _savedConfigJson is null ? null : Deserialize(_savedConfigJson);

    private void SetBaseline(string? json)
    {
        _savedConfigJson = json;
        _savedConfig = null;
    }

    /// <summary>Spreads a diff onto the markers: tab dots, the bar's summary, the profile and curve rows.</summary>
    private void ApplyDiff(ConfigDiff diff)
    {
        string? shown = SelectedProfile?.Id;
        foreach (CurveEditRow curve in Curves)
            curve.IsDirty = diff.IsCurveChanged(shown, curve.Id);
        foreach (ProfileRow profile in Profiles)
            profile.IsDirty = diff.IsProfileChanged(profile.Id);
        CurvesAreaDirty = diff.CurvesAreaChanged;
        SettingsAreaDirty = diff.SettingsAreaChanged;
        _diff = diff;
        OnPropertyChanged(nameof(DirtySummary));
        OnPropertyChanged(nameof(UnsavedBarText));
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(DirtySummary));
        OnPropertyChanged(nameof(UnsavedBarText));
        OnPropertyChanged(nameof(EditingInactiveProfileText));
        OnPropertyChanged(nameof(ActivateProfileHint));
        OnPropertyChanged(nameof(ProfileSwitchBlockedReason));
        NotifyCaseTypeTexts();
        RefreshAddCurveRow();
        OnPropertyChanged(nameof(SelectedCurveFanCount));
        foreach (CurveEditRow curve in Curves.Concat(LiveCurves))
            curve.NotifyLanguageChanged();
        foreach (ProfileRow profile in Profiles)
            profile.NotifyLanguageChanged();
        RebuildSelectedCurveFans(); // group headings („Ungruppiert", positions) and the move hints are built in code
        RebuildSourceSections();
        RefreshSettingsListsLanguage();
        RefreshAirflow(); // its texts are built in code
    }

    // Own names of fans the config lists but no row shows (not detected right now) - „Lüfter N" must not reuse them.
    private IReadOnlyList<string> _fanNamesWithoutRow = [];

    /// <summary>Gives every fan row its „Lüfter N" (<see cref="DefaultFanNames"/>), the placeholder of an empty name.</summary>
    private void RefreshDefaultFanNames()
    {
        IReadOnlyList<string> defaults = DefaultFanNames.For(Fans.Select(f => f.Name).ToList(), _fanNamesWithoutRow);
        for (int i = 0; i < Fans.Count; i++)
            Fans[i].DefaultName = defaults[i];
    }

    /// <summary>
    /// Before a save: an empty name field is saved as what its placeholder shows - „Lüfter N" for a fan, the hardware label
    /// (no own name) for a sensor - and shows it as its value from now on, as after the setup assistant. Not an edit of its
    /// own: it goes out with the save.
    /// </summary>
    private void FillEmptyNames()
    {
        var filled = new Dictionary<string, string>(StringComparer.Ordinal);
        WithoutHistory(() =>
        {
            foreach (FanAssignRow fan in Fans.Where(f => string.IsNullOrWhiteSpace(f.Name)).ToList())
            {
                filled.TryAdd(fan.FanId, fan.DefaultName);
                fan.Name = fan.DefaultName;
            }
            foreach (SensorOption sensor in Sensors)
                sensor.FillEmptyName(); // a sensor's label and an empty field are the same config: no history to adjust
        });
        if (filled.Count == 0)
            return;
        // The history still holds the empty fields: undo or redo onto such a state would show the same name, yet count
        // as unsaved against the filled-in baseline. So every state gets the name, as with a live change (RebaselineLive).
        _history.Transform(json => Deserialize(json) is { } entry
            ? Serialize(entry with
            {
                Fans = entry.Fans
                    .Select(f => string.IsNullOrWhiteSpace(f.Name) && filled.TryGetValue(f.FanId, out string? name)
                        ? f with { Name = name }
                        : f)
                    .ToList(),
            })
            : json);
    }

    /// <summary>The settings lists hold texts built in code (counts, „Ungruppiert", menus) - they follow a language switch here.</summary>
    private void RefreshSettingsListsLanguage()
    {
        RefreshDefaultFanNames();
        foreach (SensorOption sensor in Sensors)
            sensor.NotifyLanguageChanged();
        foreach (FanAssignRow fan in Fans)
            fan.NotifyLanguageChanged();
        foreach (TachSensorOption option in AvailableTachSensors)
            option.NotifyLanguageChanged();
        foreach (FanLocationSection section in FanLocationSections)
            section.NotifyLanguageChanged();
        foreach (SensorGroupSection section in SensorGroupSections)
            section.NotifyLanguageChanged();
        RebuildSensorSections();
        RefreshPositionMenus();
        RefreshFanSelection();
        RefreshDeviceActionsBlocked();
        NotifyHiddenSensors();
        NotifyHiddenFans();
        OnPropertyChanged(nameof(SensorHitsText));
        OnPropertyChanged(nameof(FanHitsText));
    }

    /// <summary>Drops the localizer subscription (see the constructor). Called by the <see cref="MainController"/> on shutdown.</summary>
    public void Dispose() => Localizer.Instance.PropertyChanged -= OnLanguageChanged;

    /// <summary>„Geändert: 1 Kurve, Profile, Lüfter, Sensoren &amp; Gruppen" - only the parts that changed.</summary>
    private static string Summarize(ConfigDiff diff)
    {
        List<string> parts = SummaryParts(diff);
        Localizer l = Localizer.Instance;
        return parts.Count == 0 ? l["UnsavedBar.SummaryGeneric"] : l.Format("UnsavedBar.Summary", string.Join(", ", parts));
    }

    /// <summary>„1 Kurve", „Profile", „Lüfter", „Sensoren &amp; Gruppen" - the parts of a diff that changed.</summary>
    private static List<string> SummaryParts(ConfigDiff diff)
    {
        Localizer l = Localizer.Instance;
        var parts = new List<string>();
        if (diff.ChangedCurveCount == 1)
            parts.Add(l["UnsavedBar.OneCurve"]);
        else if (diff.ChangedCurveCount > 1)
            parts.Add(l.Format("UnsavedBar.Curves", diff.ChangedCurveCount));
        if (diff.ProfilesChanged)
            parts.Add(l["UnsavedBar.Profiles"]);
        if (diff.FansChanged)
            parts.Add(l["UnsavedBar.Fans"]);
        if (diff.SensorsChanged)
            parts.Add(l["UnsavedBar.Sensors"]);
        if (diff.CaseTypeChanged)
            parts.Add(l["UnsavedBar.CaseType"]);
        return parts;
    }

    /// <summary>
    /// Rebases the dirty baseline AND every history step onto a live change - something the daemon already
    /// persisted on its own (calibration result, curve on/off, running profile). It must neither count as an
    /// unsaved edit nor be taken back by discard or undo: an undone calibrated start PWM would be saved back
    /// below the point where the fan starts, and the daemon would never offer the calibrated value again.
    /// </summary>
    private void RebaselineLive(Func<AppConfig, AppConfig> change)
    {
        if (SavedConfig is { } baseline)
            SetBaseline(Serialize(change(baseline)));
        _history.Transform(json => Deserialize(json) is { } entry ? Serialize(change(entry)) : json);
        // The baseline a refused save-and-switch falls back to has to know the live change as well.
        if (_preSwitchBaselineJson is { } before && Deserialize(before) is { } fallback)
            _preSwitchBaselineJson = Serialize(change(fallback));
    }

    /// <summary>
    /// Carries what the daemon changed with a calibration result - the start point (MinPwm) and the stored calibration -
    /// into the baseline and the history, like <c>RebaselineCurveEnabled</c>: a calibration is no user edit, so it must
    /// neither show as unsaved nor be taken back to the pre-calibration value by discard or undo. MaxPwm is only raised
    /// where it would otherwise lie below MinPwm (the row's own setter rule) - the daemon changes MinPwm only, and an
    /// unsaved MaxPwm edit stays an edit.
    /// </summary>
    private void RebaselineFanCalibration(
        IReadOnlyDictionary<string, byte>? minPwm, IReadOnlyDictionary<string, FanCalibration>? calibration)
    {
        RebaselineLive(config => config with
        {
            Fans = config.Fans
                .Select(f =>
                {
                    if (minPwm is not null && minPwm.TryGetValue(f.FanId, out byte min))
                        f = f with { MinPwm = min, MaxPwm = Math.Max(f.MaxPwm, min) };
                    if (calibration is not null && calibration.TryGetValue(f.FanId, out FanCalibration? cal))
                        f = f with { Calibration = cal };
                    return f;
                })
                .ToList(),
        });
        RefreshDirty();
    }

    // --- Edit-Funnel: dynamische Collections an die Dirty-Erkennung koppeln ------

    /// <summary>Kurven-Membership ändert sich (Add/Delete/Duplicate/Reload) → ConfigChanged je Zeile koppeln, dann Dirty prüfen.</summary>
    private void OnCurvesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Clear() meldet Reset OHNE OldItems → die zuvor abonnierten Zeilen über das geführte Set abmelden.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (CurveEditRow row in _subscribedCurves)
                row.ConfigChanged -= OnCurveConfigChanged;
            _subscribedCurves.Clear();
        }
        else if (e.OldItems is { } removed)
        {
            foreach (CurveEditRow row in removed.OfType<CurveEditRow>())
                if (_subscribedCurves.Remove(row))
                    row.ConfigChanged -= OnCurveConfigChanged;
        }

        if (e.NewItems is { } added)
            foreach (CurveEditRow row in added.OfType<CurveEditRow>())
                if (_subscribedCurves.Add(row))
                    row.ConfigChanged += OnCurveConfigChanged;

        SyncNavItems();
        NotifyCurveCountChanged();
        MarkDirty();
    }

    private void OnCurveConfigChanged(object? sender, EventArgs e) => MarkDirty();

    /// <summary>Profil-Membership ändert sich → Namensänderung je Profil koppeln, dann Dirty prüfen.</summary>
    private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (ProfileRow row in _subscribedProfiles)
                row.PropertyChanged -= OnProfileRowChanged;
            _subscribedProfiles.Clear();
        }
        else if (e.OldItems is { } removed)
        {
            foreach (ProfileRow row in removed.OfType<ProfileRow>())
                if (_subscribedProfiles.Remove(row))
                    row.PropertyChanged -= OnProfileRowChanged;
        }

        if (e.NewItems is { } added)
            foreach (ProfileRow row in added.OfType<ProfileRow>())
                if (_subscribedProfiles.Add(row))
                    row.PropertyChanged += OnProfileRowChanged;

        DeleteProfileCommand.NotifyCanExecuteChanged(); // the last profile cannot be deleted
        SyncNavItems();
        MarkDirty();
    }

    private void OnProfileRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProfileRow.Name))
            return;
        OnPropertyChanged(nameof(EditingInactiveProfileText)); // the banner names both profiles
        MarkDirty();
    }


    // --- Zurücksetzen (Verwerfen) ----------------------------------------------

    /// <summary>Nur etwas zu verwerfen, solange ungespeicherte Änderungen vorliegen.</summary>
    private bool CanRevert() => HasUnsavedChanges;

    partial void OnHasUnsavedChangesChanged(bool value)
    {
        RevertCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        NotifySwitchAvailability(); // the activate tooltip mentions the question; switching back needs nothing unsaved
    }

    /// <summary>
    /// „Alles verwerfen": restores the editor from the baseline (<see cref="_savedConfigJson"/>) - the counterpart
    /// of <see cref="SaveCommand"/>. A history step of its own, so Ctrl+Z brings the changes back.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        if (SavedConfig is not { } baseline)
            return;
        EditSelection where = CurrentSelection();
        RecordStep(() => RestoreFrom(baseline, where));
        SetStatus(Localizer.Instance["CurveEditorCtrl.ChangesReverted"], autoHide: true);
    }

    // --- Speichern -------------------------------------------------------------

    private bool _saveInFlight;       // a config is on its way to the daemon (SendConfigAsync)
    private bool _switchSaveInFlight; // … and it switches the running profile

    /// <summary>„Übernehmen" (and Ctrl+S): only with something unsaved, the service reachable and no save on its way.</summary>
    private bool CanSave() => HasUnsavedChanges && IsConnected && !_saveInFlight;

    partial void OnIsConnectedChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RefreshDeviceActionsBlocked(); // calibrate, identify and couple need the service
        NotifySwitchAvailability();    // switching the running profile needs the service as well
        // Back online: rebuild the running curves from the next snapshot even if its config looks unchanged - the rows
        // kept offline must not carry anything the daemon never got.
        if (value)
            _liveCurvesSignature = "";
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        // Vor dem ersten Snapshot sind Kurven/Lüfter leer - nicht speichern, sonst Datenverlust.
        if (!_initialized)
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.NotConnected"], isError: true);
            return;
        }
        if (_save is null)
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.NoSaveTarget"], isError: true);
            return;
        }

        // The saved state is a step boundary; the history itself survives the save, so undo afterwards
        // simply makes the editor dirty again.
        FlushHistory();
        FillEmptyNames();

        // Das im Editor gezeigte Profil übernimmt den aktuellen Stand (Kurven + Zuordnungen) - es ist nicht
        // zwingend das aktive; welche Kurven laufen, entscheidet weiterhin ActiveProfile (siehe BuildConfig).
        StashEditorIntoSelectedProfile();

        AppConfig config = BuildConfig();
        string savedText = SavedText(config); // before the baseline moves on
        bool ok = await SendConfigAsync(config, switches: false);
        if (ok)
        {
            RefreshDirty(); // what was edited during the round trip stays pending
            SetStatus(savedText, autoHide: true);
        }
        else
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.SaveFailed"], isError: true);
        }
    }

    /// <summary>
    /// What a save does for the fans right away: changes to the fans, the sensors or the running profile apply now;
    /// changes that only touch profiles which are not running wait for a switch, and the message says so.
    /// </summary>
    private string SavedText(AppConfig config)
    {
        if (SavedConfig is not { } saved)
            return Localizer.Instance["CurveEditorCtrl.Saved"];
        ConfigDiff diff = ConfigDiff.Compute(saved, config);
        bool appliesNow = diff.FansChanged || diff.SensorsChanged
                          || (config.Profiles.Count == 0
                              ? diff.ChangedCurveCount > 0
                              : config.ActiveProfileId is { } running && diff.IsProfileChanged(running));
        if (appliesNow)
            return Localizer.Instance["CurveEditorCtrl.Saved"];
        var waiting = config.Profiles.Where(p => diff.IsProfileChanged(p.Id)).ToList();
        return waiting.Count switch
        {
            0 => Localizer.Instance["CurveEditorCtrl.SavedOnly"], // nothing that drives a fan (a profile removed, reordered)
            1 => Localizer.Instance.Format("CurveEditorCtrl.SavedNotRunning", waiting[0].Name),
            _ => Localizer.Instance["CurveEditorCtrl.SavedNotRunningMany"],
        };
    }

    /// <summary>
    /// Sends a config to the daemon - the one way out for „Übernehmen" and „Übernehmen &amp; wechseln". While it is on its
    /// way, saving and switching wait (<see cref="CanSave"/>, <see cref="CanSwitchProfile"/>): a switch sent meanwhile
    /// could reach the daemon first and be undone by this save. That narrows the race; the daemon's own ordering of pending
    /// changes closes it. A send that throws counts as failed - the caller reports it. A sent config becomes the baseline
    /// here, while the save still counts as on its way: <see cref="UpdateLive"/> adopts no start point until then, and one
    /// adopted before the baseline moved would be overwritten by it.
    /// </summary>
    /// <param name="switches">The config switches the running profile (for the reason shown while it is on its way).</param>
    private async Task<bool> SendConfigAsync(AppConfig config, bool switches)
    {
        _saveInFlight = true;
        _switchSaveInFlight = switches;
        NotifySaveInFlight();
        try
        {
            // Not the baseline: Save moves that to what it sent without waiting for the daemon. A second save before the
            // daemon took the first would then report the new value as untouched, and the daemon would keep the old one.
            var reported = new Dictionary<string, byte>(StringComparer.Ordinal);
            foreach (FanAssignRow fan in Fans)
                reported.TryAdd(fan.FanId, fan.DaemonMinPwm);
            bool sent = await _save!(config, reported);
            if (sent)
                SetBaseline(Serialize(config)); // neue Baseline → Änderungen gelten als gespeichert
            return sent;
        }
        catch
        {
            return false;
        }
        finally
        {
            _saveInFlight = false;
            _switchSaveInFlight = false;
            NotifySaveInFlight();
        }
    }

    private void NotifySaveInFlight()
    {
        SaveCommand.NotifyCanExecuteChanged();
        NotifySwitchAvailability();
    }

    /// <summary>
    /// Baut die vollständige <see cref="AppConfig"/> aus dem aktuellen Editor-Stand - ohne Seiteneffekte.
    /// Das <b>im Editor gezeigte</b> Profil bekommt die aktuellen Kurven/Zuordnungen (ohne den
    /// ProfileRow-Snapshot zu verändern); die laufenden Top-Level-Kurven und Lüfter-Zuordnungen kommen
    /// dagegen aus dem <b>aktiven</b> Profil - dieselbe Regel, die der Daemon in
    /// <see cref="ProfileService.Apply"/> ohnehin anwendet. Beim Bearbeiten eines nicht-aktiven Profils
    /// sind das zwei verschiedene Kurven-Sätze, und genau das ist der Zweck der Trennung.
    /// </summary>
    private AppConfig BuildConfig()
    {
        List<CurveConfig> editorCurves = CurrentCurveConfigs();
        List<ProfileAssignment> editorAssignments = CurrentAssignments();
        string? editedId = SelectedProfile?.Id;
        string? activeId = ActiveProfile?.Id;

        List<Profile> profiles = Profiles
            .Select(p => p.Id == editedId ? p.ToProfile(editorCurves, editorAssignments) : p.ToProfile())
            .ToList();

        Profile? active = profiles.FirstOrDefault(p => p.Id == activeId);
        List<CurveConfig> liveCurves = (active?.Curves ?? editorCurves).ToList();

        // Doppelte Lüfter-Ids in einer (hand-editierten/migrierten) Config nicht in ein ToDictionary werfen
        // lassen - erster gewinnt, wie im SnapshotBuilder.
        var liveAssignments = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (ProfileAssignment a in active?.Assignments ?? editorAssignments)
            liveAssignments.TryAdd(a.FanId, a.CurveId);

        return new AppConfig
        {
            Curves = liveCurves, // laufende Kurven = die des aktiven Profils
            Fans = Fans.Select(f => f.ToConfig())
                .Select(f => liveAssignments.TryGetValue(f.FanId, out string? curveId)
                    ? f with { AssignedCurveId = curveId }
                    : f)
                .ToList(),
            Sensors = Sensors.Select(s => s.ToConfig()).ToList(),
            SensorGroups = SensorGroups,
            CaseType = CaseType,
            Profiles = profiles,
            ActiveProfileId = activeId,
        };
    }

    /// <summary>
    /// Zieht einen live geschalteten Profilwechsel in die gespeicherte Baseline (und den Verlauf) nach: der Daemon
    /// persistiert ihn selbst (<c>ControlLoopService</c> → <see cref="ProfileService.Apply"/> + Save), wie das
    /// Kurven-An/Aus - er darf also weder den „Nicht gespeichert"-Hinweis zünden noch von „Verwerfen" oder
    /// Rückgängig zurückgenommen werden. Angewandt wird exakt die Daemon-Regel, damit Baseline und Dienst übereinstimmen.
    /// </summary>
    private void RebaselineActiveProfile(string profileId) =>
        RebaselineLive(config => ProfileService.Apply(config, profileId));

    /// <summary>Setzt die Statuszeile; mit <paramref name="autoHide"/> blendet sie nach ein paar Sekunden aus.</summary>
    private void SetStatus(string text, bool autoHide = false, bool isError = false) =>
        Status.Set(text, autoHide, isError);
}
