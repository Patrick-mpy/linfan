// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>Eine benannte Lüfter-Gruppe fürs Dashboard (Gruppe bzw. Position) mit ihren Zeilen.</summary>
public partial class FanGroup : ObservableObject
{
    /// <summary>
    /// Localized fallback label for fans without a position. Display-string-as-key, like the
    /// position names from <see cref="FanLocationOption.GroupNameFor"/>: after a language switch the
    /// group-signature gates see changed keys on the next rebuild and re-render the headers.
    /// </summary>
    public static string Ungrouped => Localizer.Instance["Group.Ungrouped"];

    public string Name { get; }
    public ObservableCollection<FanRow> Fans { get; } = new();

    public FanGroup(string name) => Name = name;
}
