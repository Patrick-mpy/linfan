// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

// Kurven-CRUD + Kurve↔Lüfter-Zuordnung + Aktiv-/Enabled-Zustand.
public partial class CurveEditorController
{
    // --- Kurven ----------------------------------------------------------------

    private static string NewCurveId() => $"curve-{Guid.NewGuid():N}"[..14];

    private static string NewProfileId() => $"profile-{Guid.NewGuid():N}"[..16];

    /// <summary>Room for another curve in the profile shown - the daemon refuses a set beyond the limit.</summary>
    public bool CanAddCurve => _initialized && Curves.Count < ConfigLimits.MaxCurvesPerSet;

    /// <summary>Tooltip of the "add curve" button; at the limit it says why the button is off.</summary>
    private string AddCurveTooltip => CanAddCurve
        ? Localizer.Instance["MainWindow.AddCurveTooltip"]
        : Localizer.Instance.Format("CurveEditorCtrl.CurveLimitReached", ConfigLimits.MaxCurvesPerSet);

    /// <summary>
    /// Adds a curve from a template to the profile shown: the template's points (Core presets, linear), the sources of
    /// the curve being edited - a template is about the shape, the sensors usually stay - and no fans yet. One history
    /// step; the new curve opens in the editor.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddCurve))]
    private void AddCurveFromTemplate(CurveTemplate template)
    {
        if (!CanAddCurve)
            return;
        IEnumerable<string> sources = SelectedCurve?.Sources is { Count: > 0 } current
            ? current.Select(s => s.Id)
            : DefaultSourceIds();
        var row = new CurveEditRow(NewCurveId(),
                                   Localizer.Instance.Format("CurveEditorCtrl.TemplateCurveName", CurveTemplates.Name(template)),
                                   sources, SensorAggregation.Max, (decimal)CurveConfig.DefaultHysteresisC, Sensors,
                                   InterpolationMode.Linear, Fans);
        foreach (CurvePoint p in CurveTemplates.Points(template))
            row.AddPointRow((decimal)p.TemperatureC, (decimal)p.Percent);
        row.SetProfileActive(SelectedProfileIsActive);
        RecordStep(() =>
        {
            Curves.Add(row);
            SelectedCurve = row;
            Pane = CurveTabPane.Curve; // eine neu angelegte Kurve will bearbeitet werden
        });
        Status.Set("");
    }

    /// <summary>The source of a curve that has nothing to copy: the first visible sensor.</summary>
    private string[] DefaultSourceIds() =>
        (VisibleSensors.FirstOrDefault() ?? Sensors.FirstOrDefault()) is { } sensor ? new[] { sensor.Id } : Array.Empty<string>();

    private bool CanDuplicateCurve() => SelectedCurve is not null && CanAddCurve;

    /// <summary>Dupliziert die ausgewählte Kurve (neue Id, „… (Kopie)") und wählt die Kopie aus.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicateCurve))]
    private void DuplicateCurve()
    {
        if (SelectedCurve is not { } source || !CanAddCurve)
            return;
        CurveConfig copy = source.ToConfig() with
        {
            Id = NewCurveId(),
            Name = Localizer.Instance.Format("CurveEditorCtrl.CopySuffix", source.Name),
        };
        var row = CurveEditRow.From(copy, Sensors, Fans);
        row.SetProfileActive(SelectedProfileIsActive);
        RecordStep(() =>
        {
            Curves.Add(row);
            SelectedCurve = row;
            Pane = CurveTabPane.Curve;
        });
    }

    /// <summary>
    /// Öffnet eine Kurve im Editor - aus der Kurven-Übersicht des Profil-Editors heraus. Nur Auswahl und
    /// Seitenwechsel; die Kurve gehört bereits zum gezeigten Profil.
    /// </summary>
    [RelayCommand]
    private void EditCurve(CurveEditRow? curve)
    {
        if (curve is null)
            return;
        SelectedCurve = curve;
        Pane = CurveTabPane.Curve;
    }

    private bool CanDeleteCurve() => SelectedCurve is not null;

    /// <summary>
    /// Deletes the selected curve - without asking, as a history step of its own with „Rückgängig" in its toast.
    /// A plain draft change: it is saved with „Übernehmen" like any other.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteCurve))]
    private void DeleteCurve()
    {
        if (SelectedCurve is not { } removed)
            return;

        string name = removed.Name;
        RecordStep(() =>
        {
            foreach (FanAssignRow f in Fans.Where(f => ReferenceEquals(f.Selected, removed)))
                f.Selected = null;
            Curves.Remove(removed);
            SelectedCurve = Curves.FirstOrDefault();
            if (SelectedCurve is null)
                Pane = CurveTabPane.Profile; // nothing left to edit - show the profile the curve belonged to
        });
        ShowDeletedToast(Localizer.Instance.Format("CurveEditorCtrl.CurveDeleted", name));
    }

