// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Headless.XUnit;
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

    [AvaloniaFact]
    public void ControllableFan_ShowsManualPanel_SendsOnEngage_RevertsOnClose()
    {
        var sent = new List<byte>();
        var auto = new List<string>();
        ManualControl manual = Manual(sent, auto);
        var dialog = new FanLocationDialog("CPU Fan", FanLocation.CpuCooler, manual);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(dialog.ShowManual);

        manual.IsActive = true; // engagieren → ein SetManualPwm (über die gedrosselte Pumpe → in Echtzeit abwarten)
        UiTestHelpers.PumpUntil(() => sent.Count > 0);
        Assert.NotEmpty(sent);

        dialog.Close(); // jeder Schließweg → Revert → SetFanAuto
        Dispatcher.UIThread.RunJobs();
        Assert.False(manual.IsActive);
        Assert.Contains("pwm1", auto);
    }

    [AvaloniaFact]
    public void ReadOnlyFan_HidesManualPanel()
    {
        var dialog = new FanLocationDialog("GPU", FanLocation.GpuCooler, Manual(new(), new(), canControl: false));
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(dialog.ShowManual);
    }

    [AvaloniaFact]
    public void NoManualControl_HidesManualPanel()
    {
        var dialog = new FanLocationDialog("Fan", FanLocation.Unspecified);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(dialog.ShowManual);
    }
}
