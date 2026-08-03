// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LinFan.App.Controllers;

/// <summary>Eine benannte Sensor-Gruppe fürs Dashboard mit ihren Zeilen.</summary>
public partial class SensorGroup : ObservableObject
{
    public const string Ungrouped = "Ungruppiert";

    public string Name { get; }
    public ObservableCollection<SensorRow> Sensors { get; } = new();

    public SensorGroup(string name) => Name = name;
}