    partial void OnSelectedCurveChanged(CurveEditRow? value)
    {
        RebuildSelectedCurveFans();
        RebuildSourceSections();
        OnPropertyChanged(nameof(SelectedCurveEnabled)); // der Toggle im Kurven-Tab folgt der Auswahl
        DeleteCurveCommand.NotifyCanExecuteChanged();
        DuplicateCurveCommand.NotifyCanExecuteChanged();
        NotifyNavSelection();
    }

    /// <summary>A curve was added or removed: the limit of the set may have been reached or left.</summary>
    private void NotifyCurveCountChanged()
    {
        OnPropertyChanged(nameof(CanAddCurve));
        RefreshAddCurveRow();
        AddCurveFromTemplateCommand.NotifyCanExecuteChanged();
        DuplicateCurveCommand.NotifyCanExecuteChanged();
    }

    private void RefreshAddCurveRow()
    {
        AddCurveRow.CanAdd = CanAddCurve;
        AddCurveRow.Tooltip = AddCurveTooltip;
    }

    // --- Source sensors of the selected curve, per group --------------------------

    /// <summary>
    /// The source sensors of the selected curve as one collapsible section per sensor group, in the configured group
    /// order (<see cref="SensorGroups"/>) with the ungrouped ones last. Rebuilt when the curve, a sensor's group or its
    /// visibility changes - never by a selection inside, so a click on a check never rebuilds the list it is in.
    /// </summary>
    public ObservableCollection<SensorCheckGroup> SourceSensorSections { get; } = new();

