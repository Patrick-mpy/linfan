// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;

namespace LinFan.App.UiTests;

/// <summary>
/// The hardware dialog (<see cref="HardwareChangeDialog"/>) with its real XAML - each card's buttons and checkbox reach
/// its controller, „Neu kalibrieren" is the fan row's command, a removal goes through the confirmation, the dialog closes
/// itself once nothing is left - and the way there from the overview's hints, through the real poll loop.
/// </summary>
public class HardwareChangeDialogSmokeTests
{
    private static readonly HardwareChangeReading GpuReplaced = new("Replaced:gpu", HardwareChangeKind.Replaced,
        "amdgpu", "pci:1002:73bf:1002:0e3a", "amdgpu", "pci:1002:744c:1da2:e471",
        new[] { "hwmon0/pwm1" }, new[] { "hwmon0/temp1" });

    private static readonly HardwareChangeReading NvmeMissing = new("Missing:nvme", HardwareChangeKind.Missing,
        "nvme", "nvme:Samsung SSD 980", null, null, Array.Empty<string>(), new[] { "nvme/temp1" });

    /// <summary>The sample (its sensor has an own name, „CPU") with a calibrated fan, reporting the changes.</summary>
    private static MonitorSnapshot Reporting(params HardwareChangeReading[] changes)
    {
        MonitorSnapshot sample = UiTestHelpers.SampleSnapshot();
        return sample with
        {
            Config = sample.Config with
            {
                Fans = new[]
                {
                    sample.Config.Fans[0] with { Calibration = new FanCalibration { StartPwm = 60, MinRpm = 400, MaxRpm = 1800 } },
                },
            },
            HardwareChanges = changes,
        };
    }

    private sealed record Sent(List<(string Key, bool Names)> Accepted, List<string> Forgotten, List<string> Calibrated);

    private static (HardwareChangeDialog Dialog, HardwareChangeController Controller, Sent Sent) ShowDialog(MonitorSnapshot snapshot)
    {
        var sent = new Sent(new(), new(), new());
        var fan = new FanRow("hwmon0/pwm1", "CPU Fan");
        fan.BindCommands(null, null, id => { sent.Calibrated.Add(id); return Task.CompletedTask; });
        fan.Update(snapshot.Fans[0]);
        var controller = new HardwareChangeController(
            (key, names) => { sent.Accepted.Add((key, names)); return Task.FromResult(true); },
            key => { sent.Forgotten.Add(key); return Task.FromResult(true); },
            _ => Task.FromResult(true),
            () => { },
            () => false,
            id => id == fan.FanId ? fan : null);
        controller.Apply(snapshot);
        var dialog = new HardwareChangeDialog(controller);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        return (dialog, controller, sent);
    }

    /// <summary>
    /// "Apply" is a primary button, whose text an App rule colours (Button.primary TextBlock) - and with it its tooltip's,
    /// until Theme/ToolTipText.axaml, included after Layout.axaml, gives that the tooltip's look back. A dialog has no
    /// window styles of its own, so the App's include is what keeps it readable here. In the light theme: the button's
    /// white text would be white on the light tooltip (in the dark one it happens to match).
    /// </summary>
    [AvaloniaFact]
    public void AcceptsToolTip_KeepsTheToolTipsLook()
    {
        var (dialog, _, _) = ShowDialog(Reporting(GpuReplaced));
        dialog.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();

        var (tip, text) = UiTestHelpers.OpenToolTip(Named<Button>(dialog, "AcceptButton"));

        Assert.StartsWith("Namen, Kalibrierung", text.Text);
        Assert.Equal(((ISolidColorBrush)tip.Foreground!).Color, ((ISolidColorBrush)text.Foreground!).Color);
        Assert.Equal(tip.FontWeight, text.FontWeight);
    }

    private static Border Card(HardwareChangeDialog dialog, string key) =>
        dialog.Find<Border>().Single(b => b.Classes.Contains("block") && b.DataContext is HardwareChangeCard c && c.Key == key);

    private static T Named<T>(Control root, string name) where T : Control => root.Find<T>().Single(c => c.Name == name);

