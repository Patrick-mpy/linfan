// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;

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
            Refresh();
        }
    }

    /// <summary>The fan follows another curve, so ticking it here moves it (one curve per fan).</summary>
    public bool ShowMoveHint => !Assigned && Fan.Selected is not null;

    /// <summary>„Steckt in „CPU“ – Häkchen verschiebt ihn hierher"; empty unless <see cref="ShowMoveHint"/>.</summary>
    public string MoveHint => ShowMoveHint
        ? Localizer.Instance.Format("FanCurveCheck.MoveHint", Fan.Selected!.Name)
        : "";

    /// <summary>Re-reads the assignment - it lives on the fan row and also changes through other curves.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Assigned));
        OnPropertyChanged(nameof(ShowMoveHint));
        OnPropertyChanged(nameof(MoveHint));
    }
}