    // Open state per group key, shared by all curves of the session: "CPU" stays open from curve to curve. A group
    // without an entry opens when it holds a selected source.
    private readonly Dictionary<string, bool> _sourceSectionOpen = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildSourceSections()
    {
        foreach (SensorCheckGroup old in SourceSensorSections)
            old.Detach();
        SourceSensorSections.Clear();
        if (SelectedCurve is not { } curve)
            return;

        static string KeyOf(SensorCheck c) => string.IsNullOrWhiteSpace(c.Sensor.Group) ? "" : c.Sensor.Group.Trim();
        List<SensorCheck> checks = curve.SensorChecks.OrderBy(c => c.Sensor.Name, StringComparer.OrdinalIgnoreCase).ToList();
        IEnumerable<string> keys = SensorGroupOrder.Merge(SensorGroups, checks.Select(KeyOf).Where(k => k.Length > 0))
            .Append("");
        foreach (string key in keys)
        {
            List<SensorCheck> members = checks.Where(c => string.Equals(KeyOf(c), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (members.Count == 0)
                continue; // a configured group without (visible) sensors offers nothing to pick
            bool open = _sourceSectionOpen.TryGetValue(key, out bool remembered) ? remembered : members.Any(c => c.Selected);
            string name = key.Length == 0 ? GroupLabels.Ungrouped : key;
            SourceSensorSections.Add(new SensorCheckGroup(key, name, members, open, (k, v) => _sourceSectionOpen[k] = v, RecordStep));
        }
    }

    /// <summary>
    /// An/Aus der aktuell ausgewählten Kurve - Bindungsziel des Toggles im Kurven-Tab. Setzen läuft über
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
            // Only fans the curve can drive (a read-only fan ignores every curve) and only visible ones - unless they
            // already follow this curve: such an assignment stays visible and removable (hidden is display only, the
            // regulation keeps running). A fan unticked here stays in the list until the next rebuild - the untick
            // comes from this list's own check box, and rebuilding now would tear the item away mid binding write.
            foreach (FanAssignRow fan in Fans.Where(f => (f.Visible && f.CanControl) || ReferenceEquals(f.Selected, curve)))
                SelectedCurveFans.Add(new FanCurveCheck(fan, curve));
        RebuildSelectedCurveFanGroups();
    }

    /// <summary>Bündelt die Lüfter-Checkboxen nach <see cref="FanAssignRow.GroupKey"/> (Ungruppiert zuletzt) - wie die Dashboard-Gruppen.</summary>
    private void RebuildSelectedCurveFanGroups()
    {
        SelectedCurveFanGroups.Clear();
        IEnumerable<FanCurveCheck> ordered = SelectedCurveFans
            .OrderBy(c => c.Fan.GroupKey == GroupLabels.Ungrouped ? "￿" : c.Fan.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Fan.Name, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, FanCurveCheck> grp in ordered.GroupBy(c => c.Fan.GroupKey, StringComparer.OrdinalIgnoreCase))
            SelectedCurveFanGroups.Add(new FanCheckGroup(grp.Key, grp.ToList(), RecordStep));
        OnPropertyChanged(nameof(SelectedCurveFanCount));
    }

    /// <summary>„2 Lüfter gewählt" - how many of the listed fans follow the selected curve (subtitle of the fan blocks).</summary>
    public string SelectedCurveFanCount
    {
        get
        {
            int n = SelectedCurveFans.Count(c => c.Assigned);
            return n == 1
                ? Localizer.Instance["CurveEditorCtrl.OneFanAssigned"]
                : Localizer.Instance.Format("CurveEditorCtrl.FansAssigned", n);
        }
    }

    /// <summary>An assignment changed somewhere: headers, checks and move hints of the fan blocks follow it.</summary>
    private void RefreshSelectedCurveFanStates()
    {
        foreach (FanCheckGroup group in SelectedCurveFanGroups)
            group.Refresh();
        OnPropertyChanged(nameof(SelectedCurveFanCount));
    }

    /// <summary>
    /// A fan row changed: a config field marks the editor dirty; the assignment re-reads the curves' active badges and
    /// limits, the position re-sorts the settings sections and the curve's fan blocks, the visibility the filtered lists.
    /// The selection is view state and only updates the bulk bar.
    /// </summary>
    private void OnFanRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Unabhängige Dirty-Prüfung (NICHT an die Seiteneffekt-Kette hängen): alle in FanConfig persistierten
        // Felder. Live-/View-Properties (LiveRpm, Kalibrier-/Identify-Status, ShowAdvanced, MinPercent/MaxPercent
        // = Spiegel von MinPwm/MaxPwm) bewusst ausgenommen, sonst kehrt die Pro-Tick-Serialisierung zurück.
        if (e.PropertyName is nameof(FanAssignRow.Name) or nameof(FanAssignRow.Selected)
            or nameof(FanAssignRow.Location) or nameof(FanAssignRow.Visible)
            or nameof(FanAssignRow.MinPwm) or nameof(FanAssignRow.MaxPwm))
            MarkDirty();
        if (e.PropertyName == nameof(FanAssignRow.Name))
            RefreshDefaultFanNames(); // a name typed in one row can take the number another row's placeholder showed

        // The selection of the settings list is view state, never an edit.
        if (e.PropertyName == nameof(FanAssignRow.IsSelected))
        {
            RefreshFanSelection();
            return;
        }

        if (e.PropertyName == nameof(FanAssignRow.Selected))
        {
            RefreshCurveActivity(); // also re-reads the curves' power limits
            RefreshSelectedCurveFanStates();
        }
        else if (e.PropertyName is nameof(FanAssignRow.MinPwm) or nameof(FanAssignRow.MaxPwm) or nameof(FanAssignRow.Name))
        {
            foreach (CurveEditRow curve in Curves)
                curve.NotifyLimitsChanged(); // the chart's limit bands and their captions
        }
        else if (e.PropertyName == nameof(FanAssignRow.Visible))
        {
            RebuildFilteredFans(); // with hidden ones not shown, the toggled row has to go (or come back) right away
            NotifyHiddenFans();
            RebuildSelectedCurveFans(); // der Auge-Toggle (Geräte-Tab) wirkt sofort auf die Kurven-Zuordnungsliste
        }

        // Position ändert die Bündelung der Kurven-Zuordnung → Gruppen-Header nachziehen.
        if (e.PropertyName == nameof(FanAssignRow.Location))
        {
            RebuildSelectedCurveFanGroups();
            RebuildFanSections(); // the fan moves to another section of the settings list
            if (sender is FanAssignRow moved)
                RefreshPositionMenu(moved); // its menu marks the new position
        }

        // Which fan holds which speed sensor - and by what name: every row's status and „assign by hand" list may
        // change with one pick or rename.
        if (e.PropertyName is nameof(FanAssignRow.SelectedTach) or nameof(FanAssignRow.DisplayName))
            foreach (FanAssignRow row in Fans)
                row.RefreshTach();

        // Settings › Airflow: an assignment changes the proposal, a position the schema and the balance, the
        // visibility both (hidden fans stay out), a name the labels, a stored calibration the balance's basis.
        if (e.PropertyName is nameof(FanAssignRow.Selected) or nameof(FanAssignRow.Location)
            or nameof(FanAssignRow.Visible) or nameof(FanAssignRow.DisplayName) or nameof(FanAssignRow.MaxRpm))
            RefreshAirflow();
    }

