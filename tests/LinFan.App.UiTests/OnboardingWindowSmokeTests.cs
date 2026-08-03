// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Smoke-Tests des Onboarding-Fensters. Schwerpunkt: die Schritt-Sichtbarkeit über
/// <c>EnumMatchConverter</c> (genau die Binding-Klasse, die früher als Platzhalter kaputt war) und das
/// ListBox-Einzelauswahl-Binding für die Profilwahl.
/// </summary>
public class OnboardingWindowSmokeTests
{
    private static (OnboardingController ctrl, OnboardingWindow window) ShowWizard()
    {
        var ctrl = new OnboardingController(
            sendStartCalibration: _ => Task.CompletedTask,
            sendCancelCalibration: () => Task.CompletedTask,
            sendConfig: _ => Task.FromResult(true),
            onClose: () => { });
        var window = new OnboardingWindow { DataContext = ctrl };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (ctrl, window);
    }

    private static Button Nav(OnboardingWindow w, string content) =>
        w.Find<Button>().Single(b => (b.Content as string) == content);

    [AvaloniaTheory]
    [InlineData(OnboardingStep.Welcome, true, false, false)]
    [InlineData(OnboardingStep.Calibration, true, true, false)]
    [InlineData(OnboardingStep.Devices, true, true, false)]
    [InlineData(OnboardingStep.ChooseProfile, false, true, true)]
    [InlineData(OnboardingStep.Done, false, true, true)]
    public void Onboarding_NavButtonVisibility_TracksStep(
        OnboardingStep step, bool weiter, bool zurueck, bool fertig)
    {
        var (ctrl, window) = ShowWizard();

        ctrl.CurrentStep = step;
        Dispatcher.UIThread.RunJobs();

        // Exercised hier: EnumMatchConverter mit Einzel- ('Welcome') und Mehrwert-Parameter
        // ('Calibration,Devices,ChooseProfile,Done' — der Komma-Trap-Fall, einfach-gequotet).
        Assert.Equal(weiter, Nav(window, "Weiter").IsVisible);
        Assert.Equal(zurueck, Nav(window, "Zurück").IsVisible);
        Assert.Equal(fertig, Nav(window, "Fertigstellen").IsVisible);
    }

    [AvaloniaFact]
    public void Onboarding_FanPositionButton_ReflectsLocationChange()
    {
        // Verifiziert das Sub-Property-Binding `{Binding Location.Display}`: ersetzt der Positions-Dialog
        // `row.Location` (ObservableProperty), muss der gebundene Button-Text nachziehen — sonst zeigt das
        // Modal die Auswahl, der Button aber weiter die alte Position.
        var (ctrl, window) = ShowWizard();
        ctrl.Apply(new MonitorSnapshot(
            "test",
            [new SensorReading("temp1", "CPU", SensorKind.Temperature, "°C", 50)],
            [new FanReading("f1", "Case Fan", 900, 100, FanMode.Auto, CanControl: true)],
            AppConfig.Empty));
        ctrl.CurrentStep = OnboardingStep.Devices;
        Dispatcher.UIThread.RunJobs();

        OnboardingFanRow row = ctrl.Fans.Single(f => f.FanId == "f1");
        Assert.Contains(window.Find<TextBlock>(), t => t.Text == "— nicht zugeordnet —"); // Ausgangszustand

        row.Location = FanLocationOption.For(FanLocation.CaseRearExhaust);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Gehäuse hinten · Auslass", row.Location.Display); // Sanity: Datenseite stimmt
        Assert.Contains(window.Find<TextBlock>(), t => t.Text == "Gehäuse hinten · Auslass"); // UI zog nach
        Assert.DoesNotContain(window.Find<TextBlock>(), t => t.Text == "— nicht zugeordnet —");
    }

    [AvaloniaFact]
    public void Onboarding_ProfileListBox_SelectionUpdatesController()
    {
        var (ctrl, window) = ShowWizard();
        ctrl.CurrentStep = OnboardingStep.ChooseProfile;
        Dispatcher.UIThread.RunJobs();

        ListBox profiles = Assert.Single(window.Find<ListBox>());
        ProfileOption performance = ctrl.ProfileOptions.Single(p => p.Id == "performance");

        profiles.SelectedItem = performance; // View → Controller (echtes Zwei-Wege-Binding)
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("performance", ctrl.SelectedProfileId);
    }
}
