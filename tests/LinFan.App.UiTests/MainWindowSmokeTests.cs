// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Smoke-Tests des Hauptfensters: prüfen, dass die produktive XAML parst und ihre Bindings gegen einen
/// echten <see cref="MainController"/> zur Laufzeit auflösen (nicht nur kompilieren).
/// </summary>
public class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_WithRealController_InstantiatesAndBinds()
    {
        var controller = new MainController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var window = new MainWindow { DataContext = controller };
        window.Show();

        // Erster Poll-Tick muss die Bindings gegen echte Daten auflösen (Editor wird befüllt).
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);

        Assert.NotNull(window.Content);
        Assert.True(controller.Connected);
        // TabControl ist realisiert und hat die drei Tabs (Übersicht/Kurven/Einstellungen) → XAML hat geparst.
        TabControl tabs = Assert.Single(window.Find<TabControl>());
        Assert.Equal(3, tabs.Items.Count);
    }

    /// <summary>
    /// Pending changes have one indicator left: the apply/discard pair next to the profile picker. Both
    /// must follow the dirty state - otherwise nothing would announce unsaved changes at all.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_HeaderActions_VisibilityTracksHasUnsavedChanges()
    {
        var controller = new MainController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var window = new MainWindow { DataContext = controller };
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);

        Button save = Assert.Single(window.Find<Button>(), b => b.Name == "HeaderSaveButton");
        Button revert = Assert.Single(window.Find<Button>(), b => b.Name == "HeaderRevertButton");

        // Freshly initialised -> not dirty -> both hidden.
        Assert.False(controller.Editor.HasUnsavedChanges);
        Assert.False(save.IsEffectivelyVisible);
        Assert.False(revert.IsEffectivelyVisible);

        // Real edit path (MarkDirty) -> both visible (the binding works, it does not just compile).
        controller.Editor.SelectedCurve!.AddPointRow(95, 90);
        Dispatcher.UIThread.RunJobs();
        Assert.True(controller.Editor.HasUnsavedChanges);
        Assert.True(save.IsEffectivelyVisible);
        Assert.True(revert.IsEffectivelyVisible);

        // Fenster bewusst NICHT schließen: MainWindow.OnClosing würde bei „dirty" den Speichern-Dialog öffnen.
    }

    /// <summary>
    /// The header has to hold at the smallest window width: the profile group (picker + apply + discard)
    /// must neither overlap the wordmark nor run out of the window - not even with a very long profile
    /// name, since the names are the user's own. That is exactly where the earlier placement on the tab
    /// row failed: it needed a fixed reserve that no growing name can respect.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_HeaderAtMinimumWidth_ProfileGroupClearsTheWordmark()
    {
        var snapshot = UiTestHelpers.SampleSnapshot();
        var controller = new MainController(new FakeLiveMonitor(snapshot with
        {
            Config = snapshot.Config with
            {
                Profiles = snapshot.Config.Profiles.Select(p => p with { Name = new string('M', 80) }).ToArray(),
            },
        }));
        var window = new MainWindow { DataContext = controller, Width = 760 }; // = the window's MinWidth
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);

        controller.Editor.HasUnsavedChanges = true; // widest state: both buttons visible
        Dispatcher.UIThread.RunJobs();

        ComboBox picker = Assert.Single(window.Find<ComboBox>(), c => c.Name == "ActiveProfilePicker");
        StackPanel group = picker.GetVisualAncestors().OfType<StackPanel>().First();
        // The row of mark + "LinFan" (the mark's parent), not just the mark itself.
        StackPanel wordmark = Assert.Single(window.Find<BrandMark>()).GetVisualAncestors().OfType<StackPanel>().First();

        Rect groupBounds = InWindow(group, window);
        Rect wordmarkBounds = InWindow(wordmark, window);
        Assert.True(groupBounds.Width > 0 && wordmarkBounds.Width > 0); // layout really ran
        Assert.False(groupBounds.Intersects(wordmarkBounds), $"header overlaps itself: {groupBounds} / {wordmarkBounds}");
        Assert.True(groupBounds.Right <= window.Width, $"profile group runs out of the window: {groupBounds}");

        // Fenster bewusst NICHT schließen: MainWindow.OnClosing würde bei „dirty" den Speichern-Dialog öffnen.
    }

    /// <summary>Bounds of a control in window coordinates (Bounds itself is relative to the parent).</summary>
    private static Rect InWindow(Visual visual, Visual root) =>
        new(visual.TranslatePoint(default, root) ?? default, visual.Bounds.Size);
}
