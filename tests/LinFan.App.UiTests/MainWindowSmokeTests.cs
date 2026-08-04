// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Views;

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

    [AvaloniaFact]
    public void MainWindow_DirtyIndicator_VisibilityTracksHasUnsavedChanges()
    {
        var controller = new MainController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var window = new MainWindow { DataContext = controller };
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);

        TextBlock dirty = window.Find<TextBlock>()
            .Single(t => t.Text == "● Nicht gespeicherte Änderungen");

        // Frisch initialisiert → nicht „dirty" → ausgeblendet. (IsEffectivelyVisible, da das IsVisible-Binding
        // am umschließenden Toast-Border mit dem „Übernehmen"-Button sitzt, nicht am TextBlock selbst.)
        Assert.False(controller.Editor.HasUnsavedChanges);
        Assert.False(dirty.IsEffectivelyVisible);

        // Dirty setzen → Indikator sichtbar (das Binding greift, nicht nur kompiliert).
        controller.Editor.HasUnsavedChanges = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(dirty.IsEffectivelyVisible);

        // X on the toast -> hidden although still dirty; the next edit re-shows it.
        controller.Editor.HideUnsavedToastCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(controller.Editor.HasUnsavedChanges);
        Assert.False(dirty.IsEffectivelyVisible);

        controller.Editor.SelectedCurve!.AddPointRow(95, 90); // real edit path (MarkDirty) re-arms the toast
        Dispatcher.UIThread.RunJobs();
        Assert.True(dirty.IsEffectivelyVisible);

        // Fenster bewusst NICHT schließen: MainWindow.OnClosing würde bei „dirty" den Speichern-Dialog öffnen.
    }
}
