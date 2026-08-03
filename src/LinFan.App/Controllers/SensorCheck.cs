// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;

namespace LinFan.App.Controllers;

/// <summary>
/// Checkbox-Eintrag im Quell-Sensor-Mix einer Kurve: ist der Sensor <see cref="Sensor"/> als Quelle
/// ausgewählt? Reiner View-Zustand; das Setzen meldet die Änderung über <see cref="SelectionChanged"/>
/// an die <see cref="CurveEditRow"/>, damit Label/Live-Wert aktualisiert werden.
/// </summary>
public sealed partial class SensorCheck : ObservableObject
{
    public SensorOption Sensor { get; }

    [ObservableProperty] private bool _selected;

    public SensorCheck(SensorOption sensor, bool selected)
    {
        Sensor = sensor;
        _selected = selected;
    }

    partial void OnSelectedChanged(bool value) => SelectionChanged?.Invoke();

    /// <summary>Wird ausgelöst, wenn sich <see cref="Selected"/> ändert.</summary>
    public Action? SelectionChanged { get; set; }
}