    /// <summary>A real mouse click, so the test goes through hit-testing, enabled state and the command binding.</summary>
    private static void Click(TopLevel top, Control target)
    {
        Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top)
                       ?? throw new InvalidOperationException("Not rendered.");
        top.MouseDown(center, MouseButton.Left);
        top.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void EachCard_ShowsItsOwnActions_AndTheCheckboxOnlyWithOwnNames()
    {
        var (dialog, _, _) = ShowDialog(Reporting(GpuReplaced, NvmeMissing));
        Border gpu = Card(dialog, "Replaced:gpu");
        Border nvme = Card(dialog, "Missing:nvme");

        Assert.True(Named<Button>(gpu, "AcceptButton").IsEffectivelyVisible);
        Assert.True(Named<Button>(gpu, "SetUpAsNewButton").IsEffectivelyVisible);
        Assert.False(Named<Button>(gpu, "RemoveEntriesButton").IsEffectivelyVisible);
        Assert.True(Named<CheckBox>(gpu, "ResetSensorNamesCheck").IsEffectivelyVisible); // „CPU" is an own name
        Assert.Contains(gpu.Find<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == "pci:1002:744c:1da2:e471");
        Assert.Contains(gpu.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("Anlaufpunkt ungeprüft") == true);

        Assert.True(Named<Button>(nvme, "RemoveEntriesButton").IsEffectivelyVisible);
        Assert.True(Named<Button>(nvme, "KeepButton").IsEffectivelyVisible);
        Assert.False(Named<Button>(nvme, "AcceptButton").IsEffectivelyVisible);
        Assert.False(Named<CheckBox>(nvme, "ResetSensorNamesCheck").IsEffectivelyVisible);
        Assert.Equal("Nicht gefunden: nvme", AutomationProperties.GetName(nvme));
        dialog.Close();
    }

    [AvaloniaFact]
    public void TheCheckbox_ReachesTheCard_AndKeepSendsTheNames()
    {
        var (dialog, _, sent) = ShowDialog(Reporting(GpuReplaced));
        Border gpu = Card(dialog, "Replaced:gpu");

        Click(dialog, Named<CheckBox>(gpu, "ResetSensorNamesCheck"));
        Assert.True(((HardwareChangeCard)gpu.DataContext!).ResetSensorNames);
        Click(dialog, Named<Button>(gpu, "AcceptButton"));
        UiTestHelpers.PumpUntil(() => sent.Accepted.Count > 0);

        Assert.Equal(new[] { ("Replaced:gpu", true) }, sent.Accepted);
        Assert.False(Named<Button>(gpu, "AcceptButton").IsEffectivelyEnabled); // waiting for the daemon
        dialog.Close();
    }

    [AvaloniaFact]
    public void Recalibrate_IsTheFanRowsCommand()
    {
        var (dialog, _, sent) = ShowDialog(Reporting(GpuReplaced));
        Button recalibrate = Card(dialog, "Replaced:gpu").Find<Button>()
            .Single(b => b.IsEffectivelyVisible && b.DataContext is HardwareChangeEntry);

        Assert.True(recalibrate.IsEffectivelyEnabled);
        Assert.Equal("CPU Fan neu kalibrieren", AutomationProperties.GetName(recalibrate));
        Click(dialog, recalibrate);

        Assert.Equal(new[] { "hwmon0/pwm1" }, sent.Calibrated);
        dialog.Close();
    }

    [AvaloniaFact]
    public void RemoveEntries_GoesThroughTheConfirmation()
    {
        var (dialog, _, sent) = ShowDialog(Reporting(NvmeMissing));

        Click(dialog, Named<Button>(Card(dialog, "Missing:nvme"), "RemoveEntriesButton"));
        UiTestHelpers.PumpUntil(() => dialog.OwnedWindows.Count > 0);
        var confirm = Assert.IsType<ConfirmDialog>(Assert.Single(dialog.OwnedWindows));
        Assert.Empty(sent.Forgotten);

        Button yes = Named<Button>(confirm, "ConfirmButton");
        Assert.Equal("Entfernen", yes.Content);
        Assert.Contains("danger", yes.Classes);
        Click(confirm, yes);
        UiTestHelpers.PumpUntil(() => sent.Forgotten.Count > 0);

        Assert.Equal(new[] { "Missing:nvme" }, sent.Forgotten);
        dialog.Close();
    }

    [AvaloniaFact]
    public void Offline_LocksTheButtons_AndSaysWhy()
    {
        var (dialog, controller, _) = ShowDialog(Reporting(GpuReplaced));

        controller.Apply(MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable));
        Dispatcher.UIThread.RunJobs();

