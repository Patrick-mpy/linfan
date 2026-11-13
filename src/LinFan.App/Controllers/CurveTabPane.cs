// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Which editor the curves tab shows on its right-hand side. A selected profile always has a selected curve as
/// well, so what is being edited cannot be derived from the two selections - the last click decides, and the side
/// menu highlights its row (<see cref="CurveEditorController.SelectedNavItem"/>).
/// </summary>
public enum CurveTabPane
{
    /// <summary>Die ausgewählte Kurve (Standard).</summary>
    Curve,

    /// <summary>Das ausgewählte Profil (Name, Aktiv-Schalter, seine Kurven).</summary>
    Profile,
}
