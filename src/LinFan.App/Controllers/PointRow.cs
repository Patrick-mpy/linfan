// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LinFan.App.Controllers;

/// <summary>Editierbarer Kurven-Stützpunkt (°C → %). <see cref="RemoveCommand"/> setzt der Besitzer.</summary>
public partial class PointRow : ObservableObject
{
    [ObservableProperty] private decimal _temperature;
    [ObservableProperty] private decimal _percent;

    /// <summary>Entfernt diesen Punkt aus der Kurve; wird von <see cref="CurveEditRow"/> verdrahtet.</summary>
    public ICommand? RemoveCommand { get; set; }

    public PointRow(decimal temperature, decimal percent)
    {
        _temperature = temperature;
        _percent = percent;
    }
}