    /// <summary>The fan other than <paramref name="row"/> that is coupled to <paramref name="sensorId"/> explicitly, by name - or empty.</summary>
    private string TachOwnerOf(FanAssignRow row, string sensorId) =>
        Fans.FirstOrDefault(f => !ReferenceEquals(f, row) && f.SelectedTach?.Id == sensorId)?.DisplayName ?? "";

    /// <summary>The fan other than <paramref name="row"/> the hardware reads from <paramref name="sensorId"/>, by name - or empty.</summary>
    private string TachAutoOwnerOf(FanAssignRow row, string sensorId) =>
        Fans.FirstOrDefault(f => !ReferenceEquals(f, row) && f.AutoTachId == sensorId)?.DisplayName ?? "";

    private void RefreshCurveActivity()
    {
        foreach (CurveEditRow curve in Curves)
            curve.NotifyActiveChanged();
    }

    /// <summary>
    /// Schaltet eine Kurve live an/aus (vom Dashboard): sendet das Quick-Command an den Daemon (sofort
    /// persistiert; „aus" ⇒ zugeordnete Lüfter auf Hardware-Auto) und zieht die Dirty-Baseline und den Verlauf
    /// mit - so zündet der Toggle weder die Ungespeichert-Leiste noch nehmen „Verwerfen" oder Rückgängig ihn zurück.
    /// A running curve can only be switched while the service is reachable.
    /// </summary>
    public void SetCurveEnabled(CurveEditRow curve, bool enabled)
    {
        if (curve.Enabled == enabled)
            return;

        if (IsLiveCurve(curve))
        {
            // Offline the command cannot arrive, yet the rebaseline below would count the toggle as done - and the next
            // save after reconnecting would write the flag the daemon never switched. Refuse; the switch springs back.
            if (!IsConnected)
            {
                OnPropertyChanged(nameof(SelectedCurveEnabled));
                return;
            }

            // No history step: Enabled is outside CurveEditRow.ConfigChanged, so nothing here reaches MarkDirty.
            curve.Enabled = enabled;
            // The running curve's flag lives in up to three places that have to agree: the dashboard's live row,
            // the editor row while the editor shows the running profile, and the running profile's own curve list
            // while it does not - that list is what the next save sends as the running curves. A curve of another
            // profile is never touched: duplicated profiles share curve ids.
            IEnumerable<CurveEditRow> twins = SelectedProfileIsActive ? LiveCurves.Concat(Curves) : LiveCurves;
            foreach (CurveEditRow twin in twins)
            {
                if (!ReferenceEquals(twin, curve) && twin.Id == curve.Id)
                    twin.Enabled = enabled;
            }
            if (!SelectedProfileIsActive && ActiveProfile is { } running)
                running.Curves = running.Curves.Select(c => c.Id == curve.Id ? c with { Enabled = enabled } : c).ToList();
            _ = _setCurveEnabled?.Invoke(curve.Id, enabled);
            RebaselineCurveEnabled(curve.Id, enabled);
            RefreshDirty();
        }
        else
        {
            // Kurve eines nicht laufenden (oder noch nicht gespeicherten) Profils: im Daemon gibt es nichts
            // zu schalten - das ist eine gewöhnliche Konfigurationsänderung und wartet auf „Übernehmen".
            curve.Enabled = enabled;
            MarkDirty();
        }

        if (curve.Id == SelectedCurve?.Id)
            OnPropertyChanged(nameof(SelectedCurveEnabled)); // Editor-Toggle synchron halten (z. B. wenn das Dashboard schaltet)
    }

    /// <summary>
    /// Whether this row stands for a curve the daemon is actually regulating with: a row from
    /// <see cref="LiveCurves"/> always is; an edited row only while its profile is the active one and the
    /// curve already exists in the saved set (a freshly added one does not, however active the profile is).
    /// Ids alone would not do - duplicating a profile copies the curve ids along.
    /// </summary>
    private bool IsLiveCurve(CurveEditRow curve) =>
        LiveCurves.Contains(curve)
        || (SelectedProfileIsActive && Curves.Contains(curve) && LiveCurves.Any(c => c.Id == curve.Id));

    /// <summary>Zieht das Enabled-Flag einer Kurve in der gespeicherten Baseline und im Verlauf nach (aktive Kurven + aktives Profil).</summary>
    private void RebaselineCurveEnabled(string curveId, bool enabled)
    {
        CurveConfig Flip(CurveConfig c) => c.Id == curveId ? c with { Enabled = enabled } : c;
        RebaselineLive(config => config with
        {
            Curves = config.Curves.Select(Flip).ToList(),
            Profiles = config.Profiles
                .Select(p => p.Id == config.ActiveProfileId ? p with { Curves = p.Curves.Select(Flip).ToList() } : p)
                .ToList(),
        });
    }
}
