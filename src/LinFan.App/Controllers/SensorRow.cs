// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LinFan.App.Controllers;

/// <summary>Zeilen-Controller für einen Temperaturwert; <see cref="Display"/> und <see cref="History"/> live.</summary>
public partial class SensorRow : ObservableObject
{
    /// <summary>Anzahl der für den Verlauf (Sparkline) vorgehaltenen Messwerte.</summary>
    private const int MaxHistory = 60;

    private readonly string _unit;

    /// <summary>Stabile Sensor-Id (Hardware) - dient als Unterscheidungs-Zusatz bei doppelten Namen.</summary>
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _display = "-";

    /// <summary>Letzter numerischer Messwert (°C) oder <c>NaN</c> - treibt die Schwere-Farbe (Sparkline/Wert).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHot))]
    private double _value = double.NaN;

    /// <summary>At or above the hot mark - shown with an icon and in bold, not by colour alone.</summary>
    public bool IsHot => TemperatureLevels.IsHot(Value);

    /// <summary>Dezenter Zusatz (Hardware-Id) - nur gesetzt, wenn der Anzeigename im Dashboard mehrfach vorkommt.</summary>
    [ObservableProperty] private string _disambiguator = "";

    /// <summary>The configured group as a key (see <see cref="SensorGroup.KeyOf"/>): "" while ungrouped.</summary>
    public string GroupKey { get; private set; } = "";

    /// <summary>Rollender Verlauf der letzten Messwerte für die Sparkline.</summary>
    public ObservableCollection<double> History { get; } = new();

    public SensorRow(string id, string name, string unit)
    {
        Id = id;
        _name = name;
        _unit = unit;
    }

    /// <summary>Setzt die Gruppe aus der Konfiguration (für die Dashboard-Gruppierung).</summary>
    public void SetGroup(string? group) => GroupKey = SensorGroup.KeyOf(group);

    public void Update(string name, double value)
    {
        Name = name; // Custom-Name kann sich nach dem Speichern ändern → live übernehmen
        Value = value;
        Display = double.IsNaN(value)
            ? "n/a"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {_unit}");

        if (double.IsNaN(value))
            return;
        History.Add(value);
        while (History.Count > MaxHistory)
            History.RemoveAt(0);
    }
}
