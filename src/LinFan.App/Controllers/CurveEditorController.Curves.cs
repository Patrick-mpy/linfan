// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

// Kurven-CRUD + Kurve↔Lüfter-Zuordnung + Aktiv-/Enabled-Zustand.
public partial class CurveEditorController
{
    // --- Kurven ----------------------------------------------------------------

    [RelayCommand]
    private void AddCurve()
    {
        SensorOption? defaultSource = VisibleSensors.FirstOrDefault() ?? Sensors.FirstOrDefault();
        string[] defaultSources = defaultSource is null ? Array.Empty<string>() : new[] { defaultSource.Id };
        var row = new CurveEditRow($"curve-{Guid.NewGuid():N}"[..14], Localizer.Instance["CurveEditorCtrl.NewCurveName"],
                                   defaultSources, SensorAggregation.Max, 2m, Sensors, VisibleSensors,
                                   InterpolationMode.Linear, Fans);
        foreach ((decimal temp, decimal percent) in DefaultCurvePoints)
            row.AddPointRow(temp, percent);
        Curves.Add(row);
        SelectedCurve = row;
        Status = "";
    }

    /// <summary>Dupliziert die ausgewählte Kurve (neue Id, „… (Kopie)") und wählt die Kopie aus.</summary>
    [RelayCommand]
    private void DuplicateCurve()
    {
        if (SelectedCurve is not { } source)
            return;
        CurveConfig copy = source.ToConfig() with
        {
            Id = $"curve-{Guid.NewGuid():N}"[..14],
            Name = Localizer.Instance.Format("CurveEditorCtrl.CopySuffix", source.Name),
        };
        var row = CurveEditRow.From(copy, Sensors, VisibleSensors, Fans);
        Curves.Add(row);
        SelectedCurve = row;
    }

    /// <summary>
    /// Löscht die ausgewählte Kurve. War der Editor vorher sauber, wird die Löschung sofort persistiert
    /// (sie ist dann die einzige offene Änderung). Lagen schon Änderungen vor, bleibt sie als ungespeicherte
    /// Änderung stehen — so committet die bestätigte Löschung keine fremden, unfertigen Edits mit.
    /// </summary>
    [RelayCommand]
    private async Task DeleteCurve()
    {
        if (SelectedCurve is not { } removed)
            return;

        bool wasClean = !HasUnsavedChanges; // VOR jeder Mutation lesen (Remove/Selected=null markieren dirty)

        foreach (FanAssignRow f in Fans.Where(f => ReferenceEquals(f.Selected, removed)))
            f.Selected = null;
        Curves.Remove(removed);
        SelectedCurve = Curves.FirstOrDefault();

        if (wasClean)
            await Save();
    }

    partial void OnSelectedCurveChanged(CurveEditRow? value)
    {
        RebuildSelectedCurveFans();
        OnPropertyChanged(nameof(SelectedCurveEnabled)); // der Toggle im Kurven-Tab folgt der Auswahl
    }

    /// <summary>
    /// An/Aus der aktuell ausgewählten Kurve — Bindungsziel des Toggles im Kurven-Tab. Setzen läuft über
    /// denselben Live-Pfad wie das Dashboard (<see cref="SetCurveEnabled"/>): sofort persistiert, kein Dirty-Banner.
    /// </summary>
    public bool SelectedCurveEnabled
    {
        get => SelectedCurve?.Enabled ?? false;
        set
        {
            if (SelectedCurve is { } curve)
                SetCurveEnabled(curve, value);
        }
    }

    private void RebuildSelectedCurveFans()
    {
        SelectedCurveFans.Clear();
        if (SelectedCurve is { } curve)
            foreach (FanAssignRow fan in Fans)
                SelectedCurveFans.Add(new FanCurveCheck(fan, curve));
        RebuildSelectedCurveFanGroups();
    }

    /// <summary>Bündelt die Lüfter-Checkboxen nach <see cref="FanAssignRow.GroupKey"/> (Ungruppiert zuletzt) — wie die Dashboard-Gruppen.</summary>
    private void RebuildSelectedCurveFanGroups()
    {
        SelectedCurveFanGroups.Clear();
        IEnumerable<FanCurveCheck> ordered = SelectedCurveFans
            .OrderBy(c => c.Fan.GroupKey == FanGroup.Ungrouped ? "￿" : c.Fan.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Fan.Name, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, FanCurveCheck> grp in ordered.GroupBy(c => c.Fan.GroupKey))
        {
            var group = new FanCheckGroup(grp.Key);
            foreach (FanCurveCheck c in grp)
                group.Fans.Add(c);
            SelectedCurveFanGroups.Add(group);
        }
    }

