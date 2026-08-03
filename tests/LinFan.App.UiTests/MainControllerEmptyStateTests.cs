// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Tier — Empty-State: unterscheidet „lädt/verbinde" (vor dem ersten Snapshot) von „keine Geräte"
/// (Snapshot da, aber leer). Geprüft werden die change-notifizierten Controller-Properties, die die
/// Dashboard-/Tab-Hinweise im XAML ein-/ausblenden. Läuft über den echten Poll-Loop + Dispatcher.
/// </summary>
public class MainControllerEmptyStateTests
{
    private static MainController Start(MonitorSnapshot initial) =>
        new(new FakeLiveMonitor(initial), pollInterval: TimeSpan.FromMilliseconds(10));

    /// <summary>Verbundener Snapshot ohne jedes Gerät (leere Live-Listen + leere Config).</summary>
    private static MonitorSnapshot ConnectedEmpty() => new(
        "Verbunden",
        Array.Empty<SensorReading>(),
        Array.Empty<FanReading>(),
        AppConfig.Empty,
        Connected: true);

    [AvaloniaFact]
    public void BeforeFirstSnapshot_NoDashboardPlaceholders_AndConnectingText()
    {
        // Der Controller hat noch nichts angewandt: keine Leer-Hinweise, Text = „Verbinde …".
        var ctrl = Start(MonitorSnapshot.Unavailable("Verbinde …"));
        try
        {
            Assert.False(ctrl.HasSnapshot);
            Assert.False(ctrl.ShowNoSensors);
            Assert.False(ctrl.ShowNoFans);
            Assert.Equal("Verbinde mit dem Hintergrunddienst …", ctrl.DeviceLoadingText);
            Assert.Equal("Verbinde mit dem Hintergrunddienst …", ctrl.CurveLoadingText);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void ConnectedButEmpty_ShowsNoDevicePlaceholders()
    {
        var ctrl = Start(ConnectedEmpty());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.HasSnapshot);

            Assert.True(ctrl.ShowNoSensors);    // geladen UND keine Sensoren
            Assert.True(ctrl.ShowNoFans);       // geladen UND keine Lüfter
            Assert.False(ctrl.HasSensors);
            Assert.False(ctrl.HasFans);
            Assert.True(ctrl.ShowNoDevices);    // Geräte-Tab: „Keine Geräte erkannt."
            Assert.False(ctrl.ShowDeviceLoading); // … und NICHT mehr der Lade-Hinweis
            Assert.False(ctrl.ShowCurveLoading);  // Kurven-Tab: ebenfalls KEIN ewiger Lade-Hinweis …
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Disconnected_StaysConnecting_NoEmptyDeviceHint()
    {
        // Snapshot da (HasSnapshot), aber nicht verbunden: Lade-Hinweis bleibt sichtbar (Text „Verbinde …"),
        // KEIN „keine Geräte" (das wäre irreführend ohne Daemon).
        var ctrl = Start(MonitorSnapshot.Unavailable("nicht erreichbar"));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.HasSnapshot);

            Assert.False(ctrl.Connected);
            Assert.False(ctrl.ShowNoDevices);
            Assert.True(ctrl.ShowDeviceLoading);
            Assert.True(ctrl.ShowCurveLoading); // ohne Daemon bleibt auch der Kurven-Tab im „Verbinde …"
            Assert.Equal("Verbinde mit dem Hintergrunddienst …", ctrl.DeviceLoadingText);
            Assert.Equal("Verbinde mit dem Hintergrunddienst …", ctrl.CurveLoadingText);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Connected_LoadingText_BecomesLoading()
    {
        var ctrl = Start(ConnectedEmpty());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.HasSnapshot);

            Assert.True(ctrl.Connected);
            Assert.Equal("Lade Geräte …", ctrl.DeviceLoadingText);
            Assert.Equal("Lade Kurven …", ctrl.CurveLoadingText);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void WithDevices_NoPlaceholders()
    {
        var ctrl = Start(UiTestHelpers.SampleSnapshot());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.HasSensors && ctrl.HasFans);

            Assert.False(ctrl.ShowNoSensors);
            Assert.False(ctrl.ShowNoFans);
            Assert.False(ctrl.ShowNoDevices);
            Assert.False(ctrl.ShowDeviceLoading); // Editor ist bereit → kein Lade-Hinweis mehr
            Assert.False(ctrl.ShowCurveLoading);  // … gilt analog für den Kurven-Tab
        }
        finally { ctrl.Dispose(); }
    }

    // --- Bindings im echten Fenster: toggeln die Sichtbarkeiten wirklich? ----------------------

    [AvaloniaFact]
    public void DashboardPlaceholder_VisibleWhenEmpty_HiddenWhenPopulated()
    {
        // Leerer (verbundener) Daemon: beide Dashboard-Platzhalter müssen im gerenderten Fenster sichtbar sein.
        var emptyCtrl = new MainController(new FakeLiveMonitor(ConnectedEmpty()));
        var emptyWindow = new MainWindow { DataContext = emptyCtrl };
        emptyWindow.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => emptyCtrl.HasSnapshot);

            TextBlock noSensors = SinglePlaceholder(emptyWindow, "Keine Temperatursensoren erkannt.");
            TextBlock noFans = SinglePlaceholder(emptyWindow, "Keine Lüfter erkannt.");
            Assert.True(noSensors.IsEffectivelyVisible);
            Assert.True(noFans.IsEffectivelyVisible);
        }
        finally { emptyCtrl.Dispose(); }

        // Voller Daemon: dieselben Platzhalter müssen unsichtbar (eingeklappt) sein.
        var fullCtrl = new MainController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var fullWindow = new MainWindow { DataContext = fullCtrl };
        fullWindow.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => fullCtrl.HasSensors && fullCtrl.HasFans);

            Assert.False(SinglePlaceholder(fullWindow, "Keine Temperatursensoren erkannt.").IsEffectivelyVisible);
            Assert.False(SinglePlaceholder(fullWindow, "Keine Lüfter erkannt.").IsEffectivelyVisible);
        }
        finally { fullCtrl.Dispose(); }
    }

    private static TextBlock SinglePlaceholder(MainWindow window, string text) =>
        window.Find<TextBlock>().Single(t => t.Text == text);
}
