// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LinFan.App.Controllers;

/// <summary>Eine benannte Lüfter-Gruppe fürs Dashboard (Gruppe bzw. Position) mit ihren Zeilen.</summary>
public partial class FanGroup : ObservableObject
{
    public const string Ungrouped = "Ungruppiert";

    public string Name { get; }
    public ObservableCollection<FanRow> Fans { get; } = new();

    public FanGroup(string name) => Name = name;
}
