// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// One entry of a fan's „assign by hand" list: a speed sensor and the fan that already holds it (empty = free). A
/// sensor another fan is coupled to explicitly cannot be picked (<see cref="IsEnabled"/>) - one sensor, one fan.
/// A sensor the hardware pairs with another fan (<see cref="AutoAt"/>) can: an own coupling outranks that pairing, and
/// the other fan is left without a speed sensor. The entries stay the same objects while the list is open; only
/// <see cref="TakenBy"/> and <see cref="AutoAt"/> follow the other rows, so the list keeps its selection instead of
/// being rebuilt under it.
/// </summary>
public sealed partial class TachChoice : ObservableObject
{
    public TachChoice(TachSensorOption option, string takenBy, string autoAt)
    {
        Option = option;
        _takenBy = takenBy;
        _autoAt = autoAt;
    }

    public TachSensorOption Option { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaken), nameof(IsEnabled), nameof(TakenText), nameof(HasAutoAt))]
    private string _takenBy;

    /// <summary>The fan whose speed the hardware reads from this sensor while it has no coupling of its own (empty = none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoAt), nameof(AutoAtText))]
    private string _autoAt;

    public bool IsTaken => TakenBy.Length > 0;

    public bool IsEnabled => !IsTaken;

    /// <summary>„belegt: Front oben" - empty while free.</summary>
    public string TakenText => IsTaken ? Localizer.Instance.Format("FanAssignRow.TachTakenBy", TakenBy) : "";

    /// <summary>Shown only while no fan holds the sensor explicitly - that one says more.</summary>
    public bool HasAutoAt => !IsTaken && AutoAt.Length > 0;

    /// <summary>„automatisch bei Front oben" - empty without such a fan.</summary>
    public string AutoAtText => AutoAt.Length > 0 ? Localizer.Instance.Format("FanAssignRow.TachAutoAt", AutoAt) : "";

    /// <summary>The language changed: <see cref="TakenText"/> and <see cref="AutoAtText"/> read differently.</summary>
    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(TakenText));
        OnPropertyChanged(nameof(AutoAtText));
    }
}
