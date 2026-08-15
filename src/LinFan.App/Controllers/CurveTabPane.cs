// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Which editor the curves tab shows on its right-hand side. The side menu lists profiles and the curves of
/// the selected profile; both keep their selection, so what is being edited cannot be derived from the
/// selections alone - the last click decides.
/// </summary>
public enum CurveTabPane
{
    /// <summary>Die ausgewählte Kurve (Standard).</summary>
    Curve,

    /// <summary>Das ausgewählte Profil (Name, Aktiv-Schalter, seine Kurven).</summary>
    Profile,
}
