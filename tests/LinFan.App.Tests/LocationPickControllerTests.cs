// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.Core.Models;

namespace LinFan.App.Tests;

/// <summary>The position dialog's state: pick by zone, menu or direction; the footer; occupancy; the nudge.</summary>
public sealed class LocationPickControllerTests
{
    private static ManualControl Manual(List<byte>? sent = null) =>
        new("me", canControl: true, sendManual: (_, p) => { sent?.Add(p); return Task.CompletedTask; }, sendAuto: _ => Task.CompletedTask)
        { Throttle = TimeSpan.Zero };

    private static LocationPickController Pick(FanLocation current = FanLocation.CaseFrontIntake, ManualControl? manual = null) =>
        new("me", "CPU", current, CaseType.Tower, manual ?? Manual(),
            [new CaseFan("a", "Front unten", FanLocation.CaseFrontIntake, Manual()), new CaseFan("b", "Heck", FanLocation.CaseRearExhaust, Manual())]);

    [Fact]
    public void Opens_OnTheCurrentPosition_WithNothingToApply()
    {
        LocationPickController pick = Pick();

        Assert.Equal("Position von „CPU“", pick.Heading);
        Assert.False(pick.Moved);
        Assert.Equal("Keine Änderung", pick.FromText);
        Assert.True(pick.Zones.Single(z => z.Key == FanLocation.CaseFrontIntake).IsSelected);
    }

    [Fact]
    public void AZone_PicksItsUsualDirection_AndTheFooterNamesTheMove()
    {
        LocationPickController pick = Pick();

        pick.Zones.Single(z => z.Key == FanLocation.CaseTopExhaust).Pick!.Execute(null);

        Assert.Equal(FanLocation.CaseTopExhaust, pick.Selected);
        Assert.True(pick.Moved);
        Assert.Equal("Gehäuse vorn · Einlass", pick.FromText);
        Assert.Equal("→ Gehäuse oben · Auslass", pick.ToText);
        Assert.True(pick.Zones.Single(z => z.Key == FanLocation.CaseFrontIntake).WasHere);
    }

    [Fact]
    public void TheDirectionSwitch_Flips_AndAClickOnThePickKeepsIt()
    {
        LocationPickController pick = Pick();
        Assert.True(pick.CanFlip);
        Assert.Equal(0, pick.DirectionIndex);

        pick.DirectionIndex = 1;
        Assert.Equal(FanLocation.CaseFrontExhaust, pick.Selected);

        pick.Zones.Single(z => z.IsSelected).Pick!.Execute(null);
        Assert.Equal(FanLocation.CaseFrontExhaust, pick.Selected);

        pick.Zones.Single(z => z.Key == FanLocation.CpuCooler).Pick!.Execute(null);
        Assert.False(pick.CanFlip);
    }

    [Fact]
    public void TheMenu_MarksThePick_AndSaysWhoSitsAtEachPosition()
    {
        LocationPickController pick = Pick();

        MenuChoice front = pick.PositionMenu.Single(m => m.AutomationName == "Gehäuse vorn · Einlass");
        Assert.True(front.IsCurrent);
        Assert.Equal("Front unten", front.Detail);
        Assert.Equal("Heck", pick.PositionMenu.Single(m => m.AutomationName == "Gehäuse hinten · Auslass").Detail);

        pick.PositionMenu.Single(m => m.AutomationName == "Netzteil").Command!.Execute(null);
        Assert.Equal(FanLocation.Psu, pick.Selected);
    }

    [Fact]
    public async Task Nudge_SpinsTheFanUp_AndClosingLetsItGo()
    {
        var sent = new List<byte>();
        ManualControl manual = Manual(sent);
        LocationPickController pick = Pick(manual: manual);

        pick.NudgeCommand.Execute(null);
        await manual.PumpCompletion;
        Assert.True(manual.IsActive);
        Assert.Equal(100, manual.Percent);
        Assert.Equal("Anstoßen beenden", pick.NudgeLabel);
        Assert.Contains((byte)255, sent);

        pick.Close();
        Assert.False(manual.IsActive);
    }

    [Fact]
    public void Nudge_IsLocked_WhileManualControlMayNotStart()
    {
        ManualControl manual = Manual();
        manual.EngageBlocked = true;
        LocationPickController pick = Pick(manual: manual);

        Assert.False(pick.NudgeCommand.CanExecute(null));
        Assert.Contains("Gesperrt", pick.NudgeTooltip);
    }

    [Fact]
    public void TheCaseType_SwitchesSchemaAndMenu_AndCountsAsAChange()
    {
        LocationPickController pick = Pick();

        pick.CaseType = CaseType.Server;

        Assert.True(pick.Changed);
        Assert.False(pick.Moved);
        Assert.Equal(new LocationPick(FanLocation.CaseFrontIntake, CaseType.Server), pick.Result);
        Assert.Contains(pick.Zones, z => z.Key == FanLocation.FanWall);
        Assert.Contains(pick.PositionMenu, m => m.Header == "Lüfterwand");
        Assert.DoesNotContain(pick.PositionMenu, m => m.AutomationName == "Gehäuse oben · Auslass");
        Assert.Equal("Server / Rack", pick.CaseTypeLabel);
        Assert.True(pick.CaseTypeMenu.Single(m => m.Header == "Server / Rack").IsCurrent);
    }

    [Fact]
    public void InALaptop_TheDialogNamesThePositionsAsTheLaptopHasThem()
    {
        LocationPickController pick = Pick(FanLocation.CpuCooler);

        pick.CaseType = CaseType.Laptop;
        Assert.Equal("CPU-Lüfter", pick.SelectedLabel);

        pick.Zones.Single(z => z.Key == FanLocation.CaseSideIntake).Pick!.Execute(null);
        Assert.Equal(FanLocation.CaseSideExhaust, pick.Selected); // a laptop's side vent blows out
        Assert.Equal("→ Seite · Auslass", pick.ToText);

        pick.DirectionIndex = 0;
        pick.Zones.Single(z => z.IsSelected).Pick!.Execute(null); // a click on the pick keeps the switched direction
        Assert.Equal(FanLocation.CaseSideIntake, pick.Selected);

        pick.Zones.Single(z => z.Key == FanLocation.CaseTopExhaust).Pick!.Execute(null);
        Assert.Equal("Tastatur · Einlass", pick.SelectedLabel);
        Assert.Equal(new LocationPick(FanLocation.CaseTopIntake, CaseType.Laptop), pick.Result);
    }

    [Fact]
    public void OnAnOpenBench_ACaseFanSitsInNotInTheSchema_UntilAZoneIsPicked()
    {
        LocationPickController pick = Pick();

        pick.CaseType = CaseType.Open;

        CaseZone outside = pick.Tray.Single(z => z.IsOutside);
        Assert.True(outside.IsSelected);
        Assert.False(pick.CanFlip); // no direction to switch at a position the type lacks
        Assert.Contains(outside.Fans, f => f.IsSelf);
        Assert.Null(outside.Pick);

        pick.Zones.Single(z => z.Key == FanLocation.CpuCooler).Pick!.Execute(null);

        Assert.Equal(new LocationPick(FanLocation.CpuCooler, CaseType.Open), pick.Result);
        Assert.True(pick.Tray.Single(z => z.IsOutside).WasHere);
    }
}
