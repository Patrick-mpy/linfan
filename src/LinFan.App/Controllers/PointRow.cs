// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>Editierbarer Kurven-Stützpunkt (°C → %). <see cref="RemoveCommand"/> setzt der Besitzer.</summary>
public partial class PointRow : ObservableObject
{
    [ObservableProperty] private decimal _temperature;
    [ObservableProperty] private decimal _percent;

    /// <summary>Position in the point list, from 1 - kept current by the owning <see cref="CurveEditRow"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureName), nameof(PercentName), nameof(RemoveName))]
    private int _number;

    /// <summary>Entfernt diesen Punkt aus der Kurve; wird von <see cref="CurveEditRow"/> verdrahtet.</summary>
    public ICommand? RemoveCommand { get; set; }

    /// <summary>Accessible names of the row's controls - the visible labels sit in the list header only.</summary>
    public string TemperatureName => Localizer.Instance.Format("PointRow.TemperatureName", Number);

    public string PercentName => Localizer.Instance.Format("PointRow.PercentName", Number);

    public string RemoveName => Localizer.Instance.Format("PointRow.RemoveName", Number);

    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(TemperatureName));
        OnPropertyChanged(nameof(PercentName));
        OnPropertyChanged(nameof(RemoveName));
    }

    public PointRow(decimal temperature, decimal percent)
    {
        _temperature = temperature;
        _percent = percent;
    }
}
