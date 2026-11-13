// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Auswahloption für den Quell-Sensor einer Kurve (im ComboBox) und zugleich editierbarer
/// Anzeigename (Umbenennen). Identität über <see cref="Id"/>; der Name ist beobachtbar, sodass
/// Dropdown und Rename-Liste dieselbe Instanz teilen und sich live aktualisieren.
/// </summary>
public partial class SensorOption : ObservableObject
{
    private readonly string _unit;
    private readonly string _originalName; // Hardware-/Anzeigename beim Laden - Fallback gegen Datenverlust
    // The hardware label, also while an own name is set. Null: an older daemon did not send it - then a name is saved as
    // it is shown, as before, since an own name could not be told from the label.
    private readonly string? _hardwareLabel;

    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectName), nameof(CanResetName))]
    [NotifyCanExecuteChangedFor(nameof(ResetNameCommand))]
    private string _name;

    /// <summary>Placeholder of the name field: the hardware label - what the sensor is called without an own name.</summary>
    public string NamePlaceholder => _hardwareLabel ?? Localizer.Instance["MainWindow.NameRequired"];

    /// <summary>The limit the daemon checks a name against (<see cref="ConfigLimits.MaxTextLength"/>).</summary>
    public int MaxNameLength => ConfigLimits.MaxTextLength;

    /// <summary>Whether the name is not the hardware label, so „Hardware-Namen verwenden" has something to do.</summary>
    public bool CanResetName => _hardwareLabel is { } label && Name.Trim() != label;

    /// <summary>Tooltip of the reset button: which name it brings back.</summary>
    public string ResetNameTooltip => Localizer.Instance.Format("SensorOption.ResetNameTooltip", _hardwareLabel ?? "");

    [ObservableProperty] private bool _visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupLabel), nameof(GroupChipName))]
    private string _group;

    /// <summary>Formatierte Live-Temperatur für den Geräte-Tab (reine Anzeige, fließt nicht in die Config).</summary>
    [ObservableProperty] private string _liveValue = "-";

    /// <summary>
    /// Ticked in the settings list for a bulk action. View state only: never part of <see cref="ToConfig"/>, the
    /// history or the unsaved-changes check.
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>The row is part of a running drag (shown dimmed). View state only, like <see cref="IsSelected"/>.</summary>
    [ObservableProperty] private bool _isDragged;

    /// <summary>The entries of the row's „Gruppe ▾" menu - built by the controller, which owns the group list.</summary>
    [ObservableProperty] private IReadOnlyList<MenuChoice> _groupMenu = Array.Empty<MenuChoice>();

    /// <summary>The group as the row's chip shows it: its name, or „Ungruppiert".</summary>
    public string GroupLabel => string.IsNullOrWhiteSpace(Group) ? GroupLabels.Ungrouped : Group.Trim();

    /// <summary>Accessible name of the group chip, which shows the value only as text inside („Gruppe: CPU").</summary>
    public string GroupChipName => Localizer.Instance.Format("SensorGroups.GroupChipName", GroupLabel);

    /// <summary>Accessible name of the row's selection check („Tctl auswählen").</summary>
    public string SelectName => Localizer.Instance.Format("SensorGroups.SelectSensor", Name);

    /// <param name="name">The name to show: the user's own, or else the hardware label.</param>
    /// <param name="hardwareLabel">The hardware label; <c>null</c> if the daemon did not send it.</param>
    public SensorOption(string id, string name, bool visible = true, string? group = null, string unit = "°C",
                        string? hardwareLabel = null)
    {
        Id = id;
        _name = name;
        _originalName = name;
        _hardwareLabel = string.IsNullOrWhiteSpace(hardwareLabel) ? null : hardwareLabel.Trim();
        _visible = visible;
        _group = group ?? "";
        _unit = unit;
    }

    /// <summary>
    /// The config entry to save. The name is the user's own: the hardware label - or an empty field - is saved as no name,
    /// so the label follows the hardware if it changes behind the same id (a replaced graphics card kept the old model's
    /// name otherwise). Without a known label an empty field keeps the name it was loaded with.
    /// </summary>
    public SensorConfig ToConfig() => new()
    {
        SensorId = Id,
        Name = OwnName(),
        Group = string.IsNullOrWhiteSpace(Group) ? null : Group.Trim(),
        Hidden = !Visible,
    };

    private string OwnName()
    {
        string trimmed = Name.Trim();
        if (_hardwareLabel is null)
            return trimmed.Length == 0 ? _originalName : trimmed;
        return trimmed == _hardwareLabel ? "" : trimmed;
    }

    /// <summary>Back to the hardware label as the name (saved as no own name).</summary>
    [RelayCommand(CanExecute = nameof(CanResetName))]
    private void ResetName() => Name = _hardwareLabel!;

    /// <summary>
    /// An empty field shows the name it stands for before the editor saves: the hardware label (or the name it was loaded
    /// with). The config is the same either way; the lists showing the sensor would stay blank otherwise.
    /// </summary>
    internal void FillEmptyName()
    {
        if (string.IsNullOrWhiteSpace(Name))
            Name = _hardwareLabel ?? _originalName;
    }

    /// <summary>
    /// Setzt den editierbaren View-Zustand (Name/Sichtbarkeit/Gruppe) aus der Config zurück - für „Verwerfen".
    /// Null (nicht in der Config) → Defaults wie beim ersten Laden (Originalname, sichtbar, keine Gruppe).
    /// </summary>
    public void ApplyConfig(SensorConfig? config)
    {
        Name = string.IsNullOrWhiteSpace(config?.Name) ? _hardwareLabel ?? _originalName : config.Name;
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

    /// <summary>The language changed: „Ungruppiert" and the accessible names have to be read again.</summary>
    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(NamePlaceholder));
        OnPropertyChanged(nameof(ResetNameTooltip));
        OnPropertyChanged(nameof(GroupLabel));
        OnPropertyChanged(nameof(GroupChipName));
        OnPropertyChanged(nameof(SelectName));
    }

    public override string ToString() => Name;

    /// <summary>
    /// The hardware label per sensor id, for the airflow analysis (<c>AirflowTuneService</c>): it names a sensor without an
    /// own name by its label - the config holds the own names only. Sensors whose label the daemon did not send are left
    /// out; their config keeps the name as shown.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels(IEnumerable<SensorOption> sensors)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SensorOption s in sensors)
            if (s._hardwareLabel is { } label)
                labels.TryAdd(s.Id, label);
        return labels;
    }
}
