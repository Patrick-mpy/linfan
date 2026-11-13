// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

// Geräte-Tab: Sensor-/Lüfter-Zeilen-Filter (Suche, „Versteckte ausblenden“).
public partial class CurveEditorController
{
    // --- Sensoren-Zeile: Sichtbarkeit (Quell-Filter) + Gruppe ------------------

    private void OnSensorRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Dirty-Prüfung zuerst - die Seiteneffekt-Zweige unten kehren früh zurück (würden Name nie erreichen).
        // Live-Wert (LiveValue) bewusst ausgenommen.
        // So is the selection (IsSelected/IsDragged): view state only, never an edit.
        if (e.PropertyName is nameof(SensorOption.Name) or nameof(SensorOption.Visible) or nameof(SensorOption.Group))
            MarkDirty();

        if (e.PropertyName == nameof(SensorOption.IsSelected))
        {
            RefreshSensorSelection();
            return;
        }
        if (e.PropertyName == nameof(SensorOption.Group))
        {
            RebuildSourceSections(); // the sensor moves to another section of the curve editor
            RebuildSensorSections(); // ... and of the settings list
            return;
        }
        if (e.PropertyName != nameof(SensorOption.Visible) || sender is not SensorOption s)
            return;
        if (s.Visible && !VisibleSensors.Contains(s))
            VisibleSensors.Add(s);
        else if (!s.Visible)
            VisibleSensors.Remove(s);
        RebuildFilteredSensors(); // with hidden ones not shown, the toggled row has to go (or come back) right away
        NotifyHiddenSensors();

        // The eye toggle must update every curve's source checkbox list immediately - each
        // CurveEditRow owns its own list (mirror of RebuildSelectedCurveFans for the fan side).
        foreach (CurveEditRow curve in Curves)
            curve.RebuildSensorChecks();
        RebuildSourceSections(); // they wrap the checks just replaced
    }

    // --- Settings lists: search and the hidden entries ---------------------------

    partial void OnSensorSearchChanged(string value)
    {
        RebuildFilteredSensors();
        ClearSensorSearchCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowHiddenSensorsChanged(bool value)
    {
        RebuildFilteredSensors();
        NotifyHiddenSensors();
    }

    partial void OnFanSearchChanged(string value)
    {
        RebuildFilteredFans();
        ClearFanSearchCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowHiddenFansChanged(bool value)
    {
        RebuildFilteredFans();
        NotifyHiddenFans();
    }

    public bool IsSearchingSensors => !string.IsNullOrWhiteSpace(SensorSearch);

    public bool IsSearchingFans => !string.IsNullOrWhiteSpace(FanSearch);

    /// <summary>„1 Treffer" / „n Treffer" while searching - what the list shows.</summary>
    public string SensorHitsText => HitsText(FilteredSensors.Count);

    public string FanHitsText => HitsText(FilteredFans.Count);

    private static string HitsText(int count) =>
        count == 1 ? Localizer.Instance["MainWindow.OneHit"] : Localizer.Instance.Format("MainWindow.Hits", count);

    private int HiddenSensorCount => Sensors.Count(s => !s.Visible);

    private int HiddenFanCount => Fans.Count(f => !f.Visible);

    /// <summary>„Ausgeblendete anzeigen (2)".</summary>
    public string ShowHiddenSensorsLabel => Localizer.Instance.Format("MainWindow.ShowHidden", HiddenSensorCount);

    public string ShowHiddenFansLabel => Localizer.Instance.Format("MainWindow.ShowHidden", HiddenFanCount);

    // Nothing hidden: nothing to show or leave out - unless the hidden ones are left out right now, so it can go back on.
    public bool CanToggleHiddenSensors => HiddenSensorCount > 0 || !ShowHiddenSensors;

    public bool CanToggleHiddenFans => HiddenFanCount > 0 || !ShowHiddenFans;

    /// <summary>Esc or the × in the search field; only while there is something to clear (else Esc goes on).</summary>
    [RelayCommand(CanExecute = nameof(IsSearchingSensors))]
    private void ClearSensorSearch() => SensorSearch = "";

    [RelayCommand(CanExecute = nameof(IsSearchingFans))]
    private void ClearFanSearch() => FanSearch = "";

    private void NotifyHiddenSensors()
    {
        OnPropertyChanged(nameof(ShowHiddenSensorsLabel));
        OnPropertyChanged(nameof(CanToggleHiddenSensors));
    }

    private void NotifyHiddenFans()
    {
        OnPropertyChanged(nameof(ShowHiddenFansLabel));
        OnPropertyChanged(nameof(CanToggleHiddenFans));
    }

    private void RebuildFilteredSensors()
    {
        IEnumerable<SensorOption> view = Sensors;
        if (!ShowHiddenSensors)
            view = view.Where(s => s.Visible);
        if (!string.IsNullOrWhiteSpace(SensorSearch))
            view = view.Where(s => FilteredListView.Matches(SensorSearch, s.Name, s.Id));
        FilteredListView.Sync(FilteredSensors, view);
        RebuildSensorSections();
        OnPropertyChanged(nameof(IsSearchingSensors));
        OnPropertyChanged(nameof(SensorHitsText));
    }

    private void RebuildFilteredFans()
    {
        IEnumerable<FanAssignRow> view = Fans;
        if (!ShowHiddenFans)
            view = view.Where(f => f.Visible);
        if (!string.IsNullOrWhiteSpace(FanSearch))
            view = view.Where(f => FilteredListView.Matches(FanSearch, f.DisplayName, f.HardwareName));
        FilteredListView.Sync(FilteredFans, view);
        RebuildFanSections();
        OnPropertyChanged(nameof(IsSearchingFans));
        OnPropertyChanged(nameof(FanHitsText));
    }
}
