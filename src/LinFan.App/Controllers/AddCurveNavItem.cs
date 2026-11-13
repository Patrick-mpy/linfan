// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// The "add curve" row below the selected profile's curves. It opens the template menu (<see cref="Templates"/>); the
/// entries run <see cref="AddFromTemplateCommand"/> with their <see cref="CurveTemplate"/>. Everything the row and its
/// menu show is carried here, because the menu opens in a popup of its own with this row as its data context.
/// </summary>
public sealed partial class AddCurveNavItem : CurveTabNavItem
{
    public AddCurveNavItem(ICommand addFromTemplate) => AddFromTemplateCommand = addFromTemplate;

    public ICommand AddFromTemplateCommand { get; }

    public IReadOnlyList<CurveTemplateOption> Templates => CurveTemplates.Menu;

    /// <summary>Room for another curve - kept current by the controller.</summary>
    [ObservableProperty] private bool _canAdd;

    /// <summary>The button's tooltip; at the curve limit it says why the button is off.</summary>
    [ObservableProperty] private string _tooltip = "";

    public override bool IsSelectable => false;

    public override string ToString() => Localizer.Instance["MainWindow.AddCurve"];
}
