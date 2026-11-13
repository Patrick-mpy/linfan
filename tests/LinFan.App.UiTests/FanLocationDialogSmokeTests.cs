// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Smoke-Tests des Positions-Modals (<see cref="FanLocationDialog"/>) mit temporärer Manuell-Steuerung:
/// Der Manuell-Block erscheint nur für steuerbare Lüfter, sein Slider sendet, und das Schließen (jeder Weg)
/// stellt den Lüfter auf Auto/Kurve zurück. Lädt das echte XAML headless → prüft die Bindungen zur Laufzeit.
/// </summary>
public class FanLocationDialogSmokeTests
{
    private static ManualControl Manual(List<byte> sent, List<string> auto, bool canControl = true) =>
        new("pwm1", canControl,
            sendManual: (_, p) => { sent.Add(p); return Task.CompletedTask; },
            sendAuto: id => { auto.Add(id); return Task.CompletedTask; });

    private static FanLocationDialog Show(ManualControl manual, FanLocation current = FanLocation.CpuCooler)
    {
        var pick = new LocationPickController("pwm1", "CPU Fan", current, CaseType.Tower, manual,
            [new CaseFan("pwm2", "Front", FanLocation.CaseFrontIntake, new ManualControl("pwm2", true, null, null))]);
        var dialog = new FanLocationDialog(pick);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.Find<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void ApplyIsLocked_UntilAZoneIsPicked_AndThenReturnsThePick()
    {
        var dialog = Show(Manual(new(), new()));
        Button apply = Named<Button>(dialog, "ApplyButton");
        Assert.False(apply.IsEffectivelyEnabled);

        RadioButton top = dialog.Find<RadioButton>().Single(r => r.DataContext is CaseZone { Key: FanLocation.CaseTopExhaust });
        top.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(apply.IsEffectivelyEnabled);
        Assert.Equal(FanLocation.CaseTopExhaust, ((LocationPickController)dialog.DataContext!).Selected);
        Assert.Contains(dialog.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Front"); // the other fan, for orientation
    }

    [AvaloniaFact]
    public void TheCaseChip_ChangesTheSchema_AndAloneUnlocksApply()
    {
        var dialog = Show(Manual(new(), new()));
        var pick = (LocationPickController)dialog.DataContext!;
        Button apply = Named<Button>(dialog, "ApplyButton");
        Button chip = Named<Button>(dialog, "CaseTypeChip");
        Assert.Contains(chip.Find<TextBlock>(), t => t.Text == "Tower");

        pick.CaseTypeMenu.Single(m => m.Header == "Server / Rack").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(chip.Find<TextBlock>(), t => t.Text == "Server / Rack");
        Assert.Contains(dialog.Find<RadioButton>(), r => r.DataContext is CaseZone { Key: FanLocation.FanWall });
        Assert.True(apply.IsEffectivelyEnabled); // the type alone is a change
        Assert.Contains(dialog.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Draufsicht · Luft strömt von vorn (links) nach hinten (rechts)");
    }

    [AvaloniaFact]
    public void APickOutsideTheSchema_StillLeavesTheZonesReachable_AndAnArrowPicksTheFirst()
    {
        var pick = new LocationPickController("pwm1", "Deckel", FanLocation.CaseTopExhaust, CaseType.Server,
            new ManualControl("pwm1", true, null, null), []);
        var dialog = new FanLocationDialog(pick);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        RadioButton stop = Assert.Single(dialog.Find<RadioButton>(), r => r.IsTabStop);
        Assert.Equal(FanLocation.CaseFrontIntake, ((CaseZone)stop.DataContext!).Key);
        stop.Focus();
        dialog.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, "");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(FanLocation.CaseFrontIntake, pick.Selected); // the first zone, not the second
    }

    [AvaloniaFact]
    public void ControllableFan_NudgesFromTheDialog_AndRevertsOnClose()
    {
        var sent = new List<byte>();
        var auto = new List<string>();
        ManualControl manual = Manual(sent, auto);
        var dialog = Show(manual);

        Button nudge = Named<Button>(dialog, "NudgeButton");
        Assert.True(nudge.IsEffectivelyVisible);
        nudge.Command!.Execute(null);
        UiTestHelpers.PumpUntil(() => sent.Count > 0);
        Assert.Contains((byte)255, sent);

        dialog.Close(); // every way out → back to auto/curve
        Dispatcher.UIThread.RunJobs();
        Assert.False(manual.IsActive);
        Assert.Contains("pwm1", auto);
    }

    [AvaloniaFact]
    public void ReadOnlyFan_HasNoNudge()
    {
        var dialog = Show(Manual(new(), new(), canControl: false));

        Assert.False(Named<Button>(dialog, "NudgeButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void DirectionSwitch_OnlyForCaseEdges()
    {
        var dialog = Show(Manual(new(), new()), FanLocation.CaseRearExhaust);
        ListBox direction = Named<ListBox>(dialog, "DirectionSwitch");
        Assert.True(direction.IsEffectivelyVisible);

        direction.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FanLocation.CaseRearIntake, ((LocationPickController)dialog.DataContext!).Selected);
    }
}
