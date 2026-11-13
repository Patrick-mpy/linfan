// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

// History (Ctrl+Z / Ctrl+Y): records user edits only, restores without live side effects.
public partial class CurveEditorController
{
    private bool CanUndo() => _initialized && _history.CanUndo;

    private bool CanRedo() => _initialized && _history.CanRedo;

    /// <summary>Ctrl+Z. Works offline too: it only changes the draft, nothing reaches the daemon until „Übernehmen".</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => StepHistory(forward: false);

    /// <summary>Ctrl+Y / Ctrl+Shift+Z.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => StepHistory(forward: true);

    private void StepHistory(bool forward)
    {
        if (!_initialized)
            return;
        FlushHistory();
        if (_history.Current is not { } currentJson)
            return;

        var current = new EditSnapshot(currentJson, CurrentSelection());
        EditSnapshot? target = forward ? _history.Redo(current) : _history.Undo(current);
        if (target is null)
            return;

        if (Deserialize(target.Config) is { } config)
            RestoreFrom(config, target.Selection);
        _history.Recapture(RefreshDirty() ?? currentJson);
        SetStatus(Localizer.Instance[forward ? "CurveEditorCtrl.Redone" : "CurveEditorCtrl.Undone"], autoHide: true);
    }

    /// <summary>The selection the history stores with a step and puts back on undo.</summary>
    private EditSelection CurrentSelection() => new(SelectedProfile?.Id, SelectedCurve?.Id, Pane);

    /// <summary>
    /// Runs a programmatic change - a rebuild, a selection, a live update - without recording it: the edit
    /// handlers it triggers neither open a history step nor run the dirty check (the caller reconciles once).
    /// The settings sections and the airflow page it touches are rebuilt once at the end instead of once per changed row.
    /// </summary>
    private void WithoutHistory(Action change)
    {
        _nonEditDepth++;
        try
        {
            change();
        }
        finally
        {
            _nonEditDepth--;
            if (_nonEditDepth == 0)
            {
                if (_sensorSectionsStale)
                    RebuildSensorSections();
                if (_fanSectionsStale)
                    RebuildFanSections();
                if (_airflowStale)
                    RefreshAirflow();
            }
        }
    }

    /// <summary>
    /// A user action that always forms its own step (add, duplicate, delete, discard all, airflow): closes a
    /// running burst first, so the state before is exact, and the next edit starts a fresh step.
    /// </summary>
    private void RecordStep(Action change)
    {
        DropActionToast();
        FlushHistory();
        string? before = _history.Current;
        EditSelection where = CurrentSelection();
        WithoutHistory(change);
        string? after = RefreshDirty();
        if (before is not null && after is not null)
            _history.Record(new EditSnapshot(before, where), after);
    }

    /// <summary>Closes the running edit burst now (before undo/redo/save and own steps).</summary>
    private void FlushHistory() => SettleEdits(force: true);

    /// <summary>
    /// Closes an edit burst (always when <paramref name="force"/>d, otherwise once it went quiet) and catches up a
    /// deferred dirty check - with one serialization for both.
    /// </summary>
    private void SettleEdits(bool force)
    {
        AppConfig? config = null;
        string? json = null;
        if (force || _history.BurstIsQuiet)
            _history.CloseBurst(() => json ??= Serialize(config = BuildConfig()));
        if (json is not null || _dirtyCheckDeferred)
            RefreshDirty(config, json);
    }

    /// <summary>
    /// Rebuilds the whole editor from <paramref name="config"/> and puts the selection back - the shared core of
    /// „Alles verwerfen", undo and redo. Records nothing and is never a live action: the running profile stays the
    /// one the daemon regulates with. If the snapshot predates it, the running profile is kept as it is now -
    /// falling back to another one would let the next save switch the running profile without an activation.
    /// The caller reconciles the dirty state afterwards.
    /// </summary>
    private void RestoreFrom(AppConfig config, EditSelection selection) => WithoutHistory(() =>
    {
        foreach (SensorOption s in Sensors)
            s.ApplyConfig(config.Sensors.FirstOrDefault(x => x.SensorId == s.Id));
        foreach (FanAssignRow f in Fans)
            f.ApplyConfig(config.Fans.FirstOrDefault(x => x.FanId == f.FanId));
        SetSensorGroups(config.SensorGroups);
        CaseType = config.CaseType;

        List<Profile> profiles = config.Profiles.ToList();
        string? runningId = ActiveProfile?.Id ?? config.ActiveProfileId;
        if (ActiveProfile is { } running && profiles.All(p => p.Id != running.Id))
            profiles.Insert(Math.Clamp(Profiles.IndexOf(running), 0, profiles.Count), CurrentStateOf(running));

        Profiles.Clear();
        foreach (Profile p in profiles)
            Profiles.Add(RowOf(p));

        ProfileRow? active = Profiles.FirstOrDefault(p => p.Id == runningId) ?? Profiles.FirstOrDefault();
        ProfileRow? shown = Profiles.FirstOrDefault(p => p.Id == selection.ProfileId) ?? active;
        _applyingActiveProfile = true;
        ActiveProfile = active;
        _applyingActiveProfile = false;
        _applyingProfile = true;
        SelectedProfile = shown;
        _applyingProfile = false;

        if (shown is not null)
            ApplyProfileToEditor(shown);
        else
            ReloadEditor(config.Curves,
                config.Fans.Select(f => new ProfileAssignment(f.FanId, f.AssignedCurveId)).ToList());

        if (Curves.FirstOrDefault(c => c.Id == selection.CurveId) is { } curve)
            SelectedCurve = curve;
        Pane = selection.Pane == CurveTabPane.Curve && SelectedCurve is null ? CurveTabPane.Profile : selection.Pane;
        NotifyProfileActivationChanged();
        RefreshCurveActivity();
    });

    /// <summary>A profile as it stands right now - for the one in the editor that is the editor's state, not its row snapshot.</summary>
    private Profile CurrentStateOf(ProfileRow profile) =>
        ReferenceEquals(profile, SelectedProfile)
            ? profile.ToProfile(CurrentCurveConfigs(), CurrentAssignments())
            : profile.ToProfile();

    /// <summary>
    /// Writes the editor's curves and assignments into the row of the profile it shows. Needed before anything
    /// else gets loaded into the editor - otherwise the shown profile's unsaved edits would be lost.
    /// </summary>
    private void StashEditorIntoSelectedProfile()
    {
        if (SelectedProfile is not { } shown)
            return;
        shown.Curves = CurrentCurveConfigs();
        shown.Assignments = CurrentAssignments();
    }

    /// <summary>Toast after a delete: no confirmation in front of it, „Rückgängig" behind it (the history undo).</summary>
    private void ShowDeletedToast(string text) =>
        Status.SetWithAction(text, Localizer.Instance["CurveEditorCtrl.UndoAction"], UndoCommand);

    /// <summary>
    /// Hides a pending action toast once another step is about to be recorded: a delete's „Rückgängig" is the plain
    /// history undo and would then take back that step instead of the delete; a switch's „Rückgängig" needs nothing
    /// unsaved.
    /// </summary>
    private void DropActionToast()
    {
        if (Status.HasAction)
        {
            Status.Set("");
            _switchBackCommand = null;
        }
    }
}
