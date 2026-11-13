// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;

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
    [InlineData(OnboardingStep.Welcome, true, false, false, false, true)]
    [InlineData(OnboardingStep.Calibration, true, true, false, false, true)]
    [InlineData(OnboardingStep.Devices, true, true, false, false, true)]
    [InlineData(OnboardingStep.ChooseProfile, false, true, true, false, true)]
    [InlineData(OnboardingStep.Done, false, false, false, true, false)]
    public void Onboarding_NavButtonVisibility_TracksStep(
        OnboardingStep step, bool weiter, bool zurueck, bool fertig, bool schliessen, bool ueberspringen)
    {
        var (ctrl, window) = ShowWizard();

        ctrl.CurrentStep = step;
        Dispatcher.UIThread.RunJobs();

        // Exercised hier: EnumMatchConverter mit Einzel- ('Welcome') und Mehrwert-Parameter
        // ('Calibration,Devices,ChooseProfile' - der Komma-Trap-Fall, einfach-gequotet).
        // Done ist die Quittungsseite: nur „Schließen", kein Zurück/Fertigstellen/Überspringen mehr.
        Assert.Equal(weiter, Nav(window, "Weiter").IsVisible);
        Assert.Equal(zurueck, Nav(window, "Zurück").IsVisible);
        Assert.Equal(fertig, Nav(window, "Fertigstellen").IsVisible);
        Assert.Equal(schliessen, Nav(window, "Schließen").IsVisible);
        Assert.Equal(ueberspringen, Nav(window, "Einrichtung überspringen").IsVisible);
    }

    [AvaloniaFact]
    public void Onboarding_FanPositionButton_ReflectsLocationChange()
    {
        // Verifiziert das Sub-Property-Binding `{Binding Location.Display}`: ersetzt der Positions-Dialog
        // `row.Location` (ObservableProperty), muss der gebundene Button-Text nachziehen - sonst zeigt das
        // Modal die Auswahl, der Button aber weiter die alte Position.
        var (ctrl, window) = ShowWizard();
        ctrl.Apply(new MonitorSnapshot(
            "test",
            [new SensorReading("temp1", "CPU", SensorKind.Temperature, "°C", 50)],
            [new FanReading("f1", "Case Fan", 900, 100, CanControl: true)],
            AppConfig.Empty));
        ctrl.CurrentStep = OnboardingStep.Devices;
        Dispatcher.UIThread.RunJobs();

        OnboardingFanRow row = ctrl.Fans.Single(f => f.FanId == "f1");
        Assert.Contains(window.Find<TextBlock>(), t => t.Text == "- nicht zugeordnet -"); // Ausgangszustand

        row.Location = FanLocationOption.For(FanLocation.CaseRearExhaust);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Gehäuse hinten · Auslass", row.Location.Display); // Sanity: Datenseite stimmt
        Assert.Contains(window.Find<TextBlock>(), t => t.Text == "Gehäuse hinten · Auslass"); // UI zog nach
        Assert.DoesNotContain(window.Find<TextBlock>(), t => t.Text == "- nicht zugeordnet -");
    }

    [AvaloniaFact]
    public void Onboarding_ProfileStep_TogglesAirflowAndPrimarySensorPanels()
    {
        // Honest binding verification for the two IsVisible bindings on the profile step: fallback mode
        // shows the primary-sensor chip, airflow mode (role-specific position set) swaps it for the
        // airflow summary panel.
        var (ctrl, window) = ShowWizard();
        ctrl.Apply(new MonitorSnapshot(
            "test",
            [new SensorReading("temp1", "CPU", SensorKind.Temperature, "°C", 50)],
            [new FanReading("f1", "Case Fan", 900, 100, CanControl: true)],
            AppConfig.Empty));

        ctrl.CurrentStep = OnboardingStep.ChooseProfile; // no positions → fallback mode
        Dispatcher.UIThread.RunJobs();

        Button combo = Assert.Single(window.Find<Button>(), b => b.Name == "PrimarySensorChip");
        Assert.True(combo.IsEffectivelyVisible);
        Assert.DoesNotContain(window.Find<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text?.StartsWith("Lüfter-Positionen erkannt") == true);

        ctrl.Fans.Single(f => f.FanId == "f1").Location = FanLocationOption.For(FanLocation.CaseRearExhaust);
        ctrl.CurrentStep = OnboardingStep.Devices;       // leave and …
        ctrl.CurrentStep = OnboardingStep.ChooseProfile; // … re-enter: the gate re-evaluates on entry
        Dispatcher.UIThread.RunJobs();

        Assert.False(combo.IsEffectivelyVisible);
        Assert.Contains(window.Find<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text?.StartsWith("Lüfter-Positionen erkannt") == true);
    }

    [AvaloniaFact]
    public void Onboarding_PrimarySensorChip_OpensItsMenu_AndAPickSelectsTheSensor()
    {
        var (ctrl, window) = ShowWizard();
        ctrl.Apply(new MonitorSnapshot(
            "test",
            [
                new SensorReading("temp1", "CPU", SensorKind.Temperature, "°C", 50),
                new SensorReading("temp2", "GPU", SensorKind.Temperature, "°C", 61.5),
            ],
            [new FanReading("f1", "Case Fan", 900, 100, CanControl: true)],
            AppConfig.Empty));
        ctrl.CurrentStep = OnboardingStep.ChooseProfile;
        Dispatcher.UIThread.RunJobs();
        Button chip = Assert.Single(window.Find<Button>(), b => b.Name == "PrimarySensorChip");
        Assert.Contains(chip.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "CPU");
        Assert.Contains(chip.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "50.0 °C");

        chip.Flyout!.ShowAt(chip); // Opening builds the menu with the live values
        Dispatcher.UIThread.RunJobs();
        Assert.True(((Avalonia.Controls.Primitives.PopupFlyoutBase)chip.Flyout).IsOpen);
        MenuChoice gpu = Assert.Single(ctrl.PrimarySensorMenu, m => m.Header == "GPU");
        Assert.Equal("61.5 °C", gpu.Detail);
        gpu.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("temp2", ctrl.SelectedPrimarySensor?.Id);
        Assert.Contains(chip.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "GPU");
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

    private static Color ResourceColor(OnboardingWindow w, string key)
    {
        Assert.True(w.TryFindResource(key, w.ActualThemeVariant, out object? value), $"Resource '{key}' missing.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaFact]
    public void Onboarding_StepIndicator_HiddenOnWelcome_StylesDoneAndCurrent()
    {
        var (ctrl, window) = ShowWizard();
        ItemsControl indicator = window.Find<ItemsControl>().Single(i => ReferenceEquals(i.ItemsSource, ctrl.Steps));

        Assert.False(indicator.IsVisible);
        Assert.Contains(window.Find<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Vier Schritte: Kalibrierung · Geräte · Profil · Fertig.");

        ctrl.CurrentStep = OnboardingStep.Devices;
        Dispatcher.UIThread.RunJobs();

        // The Classes.done/current bindings must reach the window styles: accent bars up to the current step,
        // the current label brightest and semibold, done labels dimmer, the rest faint.
        Assert.True(indicator.IsVisible);
        List<Border> bars = indicator.Find<Border>().Where(b => b.Classes.Contains("stepBar")).ToList();
        List<TextBlock> labels = indicator.Find<TextBlock>().Where(t => t.Classes.Contains("stepLabel")).ToList();
        Color accent = ResourceColor(window, "Accent"), track = ResourceColor(window, "ControlTrack");
        Assert.Equal(new[] { accent, accent, track, track }, bars.Select(b => ColorOf(b.Background)));
        Assert.Equal(new[] { "1 · Kalibrierung", "2 · Geräte", "3 · Profil", "4 · Fertig" }, labels.Select(t => t.Text));
        Assert.Equal(
            new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Normal, FontWeight.Normal },
            labels.Select(t => t.FontWeight));
        Assert.Equal(
            new[]
            {
                ResourceColor(window, "TextSecondary"), ResourceColor(window, "TextPrimary"),
                ResourceColor(window, "TextFaint"), ResourceColor(window, "TextFaint"),
            },
            labels.Select(t => ColorOf(t.Foreground)));

        ctrl.CurrentStep = OnboardingStep.Welcome; // Back from the first step
        Dispatcher.UIThread.RunJobs();
        Assert.False(indicator.IsVisible);
    }

    [AvaloniaFact]
    public void Onboarding_DevicesStep_GroupsSensors_CountFollowsCheckbox()
    {
        var (ctrl, window) = ShowWizard();
        ctrl.CurrentStep = OnboardingStep.Devices;
        Dispatcher.UIThread.RunJobs();

        static bool IsGroupsHint(TextBlock t) =>
            t.IsEffectivelyVisible && t.Text?.StartsWith("Gruppen wurden anhand der Treiber vorgeschlagen") == true;
        Assert.DoesNotContain(window.Find<TextBlock>(), IsGroupsHint); // nothing suggested without sensors

        ctrl.Apply(new MonitorSnapshot(
            "test",
            [
                new SensorReading("k10temp/temp1", "Tctl", SensorKind.Temperature, "°C", 50),
                new SensorReading("k10temp/temp3", "Tccd1", SensorKind.Temperature, "°C", 45),
                new SensorReading("nvme@nvme0/temp1", "SSD", SensorKind.Temperature, "°C", 38),
            ],
            [],
            AppConfig.Empty));
        Dispatcher.UIThread.RunJobs();

        bool Shows(string text) => window.Find<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
        Assert.Contains(window.Find<TextBlock>(), IsGroupsHint);
        Assert.True(Shows("CPU"));
        Assert.True(Shows("2 Sensoren"));
        Assert.True(Shows("Speicher"));
        Assert.True(Shows("1 Sensor"));

        CheckBox tccd = window.Find<CheckBox>().Single(c => c.DataContext is SensorOption { Id: "k10temp/temp3" });
        tccd.IsChecked = false; // View → controller through the nested group template
        Dispatcher.UIThread.RunJobs();

        Assert.False(ctrl.TemperatureSensors.Single(s => s.Id == "k10temp/temp3").Visible);
        Assert.True(Shows("1 von 2 sichtbar"));
        Assert.False(Shows("2 Sensoren"));
    }

    [AvaloniaFact]
    public void Onboarding_DevicesStep_AllSensorsGrouped_NoSuggestionHint()
    {
        // Re-run of the wizard: every sensor already has its group - nothing was suggested, the hint would be wrong.
        var (ctrl, window) = ShowWizard();
        ctrl.Apply(new MonitorSnapshot(
            "test",
            [new SensorReading("k10temp/temp1", "Tctl", SensorKind.Temperature, "°C", 50)],
            [],
            new AppConfig
            {
                Sensors = [new SensorConfig { SensorId = "k10temp/temp1", Name = "Tctl", Group = "Prozessor" }],
                SensorGroups = ["Prozessor"],
            }));
        ctrl.CurrentStep = OnboardingStep.Devices;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(window.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Prozessor");
        Assert.DoesNotContain(window.Find<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text?.StartsWith("Gruppen wurden anhand der Treiber vorgeschlagen") == true);
    }

    [AvaloniaFact]
    public void Onboarding_CalibrationButtons_HiddenWhileCalibrating()
    {
        // During a run the (then useless) button row gives its room to the fan list and the progress card.
        var (ctrl, window) = ShowWizard();
        FanReading fan = new("f1", "CPU Fan", 900, 100, CanControl: true);
        var snapshot = new MonitorSnapshot(
            "test", [new SensorReading("k10temp/temp1", "Tctl", SensorKind.Temperature, "°C", 50)], [fan], AppConfig.Empty);
        ctrl.Apply(snapshot);
        ctrl.CurrentStep = OnboardingStep.Calibration;
        Dispatcher.UIThread.RunJobs();
        Button calibrate = Nav(window, "Kalibrieren (empfohlen)");
        Button skip = Nav(window, "Kalibrierung überspringen");
        Assert.True(calibrate.IsEffectivelyVisible && skip.IsEffectivelyVisible);

        Task run = ctrl.CalibrateAllCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(ctrl.IsCalibrating);
        Assert.False(calibrate.IsEffectivelyVisible);
        Assert.False(skip.IsEffectivelyVisible);

        ctrl.Apply(snapshot with
        {
            Calibration = new CalibrationStatus("f1", CalibrationPhase.Done, 0, 0, Running: false, Done: true, StartPwm: 96, FailReason: null),
        });
        while (!run.IsCompleted)
            Dispatcher.UIThread.RunJobs(); // the sequence resumes through the dispatcher

        Assert.False(ctrl.IsCalibrating);
        Assert.True(calibrate.IsEffectivelyVisible && skip.IsEffectivelyVisible);
    }

    /// <summary>
    /// The window is fixed at 560×520 and the step indicator now takes part of its height: the welcome page and a
    /// calibration step with a typical fan count must still fit without scrolling. The headless text stub measures
    /// text wider than real fonts (more line wraps), so this errs on the strict side.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(OnboardingStep.Welcome)]
    [InlineData(OnboardingStep.Calibration)]
    public void Onboarding_FixedWindow_StepFitsWithoutScrolling(OnboardingStep step)
    {
        var (ctrl, window) = ShowWizard();
        ctrl.Apply(new MonitorSnapshot(
            "test",
            [new SensorReading("k10temp/temp1", "Tctl", SensorKind.Temperature, "°C", 50)],
            Enumerable.Range(1, 4)
                .Select(i => new FanReading($"nct6798/pwm{i}", $"Fan {i}", 900, 100, CanControl: true))
                .ToArray(),
            AppConfig.Empty));
        ctrl.CurrentStep = step;
        Dispatcher.UIThread.RunJobs();

        ScrollViewer content = Assert.Single(window.Find<ScrollViewer>(), s => s.IsEffectivelyVisible);
        double contentHeight = Assert.IsAssignableFrom<Control>(content.Content).DesiredSize.Height;
        Assert.True(content.Viewport.Height > 0);
        Assert.True(contentHeight <= content.Viewport.Height,
            $"{step}: content {contentHeight:0} px, room {content.Viewport.Height:0} px");
    }
}
