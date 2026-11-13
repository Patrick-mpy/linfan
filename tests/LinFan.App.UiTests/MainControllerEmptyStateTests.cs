// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Tier - Empty-State: unterscheidet „lädt/verbinde" (vor dem ersten Snapshot) von „keine Geräte"
/// (Snapshot da, aber leer). Geprüft werden die change-notifizierten Controller-Properties, die die
/// Dashboard-/Tab-Hinweise im XAML ein-/ausblenden. Läuft über den echten Poll-Loop + Dispatcher.
/// </summary>
public class MainControllerEmptyStateTests
{
    private static MainController Start(MonitorSnapshot initial) =>
        UiTestHelpers.NewController(new FakeLiveMonitor(initial), pollInterval: TimeSpan.FromMilliseconds(10));

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
        var ctrl = Start(MonitorSnapshot.Unavailable("Verbinde …", ConnectionProblem.Connecting));
        try
        {
            Assert.True(ctrl.OverviewConnecting);
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
            UiTestHelpers.PumpUntil(() => UiTestHelpers.Applied(ctrl));

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
        // Snapshot da, aber nicht verbunden: Lade-Hinweis bleibt sichtbar (Text „Verbinde …"),
        // KEIN „keine Geräte" (das wäre irreführend ohne Daemon).
        var ctrl = Start(MonitorSnapshot.Unavailable("nicht erreichbar", ConnectionProblem.Unreachable));
        try
        {
            UiTestHelpers.PumpUntil(() => UiTestHelpers.Applied(ctrl));

            Assert.False(ctrl.Connected);
            Assert.False(ctrl.ShowNoSensors); // the overview never claims "nothing detected" without the daemon
            Assert.False(ctrl.ShowNoFans);
            Assert.True(ctrl.OverviewConnecting);
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
            UiTestHelpers.PumpUntil(() => UiTestHelpers.Applied(ctrl));

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
        var emptyCtrl = UiTestHelpers.NewController(new FakeLiveMonitor(ConnectedEmpty()));
        var emptyWindow = new MainWindow { DataContext = emptyCtrl };
        emptyWindow.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => UiTestHelpers.Applied(emptyCtrl));

            TextBlock noSensors = SinglePlaceholder(emptyWindow, NoSensorsText);
            TextBlock noFans = SinglePlaceholder(emptyWindow, NoFansText);
            Assert.True(noSensors.IsEffectivelyVisible);
            Assert.True(noFans.IsEffectivelyVisible);
        }
        finally { emptyCtrl.Dispose(); }

        // Voller Daemon: dieselben Platzhalter müssen unsichtbar (eingeklappt) sein.
        var fullCtrl = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var fullWindow = new MainWindow { DataContext = fullCtrl };
        fullWindow.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => fullCtrl.HasSensors && fullCtrl.HasFans);

            Assert.False(SinglePlaceholder(fullWindow, NoSensorsText).IsEffectivelyVisible);
            Assert.False(SinglePlaceholder(fullWindow, NoFansText).IsEffectivelyVisible);
        }
        finally { fullCtrl.Dispose(); }
    }

    [AvaloniaFact]
    public void OfflineAtStart_SaysConnecting_AndNeverNothingDetected()
    {
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable)),
                                      pollInterval: TimeSpan.FromMilliseconds(10));
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => UiTestHelpers.Applied(ctrl));
            UiTestHelpers.PumpUntil(() => false, timeoutMs: 60); // a few offline ticks

            Assert.True(window.Find<TextBlock>().Single(t => t.Name == "OverviewConnectingText").IsEffectivelyVisible);
            Assert.False(window.Find<ScrollViewer>().Single(v => v.Name == "OverviewScroller").IsEffectivelyVisible);
            Assert.DoesNotContain(window.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text is NoSensorsText or NoFansText);
        }
        finally { ctrl.Dispose(); }
    }

    private const string NoSensorsText = "Keine sichtbaren Temperatursensoren.";
    private const string NoFansText = "Keine sichtbaren Lüfter.";

    private static TextBlock SinglePlaceholder(MainWindow window, string text) =>
        window.Find<TextBlock>().Single(t => t.Text == text);
}
