// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>Eine benannte Sensor-Gruppe fürs Dashboard mit ihren Zeilen.</summary>
public partial class SensorGroup : ObservableObject
{
    /// <summary>Localized fallback label for ungrouped sensors — same display-string-as-key
    /// convention as <see cref="FanGroup.Ungrouped"/>.</summary>
    public static string Ungrouped => Localizer.Instance["Group.Ungrouped"];

    public string Name { get; }
    public ObservableCollection<SensorRow> Sensors { get; } = new();

    public SensorGroup(string name) => Name = name;
}
