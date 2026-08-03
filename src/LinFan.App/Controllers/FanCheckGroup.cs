// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;

namespace LinFan.App.Controllers;

/// <summary>Eine benannte Lüfter-Gruppe (Position/Gruppe) für die Kurven-Zuordnung — gebündelt wie im Dashboard,
/// aber mit Ankreuz-Zeilen statt Live-Karten.</summary>
public sealed class FanCheckGroup
{
    public string Name { get; }
    public ObservableCollection<FanCurveCheck> Fans { get; } = new();

    public FanCheckGroup(string name) => Name = name;
}