    /// <summary>Eine Lüfter-Zeile hat sich geändert: Zuordnung → Aktiv-Badge neu auswerten, Gruppe → Vorschläge auffrischen.</summary>
    private void OnFanRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Unabhängige Dirty-Prüfung (NICHT an die Seiteneffekt-Kette hängen): alle in FanConfig persistierten
        // Felder. Live-/View-Properties (LiveRpm, Kalibrier-/Identify-Status, ShowAdvanced, MinPercent/MaxPercent
        // = Spiegel von MinPwm/MaxPwm) bewusst ausgenommen, sonst kehrt die Pro-Tick-Serialisierung zurück.
        if (e.PropertyName is nameof(FanAssignRow.Name) or nameof(FanAssignRow.Selected)
            or nameof(FanAssignRow.Location) or nameof(FanAssignRow.Group) or nameof(FanAssignRow.Visible)
            or nameof(FanAssignRow.MinPwm) or nameof(FanAssignRow.MaxPwm))
            MarkDirty();

        if (e.PropertyName == nameof(FanAssignRow.Selected))
            RefreshCurveActivity();
        else if (e.PropertyName == nameof(FanAssignRow.Group))
            RefreshAvailableGroups();
        else if (e.PropertyName == nameof(FanAssignRow.Visible))
            RebuildFilteredFans(); // „Versteckte ausblenden" muss die getoggelte Zeile sofort ein-/ausblenden

        // Position/Gruppe ändert die Bündelung der Kurven-Zuordnung → Gruppen-Header nachziehen.
        if (e.PropertyName is nameof(FanAssignRow.Group) or nameof(FanAssignRow.Location))
            RebuildSelectedCurveFanGroups();
    }

    private void RefreshCurveActivity()
    {
        foreach (CurveEditRow curve in Curves)
            curve.NotifyActiveChanged();
    }

    /// <summary>
    /// Schaltet eine Kurve live an/aus (vom Dashboard): sendet das Quick-Command an den Daemon (sofort
    /// persistiert; „aus" ⇒ zugeordnete Lüfter auf Hardware-Auto) und zieht die Dirty-Baseline mit — so
    /// zündet der Toggle weder den „Nicht gespeichert"-Banner noch nimmt „Verwerfen" ihn zurück.
    /// </summary>
    public void SetCurveEnabled(CurveEditRow curve, bool enabled)
    {
        if (curve.Enabled == enabled)
            return;
        curve.Enabled = enabled;
        _ = _setCurveEnabled?.Invoke(curve.Id, enabled);
        RebaselineCurveEnabled(curve.Id, enabled);
        RefreshDirty();
        if (ReferenceEquals(curve, SelectedCurve))
            OnPropertyChanged(nameof(SelectedCurveEnabled)); // Editor-Toggle synchron halten (z. B. wenn das Dashboard schaltet)
    }

    /// <summary>Zieht das Enabled-Flag einer Kurve in der gespeicherten Baseline nach (aktive Kurven + aktives Profil).</summary>
    private void RebaselineCurveEnabled(string curveId, bool enabled)
    {
        if (_savedConfigJson is null
            || JsonSerializer.Deserialize<AppConfig>(_savedConfigJson) is not { } baseline)
            return;

        CurveConfig Flip(CurveConfig c) => c.Id == curveId ? c with { Enabled = enabled } : c;
        _savedConfigJson = Serialize(baseline with
        {
            Curves = baseline.Curves.Select(Flip).ToList(),
            Profiles = baseline.Profiles
                .Select(p => p.Id == baseline.ActiveProfileId ? p with { Curves = p.Curves.Select(Flip).ToList() } : p)
                .ToList(),
        });
    }

    /// <summary>
    /// Aktualisiert die Gruppen-Vorschläge (distinkte, nicht-leere Vereinigung über Sensoren und Lüfter).
    /// Speist nur die Auto-Vervollständigung — frei getippte neue Namen bleiben erhalten.
    /// </summary>
    private void RefreshAvailableGroups()
    {
        List<string> groups = Sensors.Select(s => s.Group)
            .Concat(Fans.Select(f => f.Group))
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (AvailableGroups.SequenceEqual(groups))
            return; // unverändert → kein unnötiges Auffrischen gebundener Auswahlfelder

        AvailableGroups.Clear();
        foreach (string g in groups)
            AvailableGroups.Add(g);
    }
}
