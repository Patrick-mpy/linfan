// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.UiTests;

/// <summary>
/// Regression: Der Kurven-Editor (Geräte- und Kurven-Tab) muss die im Erststart-Assistenten gewählten
/// Positionen und Profile übernehmen. Früher initialisierte der Editor einmalig aus der leeren
/// Vor-Onboarding-Config und lud danach nie nach → Geräte-Tab zeigte „--nicht zugeordnet--",
/// Kurven-Tab blieb leer. Jetzt wird die Initialisierung verschoben, solange das Erststart-Signal
/// (<c>OnboardingCompleted == false</c>) ansteht.
/// </summary>
public class MainControllerOnboardingTests
{
    private static MonitorSnapshot FirstRun() => new(
        "Verbunden",
        new[] { new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0) },
        new[] { new FanReading("hwmon0/pwm1", "CPU Fan", 1200, 120, FanMode.Auto, CanControl: true) },
        AppConfig.Empty with { OnboardingCompleted = false },
        Connected: true);

    private static MonitorSnapshot AfterOnboarding()
    {
        var curve = new CurveConfig
        {
            Id = "balanced-curve",
            Name = "Ausgewogen",
            SourceSensorIds = new[] { "hwmon0/temp1" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU" } },
            Fans = new[]
            {
                new FanConfig
                {
                    FanId = "hwmon0/pwm1", Name = "CPU Fan",
                    Location = FanLocation.CpuCooler, AssignedCurveId = "balanced-curve",
                },
            },
            Curves = new[] { curve },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "balanced", Name = "Ausgewogen",
                    Curves = new[] { curve },
                    Assignments = new[] { new ProfileAssignment("hwmon0/pwm1", "balanced-curve") },
                },
            },
            ActiveProfileId = "balanced",
            OnboardingCompleted = true,
        };
        return new MonitorSnapshot(
            "Verbunden",
            new[] { new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0) },
            new[] { new FanReading("hwmon0/pwm1", "CPU Fan", 1200, 120, FanMode.Auto, CanControl: true) },
            config,
            Connected: true);
    }

    [AvaloniaFact]
    public void Editor_DefersInit_DuringFirstRunOnboarding_ThenLoadsChosenConfig()
    {
        var fake = new FakeLiveMonitor(FirstRun());
        var ctrl = new MainController(fake, pollInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.HasSnapshot);

            // Während des Erststart-Assistenten bleibt der Editor bewusst uninitialisiert — kein vorzeitiges
            // Befüllen aus der leeren Config (sonst Latch auf „nicht zugeordnet"/leere Kurven).
            Assert.False(ctrl.Editor.IsReady);
            Assert.Empty(ctrl.Editor.Fans);
            Assert.Empty(ctrl.Editor.Curves);
            Assert.False(ctrl.ShowNoDevices); // trotz leerem Editor KEIN „Keine Geräte"-Hinweis hinter dem Assistenten

            // Onboarding abgeschlossen: der Daemon broadcastet die Config mit Position + Profil/Kurve.
            fake.Current = AfterOnboarding();
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);

            // Geräte-Tab: gewählte Position übernommen (nicht mehr „--nicht zugeordnet--").
            FanAssignRow fan = Assert.Single(ctrl.Editor.Fans);
            Assert.Equal(FanLocation.CpuCooler, fan.Location.Value);

            // Kurven-Tab: Profil + Kurve aus dem Onboarding vorhanden.
            Assert.NotEmpty(ctrl.Editor.Profiles);
            Assert.NotEmpty(ctrl.Editor.Curves);
        }
        finally { ctrl.Dispose(); }
    }

    /// <summary>
    /// Regression: Assistent auf bestehender Config wiederholt („Einstellungen → Onboarding"). Da war der
    /// Editor bereits aus der ALTEN Config befüllt und die einmalige Initialisierung griff nicht mehr — die
    /// im Assistenten gewählten Positionen blieben unsichtbar und das nächste Speichern schrieb sie wieder
    /// weg. Der Assistent muss den Editor-Neuaufbau auslösen, sobald der Daemon die neue Config spiegelt.
    /// </summary>
    [AvaloniaFact]
    public void RepeatedOnboarding_RebuildsEditor_FromChosenConfig()
    {
        var fake = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()); // OnboardingCompleted == null ⇒ kein Erststart
        var ctrl = new MainController(fake, pollInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
            Assert.Equal(FanLocation.Unspecified, Assert.Single(ctrl.Editor.Fans).Location.Value);

            ctrl.StartOnboardingCommand.Execute(null);
            UiTestHelpers.PumpUntil(() => ctrl.Onboarding?.Fans.Count > 0);

            OnboardingController wizard = ctrl.Onboarding!;
            wizard.Fans[0].Location = FanLocationOption.For(FanLocation.CpuCooler);
            wizard.FinishCommand.Execute(null);
            UiTestHelpers.PumpUntil(() => fake.ConfigCalls.Count > 0);

            // Daemon übernimmt die gesendete Config und spiegelt sie zurück.
            fake.Current = fake.Current with { Config = fake.ConfigCalls[^1] };

            UiTestHelpers.PumpUntil(() => ctrl.Editor.Fans.Any(f => f.Location.Value == FanLocation.CpuCooler));
            Assert.Equal(FanLocation.CpuCooler, Assert.Single(ctrl.Editor.Fans).Location.Value);
        }
        finally { ctrl.Dispose(); }
    }

    /// <summary>
    /// Regression: the manual re-run ("Settings → Onboarding") must wire the tach-mapping delegates just
    /// like the first-run path — without them the wizard silently falls back to the legacy no-coupling
    /// calibration and never pairs tach sensors.
    /// </summary>
    [AvaloniaFact]
    public void RepeatedOnboarding_CalibrationStartsTachCoupling()
    {
        var fake = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        var ctrl = new MainController(fake, pollInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);

            ctrl.StartOnboardingCommand.Execute(null);
            UiTestHelpers.PumpUntil(() => ctrl.Onboarding?.ControllableFans.Count > 0);

            OnboardingController wizard = ctrl.Onboarding!;
            wizard.CalibrateAllCommand.Execute(null);

            // Coupling must run as phase 1 (fails before the fix: calibration starts directly).
            UiTestHelpers.PumpUntil(() => fake.TachMappingCalls.Count > 0);
            Assert.Contains("hwmon0/pwm1", fake.TachMappingCalls);

            // Let the coupling fail so the single-fan sequence terminates cleanly.
            fake.Current = fake.Current with
            {
                TachMapping = new TachMappingStatus("hwmon0/pwm1", TachMappingPhase.Failed, Running: false),
            };
            UiTestHelpers.PumpUntil(() => !wizard.IsCalibrating);
            Assert.False(wizard.IsCalibrating);
        }
        finally { ctrl.Dispose(); }
    }
}
