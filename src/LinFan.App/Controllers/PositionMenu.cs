// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// The position menu (settings list rows, bulk bar, position dialog): only the positions the case type has
/// (<see cref="CaseSchema.IsInSchema"/>), under headings - coolers, then the case positions (a tower: intake and exhaust by
/// their short name „Vorn" …; a server: with the usual front-to-rear flow first; a laptop: its vents in their usual
/// direction first, named as the schema names them), then the rest. No submenus.
/// </summary>
public static class PositionMenu
{
    /// <param name="current">The value to mark; <c>null</c> builds plain actions (the bulk menu has no current value).</param>
    /// <param name="detail">A note per position on the right (the dialog: who sits there), or none.</param>
    public static List<MenuChoice> Build(FanLocation? current, Action<FanLocation> pick, CaseType caseType,
                                         Func<FanLocation, string>? detail = null)
    {
        Localizer l = Localizer.Instance;
        (FanLocation, string) Full(FanLocation loc) => (loc, FanLocationOption.For(loc).Display);
        (FanLocation, string) Short(FanLocation loc, string key) => (loc, l[key]);
        (FanLocation, string) Schema(FanLocation loc) => (loc, CaseSchema.Label(loc, caseType));

        var sections = new List<(string Heading, (FanLocation Loc, string Header)[] Items)>();
        if (caseType == CaseType.Laptop)
        {
            sections.Add((l["FanPositionMenu.LaptopFans"], [Schema(FanLocation.CpuCooler), Schema(FanLocation.GpuCooler)]));
            sections.Add((l["FanPositionMenu.LaptopVents"],
            [
                Schema(FanLocation.CaseBottomIntake), Schema(FanLocation.CaseTopIntake),
                Schema(FanLocation.CaseRearExhaust), Schema(FanLocation.CaseSideExhaust),
            ]));
            sections.Add((l["FanPositionMenu.LaptopReversed"],
            [
                Schema(FanLocation.CaseBottomExhaust), Schema(FanLocation.CaseTopExhaust),
                Schema(FanLocation.CaseRearIntake), Schema(FanLocation.CaseSideIntake),
            ]));
        }
        else
        {
            sections.Add((l["FanPositionMenu.Coolers"], [Full(FanLocation.CpuCooler), Full(FanLocation.GpuCooler), Full(FanLocation.Radiator)]));
        }
        if (caseType == CaseType.Tower)
        {
            sections.Add((l["FanPositionMenu.Intake"],
            [
                Short(FanLocation.CaseFrontIntake, "FanPositionMenu.Front"),
                Short(FanLocation.CaseBottomIntake, "FanPositionMenu.Bottom"),
                Short(FanLocation.CaseSideIntake, "FanPositionMenu.Side"),
                Short(FanLocation.CaseTopIntake, "FanPositionMenu.Top"),
                Short(FanLocation.CaseRearIntake, "FanPositionMenu.Rear"),
            ]));
            sections.Add((l["FanPositionMenu.Exhaust"],
            [
                Short(FanLocation.CaseRearExhaust, "FanPositionMenu.Rear"),
                Short(FanLocation.CaseTopExhaust, "FanPositionMenu.Top"),
                Short(FanLocation.CaseFrontExhaust, "FanPositionMenu.Front"),
                Short(FanLocation.CaseBottomExhaust, "FanPositionMenu.Bottom"),
                Short(FanLocation.CaseSideExhaust, "FanPositionMenu.Side"),
            ]));
        }
        else if (caseType == CaseType.Server)
        {
            sections.Add((l["FanPositionMenu.ServerFlow"],
                [Schema(FanLocation.CaseFrontIntake), Schema(FanLocation.FanWall), Schema(FanLocation.CaseRearExhaust)]));
            sections.Add((l["FanPositionMenu.ServerAgainst"], [Schema(FanLocation.CaseFrontExhaust), Schema(FanLocation.CaseRearIntake)]));
        }
        sections.Add((l["FanPositionMenu.Other"], [Full(FanLocation.Psu), Full(FanLocation.Other), (FanLocation.Unspecified, l["FanSections.NoPosition"])]));

        var menu = new List<MenuChoice>();
        foreach ((string heading, (FanLocation Loc, string Header)[] items) in sections)
        {
            var offered = items.Where(i => CaseSchema.IsInSchema(i.Loc, caseType)).ToList();
            if (offered.Count == 0)
                continue;
            menu.Add(MenuChoice.Heading(heading));
            menu.AddRange(offered.Select(i => Item(i.Loc, i.Header)));
        }
        return menu;

        // A screen reader hears the full name - the short ones repeat under both directions („Vorn" …).
        MenuChoice Item(FanLocation loc, string header)
        {
            var command = new RelayCommand(() => pick(loc));
            string name = CaseSchema.FullName(loc, caseType);
            return current is null
                ? MenuChoice.Action(header, command, name)
                : MenuChoice.Value(header, command, current == loc, name, detail?.Invoke(loc) ?? "");
        }
    }
}
