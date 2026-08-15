// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Views;

namespace LinFan.App.UiTests;

/// <summary>
/// Tier-3-Smoke-Tests: Steuerbefehle, die die GUI an den Daemon sendet, werden über einen Fake-
/// <c>ICommandSink</c> beobachtet - Manual-PWM/Auto im Dashboard und das Speichern. Plus die
/// Sensor-Sichtbarkeit im Geräte-Tab (editor-lokal, ohne Sink).
/// </summary>
public class Tier3SmokeTests
{
    private static (MainController ctrl, FakeLiveMonitor fake, MainWindow window) Show(
        LinFan.App.Services.MonitorSnapshot snapshot)
    {
        var fake = new FakeLiveMonitor(snapshot); // ist zugleich der ICommandSink
        var ctrl = new MainController(fake);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        return (ctrl, fake, window);
    }

    private static void SelectTab(MainWindow window, int index)
    {
        TabControl tabs = Assert.Single(window.Find<TabControl>());
        tabs.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Geraete_SensorVisibilityToggle_UpdatesVisibleSensors()
    {
        var (ctrl, _, window) = Show(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, 2); // Einstellungen → Sensoren (Default-Sektion)

        // Sichtbarkeit hängt jetzt am Augen-Button (ToggleVisibleCommand) statt am ToggleSwitch.
        Button eye = window.Find<Button>().Single(b => b.DataContext is SensorOption);
        var sensor = (SensorOption)eye.DataContext!;
        Assert.True(sensor.Visible);
        Assert.Contains(ctrl.Editor.VisibleSensors, s => s.Id == sensor.Id);

        eye.Command!.Execute(eye.CommandParameter); // ausblenden
        Dispatcher.UIThread.RunJobs();
        Assert.False(sensor.Visible);
        Assert.DoesNotContain(ctrl.Editor.VisibleSensors, s => s.Id == sensor.Id);

        eye.Command!.Execute(eye.CommandParameter); // wieder einblenden
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(ctrl.Editor.VisibleSensors, s => s.Id == sensor.Id);
    }

    [AvaloniaFact]
    public void Dashboard_ManualToggleAndSlider_SendCommandsViaSink()
    {
        var (_, fake, window) = Show(UiTestHelpers.SnapshotWithFans(
            new LinFan.App.Services.FanReading("pwm1", "Fan", 1000, 128, LinFan.Core.Models.FanMode.Auto, CanControl: true)));
        UiTestHelpers.PumpUntil(() => window.Find<ToggleSwitch>().Any(t => t.DataContext is FanRow));
        // Übersicht ist der Default-Tab → das Dashboard ist bereits im Visual-Tree.

        ToggleSwitch manual = window.Find<ToggleSwitch>().Single(t => t.DataContext is FanRow);
        manual.IsChecked = true; // Manuell → ein SetManualPwm
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(fake.ManualCalls, c => c.fanId == "pwm1");

        fake.ManualCalls.Clear();
        Slider slider = window.Find<Slider>().Single(s => s.DataContext is FanRow);
        slider.Value = 80; // → SetManualPwm mit dem neuen PWM (jetzt gedrosselt: Coalescing-Pumpe, nicht sofort)
        byte expected = (byte)Math.Round(80 * 255.0 / 100);
        // Der Stellwert läuft über die Throttle-Pumpe (max. ein Send in der Luft) → in Echtzeit abwarten.
        UiTestHelpers.PumpUntil(() => fake.ManualCalls.Any(c => c.fanId == "pwm1" && c.pwm == expected));
        Assert.Contains(fake.ManualCalls, c => c.fanId == "pwm1" && c.pwm == expected);

        manual.IsChecked = false; // Auto → SetFanAuto
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(fake.AutoCalls, f => f == "pwm1");
    }

    [AvaloniaFact]
    public void Apply_Button_SendsConfigViaSink()
    {
        var (ctrl, fake, window) = Show(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        ctrl.Editor.HasUnsavedChanges = true; // blendet „Übernehmen" neben der Profil-Auswahl ein

        // Über den Namen gesucht: der Knopf trägt nur sein Symbol, keine Beschriftung.
        Button apply = window.Find<Button>().Single(b => b.Name == "HeaderSaveButton");
        Assert.Empty(fake.ConfigCalls);

        apply.Command!.Execute(null); // → Editor.SaveCommand → SendConfigAsync
        Dispatcher.UIThread.RunJobs();

        Assert.Single(fake.ConfigCalls);
    }

    [AvaloniaFact]
    public void Einstellungen_MenuIcons_ResolveFromKeyViaConverter()
    {
        // Belegt den Happy-Path des ResourceKeyConverter (App vorhanden): der String-IconKey jedes Menü-
        // Eintrags löst zur Geometrie auf. Die Controller-Schicht führt nur den Schlüssel, die View rendert.
        var (ctrl, _, window) = Show(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, 2); // Einstellungen

        var icons = window.Find<PathIcon>().Where(p => p.DataContext is SettingsSectionItem).ToList();
        Assert.NotEmpty(icons);                       // die Seitenmenü-Einträge sind realisiert
        Assert.All(icons, p => Assert.NotNull(p.Data)); // jeder Icon-Schlüssel wurde aufgelöst (kein leeres Icon)
    }
}
