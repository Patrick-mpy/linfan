// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// The case types as the GUI names them (Settings › Airflow and „Position wählen"): name, what the schema shows, the
/// caption under it, and the „Gehäuse ▾" menu.
/// </summary>
public static class CaseTypeText
{
    // The menu's order (the enum only appends).
    private static readonly CaseType[] All = [CaseType.Tower, CaseType.Server, CaseType.Laptop, CaseType.Open];

    /// <summary>„Tower", „Server / Rack", „Laptop", „Offener Aufbau".</summary>
    public static string Name(CaseType caseType) => Localizer.Instance[Key(caseType, "")];

    /// <summary>What the menu notes next to the name („Standard-PC · Seitenansicht").</summary>
    public static string Detail(CaseType caseType) => Localizer.Instance[Key(caseType, "Detail")];

    /// <summary>The line under the schema („Seitenansicht · vorn links, hinten rechts").</summary>
    public static string Caption(CaseType caseType) => Localizer.Instance[Key(caseType, "Caption")];

    /// <summary>The „Gehäuse ▾" menu, <paramref name="current"/> marked.</summary>
    public static IReadOnlyList<MenuChoice> Menu(CaseType current, Action<CaseType> pick) => All
        .Select(t => MenuChoice.Value(Name(t), new RelayCommand(() => pick(t)), t == current, detail: Detail(t)))
        .ToList();

    /// <summary>Accessible name of the chip, which shows the value only as text inside („Gehäuse: Tower").</summary>
    public static string ChipName(CaseType caseType) => Localizer.Instance.Format("CaseType.ChipName", Name(caseType));

    private static string Key(CaseType caseType, string suffix) => caseType switch
    {
        CaseType.Server => "CaseType.Server",
        CaseType.Laptop => "CaseType.Laptop",
        CaseType.Open => "CaseType.Open",
        _ => "CaseType.Tower",
    } + suffix;
}
