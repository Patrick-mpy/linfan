// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
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
}
