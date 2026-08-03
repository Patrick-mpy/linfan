// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.UiTests;

/// <summary>
/// Die geteilte Kalibrier-Anzeige im Hauptfenster: der Dashboard-Lüfter spiegelt den laufenden Status
/// (Button-Sperre + Lauf-Indikator), und das Banner zeigt die <see cref="CalibrationCard"/> mit Kopfzeile
/// + Fortschritt. Läuft über den echten Poll-Loop + Dispatcher.
/// </summary>
public class MainControllerCalibrationTests
{
    // FanName setzt im Echtbetrieb der IpcLiveMonitor aus der Config; der Fake umgeht ihn → hier mitgeben.
    private static CalibrationStatus Running(string fanId, string? fanName = "CPU Fan") =>
        new(fanId, CalibrationPhase.Measuring, 128, 1500, Running: true, Done: false, StartPwm: null, FailReason: null, FanName: fanName);

    [AvaloniaFact]
    public void RunningCalibration_SetsMatchingFanRow_IsCalibrating()
    {
        var fake = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        var ctrl = new MainController(fake, pollInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Fans.Count > 0);
            FanRow fan = ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1");
            Assert.False(fan.IsCalibrating);
            Assert.True(fan.CanCalibrate);

            fake.Current = UiTestHelpers.SampleSnapshot() with { Calibration = Running("hwmon0/pwm1") };
            UiTestHelpers.PumpUntil(() => fan.IsCalibrating);
            Assert.False(fan.CanCalibrate); // Button gesperrt, solange der Lauf läuft

            fake.Current = UiTestHelpers.SampleSnapshot(); // kein Lauf mehr → wieder frei
            UiTestHelpers.PumpUntil(() => !fan.IsCalibrating);
            Assert.True(fan.CanCalibrate);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Banner_ShowsCalibrationCard_WithFriendlyHeadline()
    {
        var fake = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot() with { Calibration = Running("hwmon0/pwm1") });
        var ctrl = new MainController(fake, pollInterval: TimeSpan.FromMilliseconds(10));
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Calibration is { Running: true });

            CalibrationCard card = window.Find<CalibrationCard>().Single();
            Assert.True(card.IsEffectivelyVisible);
            Assert.Equal("Kalibriere CPU Fan", card.Headline); // Anzeigename aus der Config, nicht die Hardware-Id
            Assert.True(card.ShowProgress);
            Assert.InRange(card.Progress, 49, 51); // 128/255 ≈ 50 %
        }
        finally { ctrl.Dispose(); }
    }
}
