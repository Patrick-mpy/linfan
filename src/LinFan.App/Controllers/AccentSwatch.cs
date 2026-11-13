// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// A colour dot of the accent choice: the colour <see cref="Choice"/> gives in the theme shown right now. The dots are
/// made once and updated in place (theme, own colour, language): replacing the list while the list box changes its own
/// selection would lose that selection - picking a dot would fall back to the first one.
/// </summary>
public sealed partial class AccentSwatch(AccentChoice choice) : ObservableObject
{
    public AccentChoice Choice { get; } = choice;

    /// <summary>Short label under the dot (translated).</summary>
    [ObservableProperty] private string _name = "";

    [ObservableProperty] private Color _color;

    /// <summary>What the dot stands for in this theme, with the colour value for presets.</summary>
    [ObservableProperty] private string _tooltip = "";

    /// <summary>The "own colour" dot - it shows a colour wheel until it is the chosen one.</summary>
    public bool IsCustom => Choice == AccentChoice.Custom;

    public override string ToString() => Name;
}
