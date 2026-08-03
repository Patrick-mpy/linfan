// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

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
    /// Zustand heraus MUSS jede Bearbeitung sofort geprüft werden (clean→dirty muss synchron greifen — daran
    /// hängen die Edit-Tests und das Banner). Ist der Editor bereits <b>dirty</b>, bleibt er dirty, solange
    /// weiter editiert wird; die einzige Rück-Transition (Edit exakt zurück auf die Baseline) wird nur
    /// vorgemerkt und beim nächsten Live-Tick (<see cref="UpdateLive"/>, ~1 s) nachgezogen — also nicht pro
    /// Sample serialisiert. Bedeutung und Zeitpunkt des Speicherns bleiben unverändert.
    /// </summary>
    private void MarkDirty()
    {
        if (HasUnsavedChanges && _savedConfigJson is not null)
        {
            _dirtyCheckDeferred = true;
            return;
        }
        RefreshDirty();
    }

    /// <summary>Vergleicht den aktuellen Editor-Stand mit der Baseline und aktualisiert <see cref="HasUnsavedChanges"/>.</summary>
    private void RefreshDirty()
    {
        _dirtyCheckDeferred = false;
        if (_savedConfigJson is null)
            return;
        HasUnsavedChanges = Serialize(BuildConfig()) != _savedConfigJson;
    }

    private static string Serialize(AppConfig config) => JsonSerializer.Serialize(config);

    /// <summary>
    /// Zieht die vom Daemon geänderten PWM-Grenzen (Kalibrier-Ergebnis) in der gespeicherten Baseline nach —
    /// analog zu <c>RebaselineCurveEnabled</c>: eine Kalibrierung ist kein Nutzer-Edit und darf weder den
    /// „Nicht gespeichert"-Banner zünden noch von „Verwerfen" auf den Vor-Kalibrier-Wert zurückgenommen werden.
    /// </summary>
    private void RebaselineFanPwmLimits(IReadOnlyDictionary<string, (int Min, int Max)> limits)
    {
        if (_savedConfigJson is null
            || JsonSerializer.Deserialize<AppConfig>(_savedConfigJson) is not { } baseline)
            return;

        _savedConfigJson = Serialize(baseline with
        {
            Fans = baseline.Fans
                .Select(f => limits.TryGetValue(f.FanId, out (int Min, int Max) l)
                    ? f with { MinPwm = (byte)Math.Clamp(l.Min, 0, 255), MaxPwm = (byte)Math.Clamp(l.Max, 0, 255) }
                    : f)
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

        MarkDirty();
    }

    private void OnProfileRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileRow.Name))
            MarkDirty();
    }


    // --- Zurücksetzen (Verwerfen) ----------------------------------------------

    /// <summary>Nur etwas zu verwerfen, solange ungespeicherte Änderungen vorliegen.</summary>
    private bool CanRevert() => HasUnsavedChanges;

    partial void OnHasUnsavedChangesChanged(bool value) => RevertCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Verwirft alle Änderungen seit dem letzten Speichern/Laden und stellt den Editor vollständig aus der
    /// Baseline (<see cref="_savedConfigJson"/>) wieder her — der Gegenpart zu <see cref="SaveCommand"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        if (string.IsNullOrEmpty(_savedConfigJson)
            || JsonSerializer.Deserialize<AppConfig>(_savedConfigJson) is not { } baseline)
            return;

        // Geräte-View-State zurücksetzen (gleiche IDs; Visible-Änderung resynct VisibleSensors über den Handler).
        foreach (SensorOption s in Sensors)
            s.ApplyConfig(baseline.Sensors.FirstOrDefault(x => x.SensorId == s.Id));
        foreach (FanAssignRow f in Fans)
            f.ApplyConfig(baseline.Fans.FirstOrDefault(x => x.FanId == f.FanId));
        RefreshAvailableGroups(); // Gruppen-Vorschläge auf den Baseline-Stand zurückführen

        // Profile aus der Baseline neu aufbauen und das aktive laden (gleicher Pfad wie Initialize).
        Profiles.Clear();
        foreach (Profile p in baseline.Profiles)
            Profiles.Add(new ProfileRow(p.Id, p.Name, p.Curves, p.Assignments));

        ProfileRow? active = Profiles.FirstOrDefault(p => p.Id == baseline.ActiveProfileId)
                             ?? Profiles.FirstOrDefault();
        _applyingProfile = true;
        SelectedProfile = active;
        _applyingProfile = false;

        if (active is not null)
            ApplyProfileToEditor(active);
        else
            ReloadEditor(baseline.Curves,
                baseline.Fans.Select(f => new ProfileAssignment(f.FanId, f.AssignedCurveId)).ToList());

        RefreshDirty();          // entspricht jetzt wieder der Baseline → HasUnsavedChanges = false
        RefreshCurveActivity();  // Aktiv-Badges nach dem Neuaufbau aktualisieren
        SetStatus(Localizer.Instance["CurveEditorCtrl.ChangesReverted"], autoHide: true);
    }

    // --- Speichern -------------------------------------------------------------

    [RelayCommand]
    private async Task Save()
    {
        // Vor dem ersten Snapshot sind Kurven/Lüfter leer — nicht speichern, sonst Datenverlust.
        if (!_initialized)
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.NotConnected"]);
            return;
        }
        if (_save is null)
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.NoSaveTarget"]);
            return;
        }

        // Das aktive Profil spiegelt den aktuellen Editor-Stand (Kurven + Zuordnungen) — für spätere Profilwechsel.
        if (SelectedProfile is { } active)
        {
            active.Curves = CurrentCurveConfigs();
            active.Assignments = CurrentAssignments();
        }

        AppConfig config = BuildConfig();
        bool ok = await _save(config);
        if (ok)
        {
            _savedConfigJson = Serialize(config); // neue Baseline → Änderungen gelten als gespeichert
            HasUnsavedChanges = false;
            SetStatus(Localizer.Instance["CurveEditorCtrl.Saved"], autoHide: true);
        }
        else
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.SaveFailed"]);
        }
    }

    /// <summary>
    /// Baut die vollständige <see cref="AppConfig"/> aus dem aktuellen Editor-Stand — ohne Seiteneffekte.
    /// Das aktive Profil bekommt die aktuellen Editor-Kurven/-Zuordnungen, ohne den ProfileRow-Snapshot zu ändern.
    /// </summary>
    private AppConfig BuildConfig()
    {
        List<CurveConfig> curves = CurrentCurveConfigs();
        List<ProfileAssignment> assignments = CurrentAssignments();
        string? activeId = SelectedProfile?.Id;

        return new AppConfig
        {
            Curves = curves, // aktive Kurven = aktuelle Editor-Kurven
            Fans = Fans.Select(f => f.ToConfig()).ToList(),
            Sensors = Sensors.Select(s => s.ToConfig()).ToList(),
            Profiles = Profiles
                .Select(p => p.Id == activeId ? p.ToProfile(curves, assignments) : p.ToProfile())
                .ToList(),
            ActiveProfileId = activeId,
        };
    }

    private CancellationTokenSource? _statusCts;

    /// <summary>Setzt die Statuszeile; mit <paramref name="autoHide"/> blendet sie nach ein paar Sekunden aus.</summary>
    private void SetStatus(string text, bool autoHide = false)
    {
        Status = text;
        _statusCts?.Cancel(); // einen früheren Auto-Hide abbrechen
        _statusCts = null;
        if (!autoHide)
            return;

        _statusCts = new CancellationTokenSource();
        _ = ClearStatusAfterAsync(_statusCts);
    }

    private async Task ClearStatusAfterAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_statusAutoHide, cts.Token);
            Status = "";
        }
        catch (OperationCanceledException)
        {
            // durch einen neueren Status abgelöst
        }
        finally
        {
            // Feld freigeben, falls es noch auf genau dieses (jetzt entsorgte) CTS zeigt — sonst würde
            // der nächste SetStatus auf einem disposed CTS Cancel() aufrufen (ObjectDisposedException).
            // Single-threaded UI-Dispatch: kein Race, ReferenceEquals genügt.
            if (ReferenceEquals(_statusCts, cts))
                _statusCts = null;
            cts.Dispose();
        }
    }
}
