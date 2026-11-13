// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Tests;

/// <summary>The position menu: its headings and order are presentation, which positions it offers is the Core's rule.</summary>
public sealed class PositionMenuTests
{
    [Theory]
    [InlineData(CaseType.Tower)]
    [InlineData(CaseType.Server)]
    [InlineData(CaseType.Open)]
    [InlineData(CaseType.Laptop)]
    public void OffersExactlyThePositionsTheCaseTypeHas(CaseType caseType)
    {
        var offered = new List<FanLocation>();
        List<MenuChoice> menu = PositionMenu.Build(null, offered.Add, caseType);

        foreach (MenuChoice entry in menu.Where(m => m.IsEnabled))
            entry.Command!.Execute(null);

        Assert.Equal(
            Enum.GetValues<FanLocation>().Where(l => AirflowTuneService.IsInCase(l, caseType)).Order(),
            offered.Order());
        Assert.Equal(offered.Count, offered.Distinct().Count()); // each once
    }

    [Fact]
    public void AServer_ListsTheUsualFlowFirst()
    {
        List<MenuChoice> menu = PositionMenu.Build(FanLocation.FanWall, _ => { }, CaseType.Server);

        Assert.Equal(new[] { "Server · von vorn nach hinten", "Vorn · Einlass", "Lüfterwand", "Hinten · Auslass", "Server · entgegen" },
            menu.SkipWhile(m => m.Header != "Server · von vorn nach hinten").Take(5).Select(m => m.Header));
        Assert.True(menu.Single(m => m.Header == "Lüfterwand").IsCurrent);
    }

    [Fact]
    public void ALaptop_NamesItsBlowersAndVents_UsualDirectionFirst()
    {
        List<MenuChoice> menu = PositionMenu.Build(FanLocation.CaseTopIntake, _ => { }, CaseType.Laptop);

        Assert.Equal(new[]
        {
            "Lüfter", "CPU-Lüfter", "GPU-Lüfter",
            "Laptop · Öffnungen", "Unterseite · Einlass", "Tastatur · Einlass", "Hinten · Auslass", "Seite · Auslass",
            "Laptop · umgekehrt", "Unterseite · Auslass", "Tastatur · Auslass", "Hinten · Einlass", "Seite · Einlass",
            "Sonstiges", "Sonstige", "Ohne Position",
        }, menu.Select(m => m.Header));
        MenuChoice keyboard = menu.Single(m => m.Header == "Tastatur · Einlass");
        Assert.True(keyboard.IsCurrent);
        Assert.Equal("Tastatur · Einlass", keyboard.AutomationName); // the laptop's own name, not „Gehäuse oben"
    }
}
