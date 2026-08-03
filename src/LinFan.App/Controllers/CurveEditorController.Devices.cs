// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

// Geräte-Tab: Sensor-/Lüfter-Zeilen-Filter (Suche, „Versteckte ausblenden“).
public partial class CurveEditorController
{
    // --- Sensoren-Zeile: Sichtbarkeit (Quell-Filter) + Gruppe ------------------

    private void OnSensorRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Dirty-Prüfung zuerst — die Seiteneffekt-Zweige unten kehren früh zurück (würden Name nie erreichen).
        // Live-Wert (LiveValue) bewusst ausgenommen.
        if (e.PropertyName is nameof(SensorOption.Name) or nameof(SensorOption.Visible) or nameof(SensorOption.Group))
            MarkDirty();

        if (e.PropertyName == nameof(SensorOption.Group))
        {
            RefreshAvailableGroups();
            return;
        }
        if (e.PropertyName != nameof(SensorOption.Visible) || sender is not SensorOption s)
            return;
        if (s.Visible && !VisibleSensors.Contains(s))
            VisibleSensors.Add(s);
        else if (!s.Visible)
            VisibleSensors.Remove(s);
        RebuildFilteredSensors(); // „Versteckte ausblenden" muss die getoggelte Zeile sofort ein-/ausblenden
    }

    // --- Geräte-Tab-Filter (Suche + „Versteckte ausblenden") -------------------

    partial void OnSensorSearchChanged(string value) => RebuildFilteredSensors();
    partial void OnHideHiddenSensorsChanged(bool value) => RebuildFilteredSensors();
    partial void OnFanSearchChanged(string value) => RebuildFilteredFans();
    partial void OnHideHiddenFansChanged(bool value) => RebuildFilteredFans();

    private void RebuildFilteredSensors()
    {
        IEnumerable<SensorOption> view = Sensors;
        if (HideHiddenSensors)
            view = view.Where(s => s.Visible);
        if (!string.IsNullOrWhiteSpace(SensorSearch))
            view = view.Where(s => FilteredListView.Matches(SensorSearch, s.Name, s.Id));
        FilteredListView.Sync(FilteredSensors, view);
    }

    private void RebuildFilteredFans()
    {
        IEnumerable<FanAssignRow> view = Fans;
        if (HideHiddenFans)
            view = view.Where(f => f.Visible);
        if (!string.IsNullOrWhiteSpace(FanSearch))
            view = view.Where(f => FilteredListView.Matches(FanSearch, f.Name, f.HardwareName));
        FilteredListView.Sync(FilteredFans, view);
    }
}
