// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>A curve of the selected profile in the side menu; selecting it opens the curve editor.</summary>
public sealed class CurveNavItem : CurveTabNavItem
{
    public CurveEditRow Curve { get; }

    public CurveNavItem(CurveEditRow curve) => Curve = curve;

    /// <summary>The row's accessible name - what the row shows.</summary>
    public override string ToString() => Curve.Label;
}
