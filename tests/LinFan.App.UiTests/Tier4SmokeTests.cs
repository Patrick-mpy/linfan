// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;

namespace LinFan.App.UiTests;

/// <summary>
/// Tier 4 — Live-Update über den echten Poll-Loop: ein austauschbarer Fake-Snapshot wird über mehrere
/// Ticks fortgeschrieben (injiziertes Kurz-Intervall), und es wird geprüft, dass die wechselnden Messwerte
/// den vollen Pfad <c>Read → Dispatcher → MainController.Apply → Dashboard/Editor</c> durchlaufen.
/// Ergänzt die Unit-Tests von <c>UpdateLive</c> um die reale Loop-/Marshaling-Verdrahtung.
/// </summary>
public class Tier4SmokeTests
{
    private static (MainController ctrl, FakeLiveMonitor fake) Start(LinFan.App.Services.MonitorSnapshot initial)
    {
        var fake = new FakeLiveMonitor(initial);
        // Kurzes (injiziertes) Intervall → die Schleife iteriert schnell mehrfach.
        var ctrl = new MainController(fake, pollInterval: TimeSpan.FromMilliseconds(10));
        return (ctrl, fake);
    }

    [AvaloniaFact]
    public void Dashboard_Temperature_FollowsLiveSnapshots()
    {
        var (ctrl, fake) = Start(UiTestHelpers.LiveSnapshot(45, 1200));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Temperatures.Any(t => t.Display == "45.0 °C"));
            SensorRow cpu = ctrl.Temperatures.Single();
            Assert.Equal("45.0 °C", cpu.Display);
            int historyBefore = cpu.History.Count;

            fake.Current = UiTestHelpers.LiveSnapshot(55, 1200);
            UiTestHelpers.PumpUntil(() => cpu.Display == "55.0 °C");

            Assert.Equal("55.0 °C", cpu.Display);
            // Verlauf akkumuliert (kein exakter Count — der 10-ms-Loop läuft frei weiter).
            Assert.True(cpu.History.Count > historyBefore);
            Assert.Contains(55.0, cpu.History);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Dashboard_Fan_FollowsLiveSnapshots()
    {
        var (ctrl, fake) = Start(UiTestHelpers.LiveSnapshot(45, 1200, pwm: 120));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Fans.Any(f => f.Rpm == "1200 RPM"));
            FanRow fan = ctrl.Fans.Single();
            Assert.Equal("1200 RPM", fan.Rpm);
            int historyBefore = fan.RpmHistory.Count;

            fake.Current = UiTestHelpers.LiveSnapshot(45, 1500, pwm: 200);
            UiTestHelpers.PumpUntil(() => fan.Rpm == "1500 RPM");

            Assert.Equal("1500 RPM", fan.Rpm);
            Assert.Equal($"pwm 200 · {200 * 100 / 255}%", fan.Pwm);
            Assert.True(fan.RpmHistory.Count > historyBefore);
            Assert.Contains(1500.0, fan.RpmHistory);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Editor_CurveWorkingPoint_FollowsLiveTemperature()
    {
        var (ctrl, fake) = Start(UiTestHelpers.LiveSnapshot(45, 1200));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.Editor.Curves.Count > 0);
            CurveEditRow curve = ctrl.Editor.Curves.Single();
            UiTestHelpers.PumpUntil(() => Math.Abs(curve.LiveTemperature - 45.0) < 0.001);
            Assert.Equal(45.0, curve.LiveTemperature, 3);

            fake.Current = UiTestHelpers.LiveSnapshot(60, 1200);
            UiTestHelpers.PumpUntil(() => Math.Abs(curve.LiveTemperature - 60.0) < 0.001);
            Assert.Equal(60.0, curve.LiveTemperature, 3);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Editor_DeviceTabRows_FollowLiveValues()
    {
        var (ctrl, fake) = Start(UiTestHelpers.LiveSnapshot(45, 1200));
        try
        {
            UiTestHelpers.PumpUntil(() =>
                ctrl.Editor.IsReady && ctrl.Editor.Sensors.Any(s => s.LiveValue == "45.0 °C"));
            SensorOption sensor = ctrl.Editor.Sensors.Single(s => s.Id == "hwmon0/temp1");
            FanAssignRow fan = ctrl.Editor.Fans.Single();
            Assert.Equal("45.0 °C", sensor.LiveValue);
            UiTestHelpers.PumpUntil(() => fan.LiveRpm == "1200 RPM");
            Assert.Equal("1200 RPM", fan.LiveRpm);

            fake.Current = UiTestHelpers.LiveSnapshot(55, 1500);
            UiTestHelpers.PumpUntil(() => sensor.LiveValue == "55.0 °C" && fan.LiveRpm == "1500 RPM");

            Assert.Equal("55.0 °C", sensor.LiveValue);
            Assert.Equal("1500 RPM", fan.LiveRpm);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void LiveUpdates_NeverMarkEditorDirty()
    {
        var (ctrl, fake) = Start(UiTestHelpers.LiveSnapshot(45, 1200));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
            Assert.False(ctrl.Editor.HasUnsavedChanges);

            // Mehrere Ticks mit wechselnden Live-Werten (identische Config) dürfen NIE „ungespeichert" auslösen.
            foreach ((double t, double r) in new[] { (50.0, 1300.0), (62.5, 1750.0), (71.0, 2600.0) })
            {
                fake.Current = UiTestHelpers.LiveSnapshot(t, r);
                UiTestHelpers.PumpUntil(() => Math.Abs(ctrl.Editor.Curves.Single().LiveTemperature - t) < 0.001);
            }

            Assert.False(ctrl.Editor.HasUnsavedChanges);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void NaNReadings_RenderAsNotAvailable_AcrossDashboardAndEditor()
    {
        var (ctrl, fake) = Start(UiTestHelpers.LiveSnapshot(double.NaN, double.NaN));
        try
        {
            UiTestHelpers.PumpUntil(() =>
                ctrl.Editor.IsReady && ctrl.Temperatures.Any() && ctrl.Fans.Any());

            Assert.Equal("n/a", ctrl.Temperatures.Single().Display);
            Assert.Equal("n/a", ctrl.Fans.Single().Rpm);

            Assert.Equal("n/a", ctrl.Editor.Sensors.Single(s => s.Id == "hwmon0/temp1").LiveValue);
            Assert.Equal("n/a", ctrl.Editor.Fans.Single().LiveRpm);
            Assert.True(double.IsNaN(ctrl.Editor.Curves.Single().LiveTemperature));

            // NaN fließt nicht in die Sparkline-Historie.
            Assert.Empty(ctrl.Temperatures.Single().History);
            Assert.Empty(ctrl.Fans.Single().RpmHistory);
        }
        finally { ctrl.Dispose(); }
    }
}
