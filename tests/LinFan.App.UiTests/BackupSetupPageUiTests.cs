// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;

namespace LinFan.App.UiTests;

/// <summary>
/// Settings › Backup &amp; setup in the real window: one row card per task (backup, setup assistant, factory reset), the
/// reset card framed in red with its red button, and every action locked while the service is unreachable.
/// </summary>
public class BackupSetupPageUiTests
{
    private const int TabSettings = 2;

    private static readonly string[] Actions = { "Exportieren …", "Importieren …", "Assistent starten", "Erst Backup …", "Zurücksetzen …" };

    private static (MainController Ctrl, MainWindow Window) Show(FakeLiveMonitor monitor)
    {
        var ctrl = UiTestHelpers.NewController(monitor);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Connected);
        Assert.Single(window.Find<TabControl>()).SelectedIndex = TabSettings;
        ctrl.Settings.SelectedSectionItem = ctrl.Settings.Sections.Single(s => s.Section == SettingsSection.Backup);
        Dispatcher.UIThread.RunJobs();
        return (ctrl, window);
    }

    private static List<Border> VisibleBlocks(MainWindow window) =>
        window.Find<Border>().Where(b => b.Classes.Contains("block") && b.IsEffectivelyVisible).ToList();

    private static Button Action(MainWindow window, string label) =>
        window.Find<Button>().Single(b => b.IsEffectivelyVisible && UiTestHelpers.ButtonLabel(b) == label);

    [AvaloniaFact]
    public void Page_HasOneRowPerTask_AndTheResetRowIsFramedInRed()
    {
        var (_, window) = Show(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));

        List<Border> blocks = VisibleBlocks(window);
        Assert.Equal(3, blocks.Count);
        Border reset = Assert.Single(blocks, b => b.Classes.Contains("danger"));
        Assert.Same(reset, blocks[^1]);

        foreach (string label in Actions)
            Assert.True(Action(window, label).IsEffectivelyEnabled, label);
        Assert.Contains("danger", Action(window, "Zurücksetzen …").Classes);
        Assert.DoesNotContain(window.Find<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Gesperrt, solange der Hintergrunddienst nicht erreichbar ist.");
    }

    [AvaloniaFact]
    public void Offline_LocksEveryAction_AndSaysWhy()
    {
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        var (ctrl, window) = Show(monitor);

        monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
        UiTestHelpers.PumpUntil(() => !ctrl.Connected);
        Dispatcher.UIThread.RunJobs();

        foreach (string label in Actions)
            Assert.False(Action(window, label).IsEffectivelyEnabled, label);
        Assert.Contains(window.Find<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Gesperrt, solange der Hintergrunddienst nicht erreichbar ist.");
    }
}
