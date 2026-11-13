// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// One mounting position in Settings › Lüfter: the fans at that <see cref="FanLocation"/> under a header that selects
/// them, shows the airflow direction and counts them. Only positions in use get a section; „Ohne Position" comes last.
/// Dropping fans on a section moves them to its position (<see cref="CurveEditorController.SetFanLocations"/>).
/// </summary>
public sealed partial class FanLocationSection : ObservableObject
{
    public FanLocationSection(FanLocation location) => Location = location;

    public FanLocation Location { get; }

    /// <summary>The block's name - the same short name the overview groups the fans under.</summary>
    public string Name => Location == FanLocation.Unspecified
        ? Localizer.Instance["FanSections.NoPosition"]
        : FanLocationOption.GroupNameFor(Location);

    public bool IsUnspecified => Location == FanLocation.Unspecified;

    // The direction colours the header icon. Like the design, a position that is neither intake nor exhaust counts as
    // internal (coolers, PSU, other); only "no position" stays neutral.
    public bool IsIntake => FanLocationOption.DirectionOf(Location) == AirflowDirection.Intake;

    public bool IsExhaust => FanLocationOption.DirectionOf(Location) == AirflowDirection.Exhaust;

    public bool IsInternal => !IsUnspecified && !IsIntake && !IsExhaust;

    /// <summary>What the direction icon means (its tooltip).</summary>
    public string FlowTooltip => Localizer.Instance[
        IsIntake ? "FanSections.FlowIntake"
        : IsExhaust ? "FanSections.FlowExhaust"
        : IsInternal ? "FanSections.FlowInternal"
        : "FanSections.FlowNone"];

    /// <summary>The fans at this position as the list shows them (search and „hide here too" applied).</summary>
    public ObservableCollection<FanAssignRow> Rows { get; } = new();

    /// <summary>„1 Lüfter" / „3 Lüfter".</summary>
    public string CountText => Rows.Count == 1
        ? Localizer.Instance["FanSections.OneFan"]
        : Localizer.Instance.Format("FanSections.Fans", Rows.Count);

    /// <summary>The header check: <c>true</c> = all rows selected, <c>false</c> = none, <c>null</c> = some.</summary>
    [ObservableProperty] private bool? _allSelected = false;

    /// <summary>Accessible name of the header check („Front · Einlass: alle Lüfter auswählen").</summary>
    public string SelectAllName => Localizer.Instance.Format("FanSections.SelectAllName", Name);

    /// <summary>Selects all rows - or clears them when all are selected already. Selection only, no edit.</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        bool select = AllSelected != true;
        foreach (FanAssignRow row in Rows.ToList())
            row.IsSelected = select;
        OnPropertyChanged(nameof(AllSelected)); // the click toggled the box itself - keep it in line (see SensorGroupSection)
    }

    internal void Update(IReadOnlyList<FanAssignRow> rows)
    {
        FilteredListView.Sync(Rows, rows);
        OnPropertyChanged(nameof(CountText));
        RefreshSelection();
    }

    internal void RefreshSelection()
    {
        int selected = Rows.Count(r => r.IsSelected);
        AllSelected = selected == 0 ? false : selected == Rows.Count ? true : null;
    }

    /// <summary>The language changed: name, tooltip and count have to be read again.</summary>
    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FlowTooltip));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SelectAllName));
    }
}
