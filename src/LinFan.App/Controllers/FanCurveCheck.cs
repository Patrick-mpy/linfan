// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;

namespace LinFan.App.Controllers;

/// <summary>
/// Checkbox-Eintrag: gehört der Lüfter <see cref="Fan"/> zur aktuell bearbeiteten Kurve? Da ein Lüfter
/// nur EINER Kurve zugeordnet sein kann, setzt/entfernt <see cref="Assigned"/> die Zuordnung direkt auf
/// der <see cref="FanAssignRow"/> (kein eigener Zustand). Pro ausgewählter Kurve neu aufgebaut.
/// </summary>
public sealed partial class FanCurveCheck : ObservableObject
{
    private readonly CurveEditRow _curve;

    public FanAssignRow Fan { get; }

    public FanCurveCheck(FanAssignRow fan, CurveEditRow curve)
    {
        Fan = fan;
        _curve = curve;
    }

    public bool Assigned
    {
        get => ReferenceEquals(Fan.Selected, _curve);
        set
        {
            if (value)
                Fan.Selected = _curve;
            else if (ReferenceEquals(Fan.Selected, _curve))
                Fan.Selected = null;
            OnPropertyChanged();
        }
    }
}
