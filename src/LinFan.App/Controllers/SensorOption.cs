// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// Auswahloption für den Quell-Sensor einer Kurve (im ComboBox) und zugleich editierbarer
/// Anzeigename (Umbenennen). Identität über <see cref="Id"/>; der Name ist beobachtbar, sodass
/// Dropdown und Rename-Liste dieselbe Instanz teilen und sich live aktualisieren.
/// </summary>
public partial class SensorOption : ObservableObject
{
    private readonly string _unit;
    private readonly string _originalName; // Hardware-/Anzeigename beim Laden — Fallback gegen Datenverlust

    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _visible;
    [ObservableProperty] private string _group;

    /// <summary>Formatierte Live-Temperatur für den Geräte-Tab (reine Anzeige, fließt nicht in die Config).</summary>
    [ObservableProperty] private string _liveValue = "—";

    /// <summary>Vorhandene Gruppennamen für die Auto-Vervollständigung des Gruppen-Felds (geteilte Controller-Liste).</summary>
    public ObservableCollection<string> AvailableGroups { get; }

    public SensorOption(string id, string name, bool visible = true, string? group = null, string unit = "°C",
                        ObservableCollection<string>? availableGroups = null)
    {
        Id = id;
        _name = name;
        _originalName = name;
        _visible = visible;
        _group = group ?? "";
        _unit = unit;
        AvailableGroups = availableGroups ?? new();
    }

    /// <summary>
    /// Baut die Persistenz-Config. Der Name wird getrimmt; ist er leer, wird der ursprüngliche
    /// Hardware-/Anzeigename behalten, statt einen leeren String zu speichern (kein stiller Datenverlust).
    /// </summary>
    public SensorConfig ToConfig() => new()
    {
        SensorId = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? _originalName : Name.Trim(),
        Group = string.IsNullOrWhiteSpace(Group) ? null : Group.Trim(),
        Hidden = !Visible,
    };

    /// <summary>
    /// Setzt den editierbaren View-Zustand (Name/Sichtbarkeit/Gruppe) aus der Config zurück — für „Verwerfen".
    /// Null (nicht in der Config) → Defaults wie beim ersten Laden (Originalname, sichtbar, keine Gruppe).
    /// </summary>
    public void ApplyConfig(SensorConfig? config)
    {
        Name = config?.Name ?? _originalName;
        Visible = config?.Hidden != true;
        Group = config?.Group ?? "";
    }

    /// <summary>Schaltet die Dashboard-Sichtbarkeit um (Augen-Button im Geräte-Tab).</summary>
    [RelayCommand]
    private void ToggleVisible() => Visible = !Visible;

    /// <summary>Übernimmt den Live-Messwert (NaN → „n/a").</summary>
    public void SetLive(double value) =>
        LiveValue = double.IsNaN(value)
            ? "n/a"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {_unit}");

    public override string ToString() => Name;
}
