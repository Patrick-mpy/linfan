// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;

namespace LinFan.App.Controllers;

/// <summary>Eine benannte Sensor-Gruppe für die Quell-Auswahl einer Kurve — gebündelt wie die Dashboard-Sensorgruppen.</summary>
public sealed class SensorCheckGroup
{
    public string Name { get; }
    public ObservableCollection<SensorCheck> Sensors { get; } = new();

    public SensorCheckGroup(string name) => Name = name;
}
