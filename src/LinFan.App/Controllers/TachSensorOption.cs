// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// One entry of a fan row's speed-sensor choice: an available RPM sensor, or the automatic entry
/// (<see cref="Id"/> == <c>null</c>) that clears the own coupling - then the hardware's own pairing applies.
/// Reference-stable (one list shared by the rows), so it serves as a selection target.
/// </summary>
public sealed partial class TachSensorOption : ObservableObject
{
    private readonly string _name;

    public TachSensorOption(string? id, string name)
    {
        Id = id;
        _name = name;
    }

    /// <summary>The automatic entry; its name follows the UI language.</summary>
    public static TachSensorOption Automatic() => new(null, "");

    public string? Id { get; }

    public string Name => Id is null ? Localizer.Instance["FanAssignRow.TachAuto"] : _name;

    /// <summary>The language changed: the automatic entry's name reads differently.</summary>
    internal void NotifyLanguageChanged() => OnPropertyChanged(nameof(Name));

    /// <summary>The sensor's current reading for the choice list ("1180 RPM", "n/a"); empty for the automatic entry.</summary>
    [ObservableProperty] private string _liveRpm = "";

    /// <summary>Mirrors the sensor's reading (<c>null</c>/NaN = not readable).</summary>
    public void SetLive(double? rpm) => LiveRpm = RpmText.Of(rpm);
}
