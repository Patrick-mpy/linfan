// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// One sensor group in the source selection of a curve: a collapsible section with a tri-state header that selects or
/// clears the whole group. Built by the controller for the selected curve; the checks belong to the curve row.
/// </summary>
public sealed partial class SensorCheckGroup : ObservableObject
{
    private readonly Action<string, bool> _rememberOpen;
    private readonly Action<Action> _runAsStep;

    /// <summary>The group's identity (its name, compared case-insensitively); empty for the ungrouped sensors.</summary>
    public string Key { get; }

    public string Name { get; }

    public IReadOnlyList<SensorCheck> Sensors { get; }

    /// <summary>Whether the section is open. A change is remembered per <see cref="Key"/> by the controller.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <param name="rememberOpen">Called with <see cref="Key"/> whenever <see cref="IsOpen"/> changes.</param>
    /// <param name="runAsStep">Runs the header toggle as an undo step of its own; without it the toggle just runs.</param>
    public SensorCheckGroup(string key, string name, IReadOnlyList<SensorCheck> sensors, bool isOpen,
                            Action<string, bool> rememberOpen, Action<Action>? runAsStep = null)
    {
        Key = key;
        Name = name;
        Sensors = sensors;
        _isOpen = isOpen;
        _rememberOpen = rememberOpen;
        _runAsStep = runAsStep ?? (change => change());
        foreach (SensorCheck check in sensors)
            check.PropertyChanged += OnCheckChanged;
    }

    private int SelectedCount => Sensors.Count(s => s.Selected);

    /// <summary>The header check: <c>true</c> = all selected, <c>false</c> = none, <c>null</c> = some.</summary>
    public bool? AllState => SelectedCount switch
    {
        0 => false,
        int n when n == Sensors.Count => true,
        _ => null,
    };

    /// <summary>„2 von 5 gewählt" / „keiner gewählt".</summary>
    public string Summary => SelectedCount is int n and > 0
        ? Localizer.Instance.Format("SensorCheckGroup.Selected", n, Sensors.Count)
        : Localizer.Instance["SensorCheckGroup.NoneSelected"];

    /// <summary>Accessible name of the header check (the summary alone does not say what the check does).</summary>
    public string ToggleAllName => Localizer.Instance.Format("SensorCheckGroup.ToggleAllName", Name, SelectedCount, Sensors.Count);

    /// <summary>
    /// The header check: clears the group when all of it is selected, otherwise selects all of it - and opens the
    /// section, so the result is visible. One undo step, never merged into an edit just before it.
    /// </summary>
    [RelayCommand]
    private void ToggleAll()
    {
        bool select = AllState != true;
        _runAsStep(() =>
        {
            foreach (SensorCheck check in Sensors)
                check.Selected = select;
        });
        IsOpen = true;
    }

    partial void OnIsOpenChanged(bool value) => _rememberOpen(Key, value);

    private void OnCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SensorCheck.Selected))
            return;
        OnPropertyChanged(nameof(AllState));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ToggleAllName));
    }

    /// <summary>Unsubscribes from the checks, which outlive the section (they belong to the curve row).</summary>
    public void Detach()
    {
        foreach (SensorCheck check in Sensors)
            check.PropertyChanged -= OnCheckChanged;
    }
}
