// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;

namespace LinFan.App.UiTests;

/// <summary>
/// Schließ-Semantik des Hauptfensters bei aktivem „In den Tray minimieren": ein normales Schließen legt das
/// Fenster nur ins Tray, eine Shutdown-Anfrage des Systems (Abmelden/Herunterfahren, oder ein Setup, das die
/// App über den Restart Manager schließt) muss den Prozess dagegen wirklich gehen lassen - sonst antwortet
/// die App dem System mit „nein" und hält die Dateien weiter belegt, die das Setup ersetzen will.
/// </summary>
public class MainWindowCloseTests
{
    /// <summary>Fenster mit Tray-Icon und aktivem „In den Tray minimieren"; beide Stores auf einem temporären
    /// Pfad, damit der Test die echten Einstellungen des Nutzers nicht anfasst.</summary>
    private static MainWindow ShowWithTray()
    {
        string uiSettings = Path.Combine(Path.GetTempPath(), $"linfan-uitest-{Guid.NewGuid():N}.json");
        var ctrl = new MainController(
            new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()),
            settings: new SettingsController(new UiSettingsStore(uiSettings)));
        ctrl.Settings.MinimizeToTray = true;

        var window = new MainWindow(new UiSettingsStore(uiSettings)) { DataContext = ctrl, TrayAvailable = true };
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void Close_WithMinimizeToTray_HidesInsteadOfClosing()
    {
        MainWindow window = ShowWithTray();
        bool closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(closed);           // das Fenster - und damit der Prozess - lebt weiter …
        Assert.False(window.IsVisible); // … liegt aber im Tray
    }

    [AvaloniaFact]
    public void Close_AfterShutdownRequest_ReallyCloses()
    {
        MainWindow window = ShowWithTray();
        bool closed = false;
        window.Closed += (_, _) => closed = true;

        // Genau das, was App beim ShutdownRequested des Lifetimes tut.
        window.PrepareForShutdown();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
    }
}
