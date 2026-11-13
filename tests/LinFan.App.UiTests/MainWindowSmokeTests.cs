// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
        var controller = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
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
    /// Pending changes show as the floating bar (summary, undo/redo, discard all, apply) and as dots on the tabs
    /// they belong to. Both must follow the dirty state - otherwise nothing would announce unsaved changes at all.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_UnsavedBarAndTabDots_TrackTheChanges()
    {
        var controller = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var window = new MainWindow { DataContext = controller };
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);

        Border bar = Assert.Single(window.Find<Border>(), b => b.Name == "UnsavedBar");
        TextBlock detail = Assert.Single(window.Find<TextBlock>(), t => t.Name == "UnsavedBarDetail");
        Ellipse curvesDot = Assert.Single(window.Find<Ellipse>(), e => e.Name == "CurvesTabDirtyDot");
        Ellipse settingsDot = Assert.Single(window.Find<Ellipse>(), e => e.Name == "SettingsTabDirtyDot");

        // Freshly initialised -> not dirty -> nothing shown.
        Assert.False(controller.Editor.HasUnsavedChanges);
        Assert.False(bar.IsEffectivelyVisible);
        Assert.False(curvesDot.IsEffectivelyVisible);
        Assert.False(settingsDot.IsEffectivelyVisible);

        // Real edit path (MarkDirty) -> bar + the curves dot (the binding works, it does not just compile).
        controller.Editor.SelectedCurve!.AddPointRow(95, 90);
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsEffectivelyVisible);
        Assert.True(curvesDot.IsEffectivelyVisible);
        Assert.False(settingsDot.IsEffectivelyVisible);
        Assert.Equal("Geändert: 1 Kurve · noch nicht an den Hintergrunddienst übergeben.", detail.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(detail)); // announced as it changes

        // A dot is more than colour: it names itself for screen readers and explains itself on hover.
        Assert.Equal("ungespeicherte Änderungen", AutomationProperties.GetName(curvesDot));
        Assert.Equal("Ungespeicherte Änderungen in diesem Bereich", ToolTip.GetTip(curvesDot));

        // Fenster bewusst NICHT schließen: MainWindow.OnClosing würde bei „dirty" den Speichern-Dialog öffnen.
    }

    /// <summary>
    /// The header has to hold at the smallest window width: the profile button must neither overlap the wordmark nor
    /// run out of the window - not even with a very long profile name (the names are the user's own; the button cuts
    /// them off). The area tabs then take a row of their own below both.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_HeaderAtMinimumWidth_TabsTakeTheirOwnRow_AndTheProfileButtonClearsTheWordmark()
    {
        var snapshot = UiTestHelpers.SampleSnapshot();
        var controller = UiTestHelpers.NewController(new FakeLiveMonitor(snapshot with
        {
            Config = snapshot.Config with
            {
                Profiles = snapshot.Config.Profiles.Select(p => p with { Name = new string('M', 80) }).ToArray(),
            },
        }));
        var window = new MainWindow { DataContext = controller, Width = 760 }; // = the window's MinWidth
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);
        Dispatcher.UIThread.RunJobs();

        Button profile = Assert.Single(window.Find<Button>(), b => b.Name == "ProfileButton");
        Button gear = Assert.Single(window.Find<Button>(), b => b.Name == "SettingsButton");
        ListBox tabs = Assert.Single(window.Find<ListBox>(), l => l.Name == "NavTabs");
        // The row of mark + "LinFan" (the mark's parent), not just the mark itself.
        StackPanel wordmark = Assert.Single(window.Find<BrandMark>()).GetVisualAncestors().OfType<StackPanel>().First();

        Rect profileBounds = InWindow(profile, window);
        Rect wordmarkBounds = InWindow(wordmark, window);
        Rect tabBounds = InWindow(tabs, window);
        Assert.True(profileBounds.Width > 0 && wordmarkBounds.Width > 0); // layout really ran
        Assert.False(profileBounds.Intersects(wordmarkBounds), $"header overlaps itself: {profileBounds} / {wordmarkBounds}");
        Assert.True(InWindow(gear, window).Right <= window.Width, $"the gear runs out of the window: {InWindow(gear, window)}");
        Assert.Equal(1, Grid.GetRow(tabs));
        Assert.True(tabBounds.Top >= Math.Max(profileBounds.Bottom, wordmarkBounds.Bottom),
            $"the tabs do not sit below the header row: {tabBounds}");
        Assert.Equal(wordmarkBounds.Left, tabBounds.Left, 1); // the whole width, from the wordmark's edge
        Assert.Equal(44, tabs.ContainerFromIndex(0)!.Bounds.Height);
    }

    /// <summary>At the default width the area tabs sit between the wordmark and the profile button, 52 px tall.</summary>
    [AvaloniaFact]
    public void MainWindow_HeaderAtDefaultWidth_TabsSitBetweenWordmarkAndProfile()
    {
        var controller = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var window = new MainWindow { DataContext = controller, Width = 1000 };
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);
        Dispatcher.UIThread.RunJobs();

        ListBox tabs = Assert.Single(window.Find<ListBox>(), l => l.Name == "NavTabs");
        Rect tabBounds = InWindow(tabs, window);
        Rect wordmarkBounds = InWindow(Assert.Single(window.Find<BrandMark>()).GetVisualAncestors().OfType<StackPanel>().First(), window);
        Rect profileBounds = InWindow(Assert.Single(window.Find<Button>(), b => b.Name == "ProfileButton"), window);
        Assert.Equal(0, Grid.GetRow(tabs));
        Assert.True(tabBounds.Left >= wordmarkBounds.Right && tabBounds.Right <= profileBounds.Left,
            $"tabs {tabBounds}, wordmark {wordmarkBounds}, profile {profileBounds}");
        Assert.Equal(52, tabs.ContainerFromIndex(0)!.Bounds.Height);
    }

    /// <summary>Bounds of a control in window coordinates (Bounds itself is relative to the parent).</summary>
    private static Rect InWindow(Visual visual, Visual root) =>
        new(visual.TranslatePoint(default, root) ?? default, visual.Bounds.Size);
}
