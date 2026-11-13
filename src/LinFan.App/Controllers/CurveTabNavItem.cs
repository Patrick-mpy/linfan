// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;

namespace LinFan.App.Controllers;

/// <summary>
/// One row of the curves tab's side menu (<see cref="CurveEditorController.NavItems"/>): a profile, a curve of the
/// selected profile, or the "add curve" row. One list for all of them, so exactly one row is highlighted.
/// </summary>
public abstract class CurveTabNavItem : ObservableObject
{
    /// <summary>Whether the row is something to open. The "add curve" row is an action and never takes the selection.</summary>
    public virtual bool IsSelectable => true;
}