        Assert.True(Named<Border>(dialog, "OfflineNote").IsEffectivelyVisible);
        Assert.False(Named<Button>(dialog, "AcceptButton").IsEffectivelyEnabled);
        Assert.False(Named<Button>(dialog, "SetUpAsNewButton").IsEffectivelyEnabled);
        dialog.Close();
    }

    /// <summary>
    /// While a calibration runs, „Set up as new device" is locked and says why (the daemon would refuse to remove fans it
    /// may be calibrating), „Keep" stays - and the button is free again once the run is over.
    /// </summary>
    [AvaloniaFact]
    public void ACalibration_LocksSetUpAsNew_AndSaysWhy()
    {
        MonitorSnapshot reporting = Reporting(GpuReplaced);
        var (dialog, controller, _) = ShowDialog(reporting);
        Button setUpAsNew = Named<Button>(dialog, "SetUpAsNewButton");
        Assert.True(setUpAsNew.IsEffectivelyEnabled);

        controller.Apply(reporting with
        {
            Calibration = new CalibrationStatus("hwmon0/pwm1", CalibrationPhase.Measuring, 100, 900, Running: true, Done: false,
                StartPwm: null, FailReason: null),
        });
        Dispatcher.UIThread.RunJobs();

        Assert.False(setUpAsNew.IsEffectivelyEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(setUpAsNew));
        Assert.Equal("Gesperrt - gerade läuft eine Kalibrierung, Identifikation oder Tacho-Kopplung.", ToolTip.GetTip(setUpAsNew));
        Assert.True(Named<Button>(dialog, "AcceptButton").IsEffectivelyEnabled);

        controller.Apply(reporting);
        Dispatcher.UIThread.RunJobs();
        Assert.True(setUpAsNew.IsEffectivelyEnabled);
        dialog.Close();
    }

    [AvaloniaFact]
    public void TheDialog_ClosesItself_OnceNothingIsLeft()
    {
        var (dialog, controller, _) = ShowDialog(Reporting(GpuReplaced));
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;

        controller.Apply(Reporting());
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
    }

    // --- from the overview ----------------------------------------------------------------------------

    private static (MainController Ctrl, FakeLiveMonitor Monitor, MainWindow Window) ShowMain(MonitorSnapshot snapshot)
    {
        var monitor = new FakeLiveMonitor(snapshot);
        var ctrl = UiTestHelpers.NewController(monitor, pollInterval: TimeSpan.FromMilliseconds(10));
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.Connected);
        return (ctrl, monitor, window);
    }

    [AvaloniaFact]
    public void TheHint_OpensTheDialog_WhichFollowsTheDaemon()
    {
        var (ctrl, monitor, window) = ShowMain(Reporting(GpuReplaced));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Hints.Any(h => h.Key == "hardwareReplaced"));
            OverviewHint hint = ctrl.Hints.Single(h => h.Key == "hardwareReplaced");
            Assert.Equal(HintSeverity.Notice, hint.Severity);

            hint.Command!.Execute(null);
            UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count > 0);
            var dialog = Assert.IsType<HardwareChangeDialog>(Assert.Single(window.OwnedWindows));
            var changes = (HardwareChangeController)dialog.DataContext!;
            Assert.Equal("Replaced:gpu", Assert.Single(changes.Cards).Key);
            Button recalibrate = dialog.Find<Button>().Single(b => b.IsEffectivelyVisible && b.DataContext is HardwareChangeEntry);
            Assert.True(recalibrate.IsEffectivelyEnabled);

            // Offline the card stays, and „Neu kalibrieren" is locked like the fan table's button - it is the same row.
            monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
            UiTestHelpers.PumpUntil(() => changes.IsOffline && !recalibrate.IsEffectivelyEnabled);
            Assert.False(recalibrate.IsEffectivelyEnabled);
            Assert.Equal("Gesperrt - der Hintergrunddienst ist nicht erreichbar.", ToolTip.GetTip(recalibrate));
            Assert.Same(dialog, Assert.Single(window.OwnedWindows));

            monitor.Current = Reporting(); // back, and resolved
            UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count == 0);
            Assert.Empty(window.OwnedWindows);
            UiTestHelpers.PumpUntil(() => ctrl.Hints.All(h => h.Key != "hardwareReplaced"));
            Assert.DoesNotContain(ctrl.Hints, h => h.Key == "hardwareReplaced");
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void TheCalibrationHint_StartsTheFansCalibration_ButNotInADryRun()
    {
        MonitorSnapshot slower = UiTestHelpers.SampleSnapshot() with
        {
            Fans = new[] { new FanReading("hwmon0/pwm1", "CPU Fan", 600, 120, CanControl: true, CalibrationMismatch: CalibrationMismatch.Slower) },
        };
        var (ctrl, monitor, _) = ShowMain(slower);
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Hints.Any(h => h.Key == "calibration:hwmon0/pwm1"));
            OverviewHint hint = ctrl.Hints.Single(h => h.Key == "calibration:hwmon0/pwm1");
            Assert.Equal("Kalibrieren", hint.ActionLabel); // this fan has no calibration yet in the sample

            hint.Command!.Execute(null);
            Assert.Equal(new[] { "hwmon0/pwm1" }, monitor.CalibrateCalls);

            monitor.Current = slower with { DryRun = true };
            UiTestHelpers.PumpUntil(() => !hint.HasAction);
            Assert.Same(hint, ctrl.Hints.Single(h => h.Key == "calibration:hwmon0/pwm1"));
            Assert.False(hint.HasAction);
        }
        finally { ctrl.Dispose(); }
    }

    /// <summary>
    /// F5: after a removal the editor must not keep the old entries - its next save would bring them back. It rebuilds
    /// from the config that no longer reports the change, not from a config change that overtook the command.
    /// </summary>
    [AvaloniaFact]
    public void Removing_RebuildsTheEditor_FromTheConfigThatResolvedTheChange()
    {
        MonitorSnapshot reporting = Reporting(GpuReplaced);
        var monitor = new FakeLiveMonitor(reporting);
        var ctrl = UiTestHelpers.NewController(monitor, pollInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.Connected && ctrl.Hints.Count > 0);
            ctrl.Editor.Fans.Single().Name = "Ungespeichert";
            Assert.True(ctrl.Editor.HasUnsavedChanges);

            HardwareChangeController? opened = null;
            HardwareConfirmRequest? asked = null;
            ctrl.HardwareDialogRequested += (_, c) =>
            {
                opened = c;
                c.ConfirmationRequested += (_, r) => asked = r;
            };
            ctrl.ReviewHardwareChanges();
            Assert.NotNull(opened);
            opened!.Cards.Single().ForgetCommand.Execute(null);
            Assert.NotNull(asked);
            Assert.EndsWith("Deine ungespeicherten Änderungen werden dabei verworfen.", asked!.Message);
            opened.ConfirmedAsync(asked).GetAwaiter().GetResult(); // the fake sink completes synchronously
            Assert.Equal(new[] { "Replaced:gpu" }, monitor.ForgetHardwareCalls);

            // A calibration lands first: the config changes, the change is still reported - no rebuild yet.
            monitor.Current = reporting with
            {
                Config = reporting.Config with
                {
                    Fans = new[] { reporting.Config.Fans[0] with { Calibration = new FanCalibration { StartPwm = 70, MinRpm = 380, MaxRpm = 1800 } } },
                },
            };
            UiTestHelpers.PumpUntil(() => false, timeoutMs: 100);
            Assert.True(ctrl.Editor.HasUnsavedChanges);
            Assert.Equal("Ungespeichert", ctrl.Editor.Fans.Single().Name);

            // The daemon took the removal: no entry for the fan, no change reported.
            monitor.Current = reporting with
            {
                Config = reporting.Config with
                {
                    Fans = Array.Empty<FanConfig>(),
                    Profiles = reporting.Config.Profiles.Select(p => p with { Assignments = Array.Empty<ProfileAssignment>() }).ToList(),
                },
                HardwareChanges = null,
            };
            UiTestHelpers.PumpUntil(() => !ctrl.Editor.HasUnsavedChanges);

            Assert.False(ctrl.Editor.HasUnsavedChanges);
            Assert.NotEqual("Ungespeichert", ctrl.Editor.Fans.Single().Name);
            Assert.Null(opened.Cards.FirstOrDefault()); // and the dialog's card is gone with it
        }
        finally { ctrl.Dispose(); }
    }
}
