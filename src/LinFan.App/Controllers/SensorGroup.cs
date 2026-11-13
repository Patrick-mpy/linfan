// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// A sensor group as an overview tile: the highest value large, a bar up to 100 °C, and - opened - the group's sensors
/// with their history. The rows are the live <see cref="SensorRow"/>s; <see cref="Refresh"/> recomputes the summary.
/// </summary>
public partial class SensorGroup : ObservableObject
{
    /// <summary>The group key a sensor's configured group stands for: trimmed, "" for no group.</summary>
    public static string KeyOf(string? group) => string.IsNullOrWhiteSpace(group) ? "" : group.Trim();

    /// <summary>Identity of the group across rebuilds (compared case-insensitively); "" for ungrouped sensors.</summary>
    public string Key { get; }

    public string Name { get; }
    public ObservableCollection<SensorRow> Sensors { get; } = new();

    /// <summary>Opened: the tile spans its row and lists the sensors. Kept by the controller per <see cref="Key"/>.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>The highest readable value of the group (°C), or NaN.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxDisplay), nameof(IsHot), nameof(BarPercent))]
    private double _max = double.NaN;

    [ObservableProperty] private string _sub = "";

    private readonly Action<SensorGroup>? _onToggled;

    public SensorGroup(string name, string key, Action<SensorGroup>? onToggled = null)
    {
        Name = name;
        Key = key;
        _onToggled = onToggled;
    }

    public string MaxDisplay => double.IsNaN(Max) ? "-" : Max.ToString("0.0", CultureInfo.InvariantCulture) + " °C";

    public bool IsHot => TemperatureLevels.IsHot(Max);

    /// <summary>Fill of the bar: the value on a 0-100 °C scale.</summary>
    public double BarPercent => double.IsNaN(Max) ? 0 : Math.Clamp(Max, 0, 100);

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        _onToggled?.Invoke(this);
    }

    /// <summary>Recomputes the summary from the live rows (NaN readings are ignored).</summary>
    public void Refresh()
    {
        double[] readable = Sensors.Select(s => s.Value).Where(v => !double.IsNaN(v)).ToArray();
        Max = readable.Length == 0 ? double.NaN : readable.Max();
        Sub = Sensors.Count == 1
            ? Sensors[0].Name
            : Localizer.Instance.Format("Overview.TileHighestOf", Sensors.Count);
    }
}
