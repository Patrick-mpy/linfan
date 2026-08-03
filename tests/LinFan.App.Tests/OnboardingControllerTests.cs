// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

public sealed class OnboardingControllerTests
{
    // ---------------------------------------------------------------------------
    // Hilfsmethoden
    // ---------------------------------------------------------------------------

    private static MonitorSnapshot MakeSnapshot(
        IReadOnlyList<SensorReading>? sensors = null,
        IReadOnlyList<FanReading>? fans = null,
        AppConfig? config = null,
        CalibrationStatus? calibration = null,
        TachMappingStatus? tachMapping = null) =>
        new(
            "test",
            sensors ?? DefaultSensors(),
            fans ?? DefaultFans(),
            config ?? AppConfig.Empty,
            Calibration: calibration,
            TachMapping: tachMapping);

    private static SensorReading[] DefaultSensors() =>
    [
        new("hwmon0/temp1", "CPU Package", SensorKind.Temperature, "°C", 55.0),
        new("hwmon0/temp2", "GPU Temp", SensorKind.Temperature, "°C", 62.0),
        new("hwmon0/fan1", "Fan1 RPM", SensorKind.FanRpm, "RPM", 1200),
    ];

    private static FanReading[] DefaultFans() =>
    [
        new("hwmon0/pwm1", "CPU Fan", 1200, 128, FanMode.Auto, CanControl: true),
        new("hwmon0/pwm2", "Case Fan", 900, 100, FanMode.Auto, CanControl: true),
        new("hwmon0/pwm3", "Read-Only Fan", 600, 80, FanMode.Auto, CanControl: false),
    ];

    private static (OnboardingController ctrl, List<AppConfig> sent) MakeController(
        Action? onClose = null)
    {
        var sent = new List<AppConfig>();
        var ctrl = new OnboardingController(
            sendStartCalibration: _ => Task.CompletedTask,
            sendCancelCalibration: () => Task.CompletedTask,
            sendConfig: cfg => { sent.Add(cfg); return Task.FromResult(true); },
            onClose: onClose ?? (() => { }));
        return (ctrl, sent);
    }

    // ---------------------------------------------------------------------------
    // Primär-Sensor-Heuristik (statische Methode)
    // ---------------------------------------------------------------------------

    [Fact]
    public void SelectPrimarySensorId_CpuNameMatch_ReturnsCpuSensor()
    {
        var sensors = new[]
        {
            new SensorOption("gpu/temp1", "GPU Temperature"),
            new SensorOption("cpu/temp1", "cpu package"),   // ← Match
        };
        var readings = new[]
        {
            new SensorReading("gpu/temp1", "GPU Temperature", SensorKind.Temperature, "°C", 80),
            new SensorReading("cpu/temp1", "cpu package", SensorKind.Temperature, "°C", 50),
        };

        string? id = OnboardingController.SelectPrimarySensorId(sensors, readings);

        Assert.Equal("cpu/temp1", id);
    }

    [Fact]
    public void SelectPrimarySensorId_TctlIdMatch_ReturnsTctlSensor()
    {
        var sensors = new[]
        {
            new SensorOption("hwmon6/temp1", "Tctl"),   // Id enthält kein Schlüsselwort, aber Name passt
            new SensorOption("hwmon7/temp1", "GPU Die"),
        };
        var readings = new[]
        {
            new SensorReading("hwmon6/temp1", "Tctl", SensorKind.Temperature, "°C", 60),
            new SensorReading("hwmon7/temp1", "GPU Die", SensorKind.Temperature, "°C", 75),
        };

        string? id = OnboardingController.SelectPrimarySensorId(sensors, readings);

        Assert.Equal("hwmon6/temp1", id); // "Tctl" matcht /tctl/i
    }

    [Fact]
    public void SelectPrimarySensorId_NoCpuMatch_ReturnsHottestSensor()
    {
        var sensors = new[]
        {
            new SensorOption("s1", "NVMe Temp"),
            new SensorOption("s2", "Motherboard"),
            new SensorOption("s3", "GPU Hot Spot"),
        };
        var readings = new[]
        {
            new SensorReading("s1", "NVMe Temp", SensorKind.Temperature, "°C", 40),
            new SensorReading("s2", "Motherboard", SensorKind.Temperature, "°C", 85), // heißester
            new SensorReading("s3", "GPU Hot Spot", SensorKind.Temperature, "°C", 72),
        };

        string? id = OnboardingController.SelectPrimarySensorId(sensors, readings);

        Assert.Equal("s2", id);
    }

    [Fact]
    public void SelectPrimarySensorId_NoCpuNoReadings_ReturnsFirst()
    {
        var sensors = new[]
        {
            new SensorOption("s1", "NVMe Temp"),
            new SensorOption("s2", "Motherboard"),
        };

        string? id = OnboardingController.SelectPrimarySensorId(sensors, Array.Empty<SensorReading>());

        Assert.Equal("s1", id);
    }

    [Fact]
    public void SelectPrimarySensorId_EmptyList_ReturnsNull()
    {
        string? id = OnboardingController.SelectPrimarySensorId(
            Array.Empty<SensorOption>(),
            Array.Empty<SensorReading>());

        Assert.Null(id);
    }

    [Theory]
    [InlineData("tctl")]
    [InlineData("Tdie")]
    [InlineData("CPU Package")]
    [InlineData("core 0")]
    public void SelectPrimarySensorId_VariousCpuKeywords_Match(string name)
    {
        var sensors = new[]
        {
            new SensorOption("s0", "GPU Temp"),
            new SensorOption("s1", name),
        };
        var readings = Array.Empty<SensorReading>();

        string? id = OnboardingController.SelectPrimarySensorId(sensors, readings);

        Assert.Equal("s1", id);
    }

    // ---------------------------------------------------------------------------
    // Apply: Lüfter/Sensor-Befüllung beim ersten Aufruf
    // ---------------------------------------------------------------------------

    [Fact]
    public void Apply_FirstCall_PopulatesControllableFansOnly()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());

        // Nur steuerbare Lüfter
        Assert.Equal(2, ctrl.ControllableFans.Count);
        Assert.All(ctrl.ControllableFans, f => Assert.NotEmpty(f.FanId));
        Assert.DoesNotContain(ctrl.ControllableFans, f => f.Name == "Read-Only Fan");
    }

    [Fact]
    public void Apply_FirstCall_PopulatesAllFans_IncludingReadOnly()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());

        // Positions-Liste umfasst ALLE Lüfter (auch read-only), die Kalibrier-Liste nur steuerbare.
        Assert.Equal(3, ctrl.Fans.Count);
        Assert.Equal(2, ctrl.ControllableFans.Count);
        Assert.Contains(ctrl.Fans, f => f.Name == "Read-Only Fan");
    }

    [Fact]
    public async Task FanRow_Identify_FlowsThroughControllerDelegate_OnlyForControllable()
    {
        var calls = new List<string>();
        var ctrl = new OnboardingController(
            sendStartCalibration: _ => Task.CompletedTask,
            sendCancelCalibration: () => Task.CompletedTask,
            sendConfig: _ => Task.FromResult(true),
            onClose: () => { },
            sendIdentify: id => { calls.Add(id); return Task.CompletedTask; });
        ctrl.Apply(MakeSnapshot());

        OnboardingFanRow controllable = ctrl.Fans.Single(f => f.Name == "CPU Fan");
        OnboardingFanRow readOnly = ctrl.Fans.Single(f => f.Name == "Read-Only Fan");
        Assert.True(controllable.CanControl);
        Assert.False(readOnly.CanControl);

        await controllable.IdentifyCommand.ExecuteAsync(null);
        await readOnly.IdentifyCommand.ExecuteAsync(null); // read-only → kein Befehl

        Assert.Equal(new[] { "hwmon0/pwm1" }, calls);
    }

    [Fact]
    public void Apply_DefaultHidesSensorsWithoutOutput()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(sensors:
        [
            new("hwmon0/temp1", "CPU Package", SensorKind.Temperature, "°C", 55.0),
            new("hwmon0/temp9", "Dead Sensor", SensorKind.Temperature, "°C", double.NaN),
        ]));

        Assert.True(ctrl.TemperatureSensors.Single(s => s.Id == "hwmon0/temp1").Visible);
        Assert.False(ctrl.TemperatureSensors.Single(s => s.Id == "hwmon0/temp9").Visible);
    }

    [Fact]
    public void Apply_RespectsConfiguredSensorVisibility_OverNaNDefault()
    {
        var (ctrl, _) = MakeController();
        // Bereits konfiguriert (Rückkehrer): explizite Sichtbarkeit schlägt die NaN-Default-Heuristik.
        var config = new AppConfig
        {
            Sensors = [new SensorConfig { SensorId = "hwmon0/temp9", Name = "Dead Sensor", Hidden = false }],
        };
        ctrl.Apply(MakeSnapshot(
            sensors: [new("hwmon0/temp9", "Dead Sensor", SensorKind.Temperature, "°C", double.NaN)],
            config: config));

        Assert.True(ctrl.TemperatureSensors.Single(s => s.Id == "hwmon0/temp9").Visible);
    }

    [Fact]
    public void Apply_FirstCall_PopulatesTemperatureSensorsOnly()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());

        // Keine FanRpm-Sensoren
        Assert.Equal(2, ctrl.TemperatureSensors.Count);
    }

    [Fact]
    public void Apply_FirstCall_SetsSelectedPrimarySensor_ByCpuHeuristic()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot()); // "CPU Package" matcht /cpu/i

        Assert.NotNull(ctrl.SelectedPrimarySensor);
        Assert.Equal("hwmon0/temp1", ctrl.SelectedPrimarySensor!.Id);
    }

    // ---------------------------------------------------------------------------
    // Finish: baut Config mit 3 Profilen + OnboardingCompleted = true
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Finish_BuildsThreeProfiles_AndSetsOnboardingCompleted()
    {
        bool closeCalled = false;
        var (ctrl, sent) = MakeController(onClose: () => closeCalled = true);

        var fans = new[] { new FanConfig { FanId = "hwmon0/pwm1", Name = "CPU Fan" } };
        var snapshot = MakeSnapshot(
            config: new AppConfig { Fans = fans });
        ctrl.Apply(snapshot);

        // Primärsensor ist nach Apply gesetzt
        Assert.NotNull(ctrl.SelectedPrimarySensor);

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.True(cfg.OnboardingCompleted);
        Assert.Equal(3, cfg.Profiles.Count);
        Assert.Contains(cfg.Profiles, p => p.Id == "silent");
        Assert.Contains(cfg.Profiles, p => p.Id == "balanced");
        Assert.Contains(cfg.Profiles, p => p.Id == "performance");
        Assert.True(closeCalled);
    }

    [Fact]
    public async Task Finish_UsesSelectedProfileId()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig
        {
            Fans = [new FanConfig { FanId = "f1", Name = "Fan" }],
        }));

        ctrl.SelectedProfileId = "performance";

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal("performance", cfg.ActiveProfileId);
    }

    [Fact]
    public async Task Finish_ProfileServiceApply_MaterializesAssignments()
    {
        var (ctrl, sent) = MakeController();
        var fan = new FanConfig { FanId = "f1", Name = "Fan" };
        ctrl.Apply(MakeSnapshot(
            fans: [new FanReading("f1", "Fan", 1200, 128, FanMode.Auto, CanControl: true)],
            config: new AppConfig { Fans = [fan] }));

        ctrl.SelectedProfileId = "silent";
        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        // ProfileService.Apply setzt AssignedCurveId aus dem Profil
        FanConfig sentFan = Assert.Single(cfg.Fans);
        Assert.Equal("silent-curve", sentFan.AssignedCurveId);
    }

    [Fact]
    public async Task Finish_WritesChosenFanLocation()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(
            fans: [new FanReading("f1", "Case Fan", 900, 100, FanMode.Auto, CanControl: true)]));

        ctrl.Fans.Single(f => f.FanId == "f1").Location = FanLocationOption.For(FanLocation.CaseRearExhaust);
        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal(FanLocation.CaseRearExhaust, cfg.Fans.Single(f => f.FanId == "f1").Location);
    }

    [Fact]
    public async Task Finish_WritesSensorVisibility()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot()); // temp1 (CPU) + temp2 (GPU), beide mit Messwert → sichtbar

        ctrl.TemperatureSensors.Single(s => s.Id == "hwmon0/temp2").Visible = false;
        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.True(cfg.Sensors.Single(s => s.SensorId == "hwmon0/temp2").Hidden);
        Assert.False(cfg.Sensors.Single(s => s.SensorId == "hwmon0/temp1").Hidden);
    }

    [Fact]
    public async Task Finish_WithoutCalibration_PersistsAllFans_AssignsCurvesToControllableOnly()
    {
        // Übersprungene Kalibrierung → persistierte Config ohne Lüfter. Finish nimmt trotzdem alle
        // entdeckten Lüfter aus der Live-Discovery auf (Positionen!), ordnet die Profilkurve aber nur den
        // steuerbaren zu — read-only Kanäle bleiben ohne Zuordnung.
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(config: AppConfig.Empty)); // DefaultFans: pwm1+pwm2 steuerbar, pwm3 read-only

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal(3, cfg.Fans.Count); // alle persistiert
        Assert.False(string.IsNullOrEmpty(cfg.Fans.Single(f => f.FanId == "hwmon0/pwm1").AssignedCurveId));
        Assert.False(string.IsNullOrEmpty(cfg.Fans.Single(f => f.FanId == "hwmon0/pwm2").AssignedCurveId));
        Assert.True(string.IsNullOrEmpty(cfg.Fans.Single(f => f.FanId == "hwmon0/pwm3").AssignedCurveId));
    }

    [Fact]
    public async Task Finish_PreservesExistingFanCalibrationAndPwmLimits()
    {
        var (ctrl, sent) = MakeController();
        var existing = new FanConfig { FanId = "f1", Name = "CPU Fan", MinPwm = 42, MaxPwm = 230 };
        ctrl.Apply(MakeSnapshot(
            fans: [new FanReading("f1", "CPU Fan", 1200, 128, FanMode.Auto, CanControl: true)],
            config: new AppConfig { Fans = [existing] }));

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        FanConfig fan = cfg.Fans.Single(f => f.FanId == "f1");
        Assert.Equal(42, fan.MinPwm);
        Assert.Equal(230, fan.MaxPwm);
    }

    // ---------------------------------------------------------------------------
    // Skip: sendet nur OnboardingCompleted = true, keine Profiländerung
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Skip_SendsOnlyOnboardingCompleted_NoProfileChange()
    {
        bool closeCalled = false;
        var (ctrl, sent) = MakeController(onClose: () => closeCalled = true);

        var originalFans = new[] { new FanConfig { FanId = "f1", Name = "Fan", AssignedCurveId = "old-curve" } };
        var snapshot = MakeSnapshot(config: new AppConfig
        {
            Fans = originalFans,
            ActiveProfileId = "someprofile",
        });
        ctrl.Apply(snapshot);

        await ctrl.SkipCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.True(cfg.OnboardingCompleted);
        // Keine neuen Profile, aktives Profil unverändert
        Assert.Empty(cfg.Profiles);
        Assert.Equal("someprofile", cfg.ActiveProfileId);
        // Lüfter-Zuordnung unverändert
        Assert.Equal("old-curve", cfg.Fans[0].AssignedCurveId);
        Assert.True(closeCalled);
    }

    [Fact]
    public async Task Skip_IsIdempotent_SendsOnlyOnce()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot());

        await ctrl.SkipCommand.ExecuteAsync(null);
        await ctrl.SkipCommand.ExecuteAsync(null); // zweiter Aufruf

        Assert.Single(sent); // nur einmal gesendet
    }

    [Fact]
    public async Task Skip_BeforeFirstApply_DoesNotCrash()
    {
        var (ctrl, sent) = MakeController();

        // Kein Apply → _cachedSnapshot ist null
        await ctrl.SkipCommand.ExecuteAsync(null);

        Assert.Empty(sent); // nichts gesendet, kein Absturz
    }

    // ---------------------------------------------------------------------------
    // Schritt-Navigation
    // ---------------------------------------------------------------------------

    [Fact]
    public void Next_AdvancesStepSequentially()
    {
        var (ctrl, _) = MakeController();
        Assert.Equal(OnboardingStep.Welcome, ctrl.CurrentStep);

        ctrl.NextCommand.Execute(null);
        Assert.Equal(OnboardingStep.Calibration, ctrl.CurrentStep);

        ctrl.NextCommand.Execute(null);
        Assert.Equal(OnboardingStep.Devices, ctrl.CurrentStep);

        ctrl.NextCommand.Execute(null);
        Assert.Equal(OnboardingStep.ChooseProfile, ctrl.CurrentStep);

        ctrl.NextCommand.Execute(null);
        Assert.Equal(OnboardingStep.Done, ctrl.CurrentStep);
    }

    [Fact]
    public async Task Back_GoesBackFromCalibration()
    {
        var (ctrl, _) = MakeController();
        ctrl.NextCommand.Execute(null); // → Calibration

        await ctrl.BackCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingStep.Welcome, ctrl.CurrentStep);
    }

    // ---------------------------------------------------------------------------
    // Latch: Finish + anschließendes Fenster-Schließen (OnClosing→Skip)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Finish_ThenSkipOnClose_DoesNotResendProfilelessConfig()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig { Fans = [new FanConfig { FanId = "f1", Name = "Fan" }] }));

        await ctrl.FinishCommand.ExecuteAsync(null);
        // Das Schließen des Fensters ruft OnClosing → SkipCommand; der Latch muss das unterdrücken,
        // sonst überschriebe eine profillose Config die gerade gesendeten Profile.
        await ctrl.SkipCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);  // nur EINE Sendung (Finish)
        Assert.Equal(3, cfg.Profiles.Count);  // und es ist die Profil-Config, nicht profillos
    }

    // ---------------------------------------------------------------------------
    // Kalibrier-Fortschritt: zeigt den Anzeigenamen, nicht die Hardware-Id
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CalibrateAll_DoneMessage_ShowsFanDisplayName_NotHardwareId()
    {
        var (ctrl, _) = MakeController();
        // Steuerbarer Lüfter mit abweichendem Anzeigenamen (wie „thinkpad pwm1" vs. „hwmon8/pwm1").
        var fans = new[] { new FanReading("hwmon8/pwm1", "thinkpad pwm1", 0, 0, FanMode.Auto, CanControl: true) };
        ctrl.Apply(MakeSnapshot(fans: fans));

        // Kalibrierung starten — blockiert bei „warte auf Done", bis wir unten einen Done-Snapshot einspielen.
        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);

        var done = new CalibrationStatus("hwmon8/pwm1", CalibrationPhase.Done, 0, 0, Running: false, Done: true, StartPwm: 96, FailReason: null);
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: done));

        await calTask;

        Assert.Contains("thinkpad pwm1", ctrl.CalibrationProgress);
        Assert.DoesNotContain("hwmon8/pwm1", ctrl.CalibrationProgress);
    }

    [Fact]
    public async Task CalibrateAll_ErrorMessage_ShowsFanDisplayName_NotHardwareId()
    {
        var (ctrl, _) = MakeController();
        var fans = new[] { new FanReading("hwmon8/pwm1", "thinkpad pwm1", 0, 0, FanMode.Auto, CanControl: true) };
        ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);

        var failed = new CalibrationStatus("hwmon8/pwm1", CalibrationPhase.Failed, 0, 0, Running: false, Done: false, StartPwm: null,
            FailReason: CalibrationFailReason.OverTemperature, OverTempC: 95, OverLimitC: 90);
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: failed));

        await calTask;

        Assert.Contains("thinkpad pwm1", ctrl.CalibrationProgress);
        Assert.DoesNotContain("hwmon8/pwm1", ctrl.CalibrationProgress);
    }

    // ---------------------------------------------------------------------------
    // Kalibrier-Fortschritt: strukturierte Anzeige (Index/Name/Balken + Pro-Lüfter-Status)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CalibrateAll_TracksPerFanAndOverallProgress()
    {
        var (ctrl, _) = MakeController();
        var fans = new[] { new FanReading("hwmon8/pwm1", "thinkpad pwm1", 0, 0, FanMode.Auto, CanControl: true) };
        ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);

        // Sequenz-Kopf ist sofort (vor dem ersten Snapshot) gesetzt.
        Assert.Equal(1, ctrl.CalibrationFanCount);
        Assert.Equal(1, ctrl.CalibrationFanIndex);
        Assert.Equal("thinkpad pwm1", ctrl.CalibrationFanName);
        Assert.Equal("Lüfter 1 von 1: thinkpad pwm1", ctrl.CalibrationHeadline);
        Assert.Equal(OnboardingCalibrationState.Running, ctrl.ControllableFans[0].CalibrationState);

        // Laufender Tick bei halber Rampe (pwm 128/255 ≈ 50 %) → Fan- und Gesamtfortschritt ~50 %.
        var running = new CalibrationStatus("hwmon8/pwm1", CalibrationPhase.Measuring, 128, 300, Running: true, Done: false, StartPwm: null, FailReason: null);
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: running));
        Assert.InRange(ctrl.CalibrationFanProgress, 49, 51);
        Assert.InRange(ctrl.CalibrationOverallProgress, 49, 51);

        // Abschluss → Lüfter „Done", Gesamtfortschritt 100 %.
        var done = new CalibrationStatus("hwmon8/pwm1", CalibrationPhase.Done, 0, 0, Running: false, Done: true, StartPwm: 96, FailReason: null);
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: done));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Done, ctrl.ControllableFans[0].CalibrationState);
        Assert.Equal(100, ctrl.CalibrationOverallProgress);
        Assert.False(ctrl.IsCalibrating);
    }

    [Fact]
    public async Task CalibrateAll_MultipleFans_AdvancesThroughSequence()
    {
        // Recording-Start: nach dem zweiten Start ist _waitingForFanId garantiert auf f2 gesetzt → der nächste
        // Done-Snapshot greift deterministisch (die Schleife läuft nach jedem Done asynchron weiter).
        var startCount = 0;
        var ctrl = new OnboardingController(
            sendStartCalibration: _ => { Interlocked.Increment(ref startCount); return Task.CompletedTask; },
            sendCancelCalibration: () => Task.CompletedTask,
            sendConfig: _ => Task.FromResult(true),
            onClose: () => { });

        var fans = new[]
        {
            new FanReading("f1", "Fan A", 0, 0, FanMode.Auto, CanControl: true),
            new FanReading("f2", "Fan B", 0, 0, FanMode.Auto, CanControl: true),
        };
        ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);

        Assert.Equal(2, ctrl.CalibrationFanCount);
        Assert.Equal(1, ctrl.CalibrationFanIndex);
        Assert.Equal("Fan A", ctrl.CalibrationFanName);
        Assert.Equal(OnboardingCalibrationState.Running, ctrl.ControllableFans[0].CalibrationState);

        // Fan A fertig → Sequenz rückt (asynchron) auf Fan B vor.
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: new CalibrationStatus("f1", CalibrationPhase.Done, 0, 0, false, true, 96, null)));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref startCount) == 2, 2000), "Start für Fan B kam nicht");

        Assert.Equal(2, ctrl.CalibrationFanIndex);
        Assert.Equal("Fan B", ctrl.CalibrationFanName);
        Assert.Equal(OnboardingCalibrationState.Done, ctrl.ControllableFans[0].CalibrationState);
        Assert.Equal(OnboardingCalibrationState.Running, ctrl.ControllableFans[1].CalibrationState);

        // Fan B fertig → Sequenz endet.
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: new CalibrationStatus("f2", CalibrationPhase.Done, 0, 0, false, true, 80, null)));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Done, ctrl.ControllableFans[1].CalibrationState);
        Assert.False(ctrl.IsCalibrating);
        Assert.Equal(100, ctrl.CalibrationOverallProgress);
    }

    [Fact]
    public async Task CalibrateAll_Error_MarksFanFailed()
    {
        var (ctrl, _) = MakeController();
        var fans = new[] { new FanReading("hwmon8/pwm1", "thinkpad pwm1", 0, 0, FanMode.Auto, CanControl: true) };
        ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);
        var failed = new CalibrationStatus("hwmon8/pwm1", CalibrationPhase.Failed, 0, 0, Running: false, Done: false, StartPwm: null,
            FailReason: CalibrationFailReason.OverTemperature, OverTempC: 95, OverLimitC: 90);
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: failed));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Failed, ctrl.ControllableFans[0].CalibrationState);
    }

    [Fact]
    public void CalibrationHeadline_EmptyBeforeSequence()
    {
        var (ctrl, _) = MakeController();
        Assert.Equal("", ctrl.CalibrationHeadline);
    }

    // ---------------------------------------------------------------------------
    // Profil-Auswahl: SelectedProfile (UI-Bindung) hält SelectedProfileId synchron
    // ---------------------------------------------------------------------------

    [Fact]
    public void SelectedProfile_DefaultsToBalanced()
    {
        var (ctrl, _) = MakeController();
        Assert.Equal("balanced", ctrl.SelectedProfile?.Id);
    }

    [Fact]
    public void SelectedProfile_Set_SyncsSelectedProfileId()
    {
        var (ctrl, _) = MakeController();
        ProfileOption perf = ctrl.ProfileOptions.Single(p => p.Id == "performance");

        ctrl.SelectedProfile = perf;

        Assert.Equal("performance", ctrl.SelectedProfileId);
    }

    // ---------------------------------------------------------------------------
    // Temporäre Manuell-Steuerung (Geräte-Schritt): Live-RPM-Anzeige + Revert beim Verlassen
    // ---------------------------------------------------------------------------

    private static OnboardingController MakeControllerWithManual(List<string> auto)
    {
        return new OnboardingController(
            sendStartCalibration: _ => Task.CompletedTask,
            sendCancelCalibration: () => Task.CompletedTask,
            sendConfig: _ => Task.FromResult(true),
            onClose: () => { },
            sendManual: (_, _) => Task.CompletedTask,
            sendAuto: id => { auto.Add(id); return Task.CompletedTask; });
    }

    [Fact]
    public void Apply_FeedsLiveRpmIntoManualControl()
    {
        OnboardingController ctrl = MakeControllerWithManual(new());

        ctrl.Apply(MakeSnapshot()); // CPU Fan @ 1200 RPM (steuerbar)

        OnboardingFanRow fan = ctrl.ControllableFans.First(f => f.FanId == "hwmon0/pwm1");
        Assert.Equal("1200 RPM", fan.Manual.LiveRpm);
    }

    [Fact]
    public void StepChange_RevertsEngagedManualControl()
    {
        var auto = new List<string>();
        OnboardingController ctrl = MakeControllerWithManual(auto);
        ctrl.Apply(MakeSnapshot());

        OnboardingFanRow fan = ctrl.ControllableFans[0];
        fan.Manual.Throttle = TimeSpan.Zero;
        fan.Manual.IsActive = true;       // vorübergehend manuell

        ctrl.NextCommand.Execute(null);    // Schritt-Wechsel verlässt die Fläche

        Assert.False(fan.Manual.IsActive); // beendet …
        Assert.Contains(fan.FanId, auto);  // … und auf Auto/Kurve zurück
    }

    // --- Event-Leak-Regression (2026-07-05 Review): der Assistent wird pro Durchlauf neu erzeugt,
    //     das Localizer-Abo muss beim Schließen (Skip/Finish) wieder gelöst werden. ------------------

    [Fact]
    public void Construction_SubscribesToLocalizer()
    {
        int before = LocalizerProbe.SubscriberCount();
        MakeController();
        Assert.Equal(before + 1, LocalizerProbe.SubscriberCount());
    }

    [Fact]
    public async Task Skip_UnsubscribesFromLocalizer()
    {
        int before = LocalizerProbe.SubscriberCount();
        (OnboardingController ctrl, _) = MakeController();
        Assert.Equal(before + 1, LocalizerProbe.SubscriberCount());

        await ctrl.SkipCommand.ExecuteAsync(null);

        Assert.Equal(before, LocalizerProbe.SubscriberCount());
    }

    [Fact]
    public async Task Finish_UnsubscribesFromLocalizer()
    {
        int before = LocalizerProbe.SubscriberCount();
        (OnboardingController ctrl, _) = MakeController();

        await ctrl.FinishCommand.ExecuteAsync(null); // ohne Snapshot: CloseWizard-Frühpfad

        Assert.Equal(before, LocalizerProbe.SubscriberCount());
    }

    [Fact]
    public async Task Repeated_CreateSkip_DoesNotAccumulateLocalizerHandlers()
    {
        int before = LocalizerProbe.SubscriberCount();

        for (int i = 0; i < 5; i++)
        {
            (OnboardingController ctrl, _) = MakeController();
            await ctrl.SkipCommand.ExecuteAsync(null);
        }

        Assert.Equal(before, LocalizerProbe.SubscriberCount());
    }

    // ---------------------------------------------------------------------------
    // Sequenz „koppeln → kalibrieren" (Tacho-Kopplung als Vorstufe pro Lüfter)
    // ---------------------------------------------------------------------------

    private sealed record CouplingHarness(
        OnboardingController Ctrl, List<string> TachStarts, List<string> CalStarts, List<int> TachCancels);

    private static CouplingHarness MakeCouplingController()
    {
        var tachStarts = new List<string>();
        var calStarts = new List<string>();
        var tachCancels = new List<int>();
        var ctrl = new OnboardingController(
            sendStartCalibration: id => { lock (calStarts) calStarts.Add(id); return Task.CompletedTask; },
            sendCancelCalibration: () => Task.CompletedTask,
            sendConfig: _ => Task.FromResult(true),
            onClose: () => { },
            sendStartTachMapping: id => { lock (tachStarts) tachStarts.Add(id); return Task.CompletedTask; },
            sendCancelTachMapping: () => { lock (tachCancels) tachCancels.Add(1); return Task.CompletedTask; });
        return new CouplingHarness(ctrl, tachStarts, calStarts, tachCancels);
    }

    private static int Count<T>(List<T> list) { lock (list) return list.Count; }
    private static T[] Snap<T>(List<T> list) { lock (list) return list.ToArray(); }

    private static TachMappingStatus Matched(string fanId) =>
        new(fanId, TachMappingPhase.Matched, Running: false, MatchedTachId: fanId + "-tach", RiseRpm: 800);

    private static TachMappingStatus NoResponse(string fanId) =>
        new(fanId, TachMappingPhase.NoResponse, Running: false);

    private static TachMappingStatus Ambiguous(string fanId) =>
        new(fanId, TachMappingPhase.Ambiguous, Running: false);

    private static TachMappingStatus TachFailed(string fanId) =>
        new(fanId, TachMappingPhase.Failed, Running: false,
            FailReason: TachMappingFailReason.OverTemperature, OverTempC: 95, OverLimitC: 90);

    private static CalibrationStatus CalDone(string fanId) =>
        new(fanId, CalibrationPhase.Done, 0, 0, Running: false, Done: true, StartPwm: 96, FailReason: null);

    /// <summary>Ein Lüfter ohne Live-Drehzahl (Rpm 0) → kein brauchbarer Tacho; nicht-eindeutige Kopplung überspringt.</summary>
    private static FanReading[] OneFan() =>
        [new("f1", "Fan A", 0, 0, FanMode.Auto, CanControl: true)];

    /// <summary>Ein Lüfter, der bereits eine positive Live-Drehzahl liefert → ein brauchbarer Tacho ist vorhanden.</summary>
    private static FanReading[] OneFanWithTacho(double rpm = 1500) =>
        [new("f1", "Fan A", rpm, 0, FanMode.Auto, CanControl: true)];

    [Fact]
    public async Task Coupling_Matched_ThenCalibratesFan()
    {
        CouplingHarness h = MakeCouplingController();
        FanReading[] fans = OneFan();
        h.Ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = h.Ctrl.CalibrateAllCommand.ExecuteAsync(null);

        // Phase 1: Kopplung gestartet, Lüfter im Coupling-Zustand (noch nicht kalibriert).
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 1, 2000), "StartTachMapping kam nicht");
        Assert.Equal(OnboardingCalibrationState.Coupling, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Empty(Snap(h.CalStarts));

        // Matched → weiter zur Kalibrierung (nutzt den gekoppelten Tacho).
        h.Ctrl.Apply(MakeSnapshot(fans: fans, tachMapping: Matched("f1")));
        Assert.True(SpinWait.SpinUntil(() => Count(h.CalStarts) == 1, 2000), "StartCalibration nach Matched kam nicht");
        Assert.Equal(OnboardingCalibrationState.Running, h.Ctrl.ControllableFans[0].CalibrationState);

        // Kalibrierung fertig → Sequenz endet.
        h.Ctrl.Apply(MakeSnapshot(fans: fans, calibration: CalDone("f1")));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Done, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Equal(new[] { "f1" }, Snap(h.TachStarts));
        Assert.Equal(new[] { "f1" }, Snap(h.CalStarts));
        Assert.False(h.Ctrl.IsCalibrating);
    }

    [Fact]
    public async Task Coupling_NoResponse_SkipsCalibration_MarksNoTacho()
    {
        CouplingHarness h = MakeCouplingController();
        FanReading[] fans = OneFan();
        h.Ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = h.Ctrl.CalibrateAllCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 1, 2000));

        h.Ctrl.Apply(MakeSnapshot(fans: fans, tachMapping: NoResponse("f1")));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.NoTacho, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Empty(Snap(h.CalStarts));       // keine Kalibrierung ohne Tacho
        Assert.Equal(1, Count(h.TachCancels));  // Abschluss-Status quittiert
        Assert.False(h.Ctrl.IsCalibrating);
    }

    [Fact]
    public async Task Coupling_Ambiguous_SkipsCalibration_MarksAmbiguous()
    {
        CouplingHarness h = MakeCouplingController();
        FanReading[] fans = OneFan();
        h.Ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = h.Ctrl.CalibrateAllCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 1, 2000));

        h.Ctrl.Apply(MakeSnapshot(fans: fans, tachMapping: Ambiguous("f1")));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Ambiguous, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Empty(Snap(h.CalStarts));
        Assert.False(h.Ctrl.IsCalibrating);
    }

    [Fact]
    public async Task Coupling_Ambiguous_WithLiveTacho_CalibratesWithExisting()
    {
        CouplingHarness h = MakeCouplingController();
        FanReading[] fans = OneFanWithTacho(1500); // dreht messbar → brauchbarer Tacho vorhanden
        h.Ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = h.Ctrl.CalibrateAllCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 1, 2000));

        // Mehrdeutig (z. B. zwei Tacho-Kanäle für denselben Lüfter), aber der Lüfter dreht messbar →
        // statt überspringen mit dem vorhandenen Tacho kalibrieren.
        h.Ctrl.Apply(MakeSnapshot(fans: fans, tachMapping: Ambiguous("f1")));
        Assert.True(SpinWait.SpinUntil(() => Count(h.CalStarts) == 1, 2000),
            "StartCalibration nach mehrdeutig + Live-Tacho kam nicht");
        Assert.Equal(OnboardingCalibrationState.Running, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Equal(1, Count(h.TachCancels)); // Ergebnis quittiert, dann kalibriert

        h.Ctrl.Apply(MakeSnapshot(fans: fans, calibration: CalDone("f1")));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Done, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Equal(new[] { "f1" }, Snap(h.CalStarts));
        Assert.False(h.Ctrl.IsCalibrating);
    }

    [Fact]
    public async Task Coupling_NoResponse_WithLiveTacho_CalibratesWithExisting()
    {
        CouplingHarness h = MakeCouplingController();
        FanReading[] fans = OneFanWithTacho(1500);
        h.Ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = h.Ctrl.CalibrateAllCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 1, 2000));

        h.Ctrl.Apply(MakeSnapshot(fans: fans, tachMapping: NoResponse("f1")));
        Assert.True(SpinWait.SpinUntil(() => Count(h.CalStarts) == 1, 2000),
            "StartCalibration nach kein-Signal + Live-Tacho kam nicht");

        h.Ctrl.Apply(MakeSnapshot(fans: fans, calibration: CalDone("f1")));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Done, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Equal(new[] { "f1" }, Snap(h.CalStarts));
        Assert.False(h.Ctrl.IsCalibrating);
    }

    [Fact]
    public async Task Coupling_Failed_SkipsCalibration_MarksFailed()
    {
        CouplingHarness h = MakeCouplingController();
        FanReading[] fans = OneFan();
        h.Ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = h.Ctrl.CalibrateAllCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 1, 2000));

        h.Ctrl.Apply(MakeSnapshot(fans: fans, tachMapping: TachFailed("f1")));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Failed, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Empty(Snap(h.CalStarts));
        Assert.Contains("Übertemperatur", h.Ctrl.CalibrationProgress); // Fehlergrund aus IpcStatusText
        Assert.False(h.Ctrl.IsCalibrating);
    }

    [Fact]
    public async Task Coupling_MixedResults_CalibratesMatched_SkipsNoResponse()
    {
        CouplingHarness h = MakeCouplingController();
        FanReading[] fans =
        [
            new("f1", "Fan A", 0, 0, FanMode.Auto, CanControl: true),
            new("f2", "Fan B", 0, 0, FanMode.Auto, CanControl: true),
        ];
        h.Ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = h.Ctrl.CalibrateAllCommand.ExecuteAsync(null);

        // Fan A: Matched → kalibrieren → fertig.
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 1, 2000));
        h.Ctrl.Apply(MakeSnapshot(fans: fans, tachMapping: Matched("f1")));
        Assert.True(SpinWait.SpinUntil(() => Count(h.CalStarts) == 1, 2000));
        h.Ctrl.Apply(MakeSnapshot(fans: fans, calibration: CalDone("f1")));

        // Fan B: kein Tacho → Kalibrierung übersprungen, Sequenz endet.
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 2, 2000), "Kopplung für Fan B kam nicht");
        h.Ctrl.Apply(MakeSnapshot(fans: fans, tachMapping: NoResponse("f2")));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Done, h.Ctrl.ControllableFans[0].CalibrationState);
        Assert.Equal(OnboardingCalibrationState.NoTacho, h.Ctrl.ControllableFans[1].CalibrationState);
        Assert.Equal(new[] { "f1", "f2" }, Snap(h.TachStarts)); // beide gekoppelt
        Assert.Equal(new[] { "f1" }, Snap(h.CalStarts));        // nur der mit Tacho kalibriert
        Assert.False(h.Ctrl.IsCalibrating);
    }

    [Fact]
    public async Task BackDuringCoupling_AbortsSequence_CancelsTachMapping_NeverCalibrates()
    {
        CouplingHarness h = MakeCouplingController();
        FanReading[] fans = OneFan();
        h.Ctrl.Apply(MakeSnapshot(fans: fans));
        h.Ctrl.NextCommand.Execute(null); // → Kalibrier-Schritt

        Task calTask = h.Ctrl.CalibrateAllCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => Count(h.TachStarts) == 1, 2000));

        await h.Ctrl.BackCommand.ExecuteAsync(null); // bricht die laufende Kopplung/Sequenz ab
        await calTask;

        Assert.Empty(Snap(h.CalStarts));       // nie kalibriert
        Assert.True(Count(h.TachCancels) >= 1); // Kopplung abgebrochen
        Assert.False(h.Ctrl.IsCalibrating);
        Assert.Equal(OnboardingStep.Welcome, h.Ctrl.CurrentStep);
    }

    [Fact]
    public async Task NoTachDelegate_FallsBackToDirectCalibration()
    {
        // Ohne verdrahteten Kopplungs-Pfad bleibt das Alt-Verhalten: direkt kalibrieren (kein Koppeln).
        var (ctrl, _) = MakeController();
        FanReading[] fans = OneFan();
        ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);
        Assert.Equal(OnboardingCalibrationState.Running, ctrl.ControllableFans[0].CalibrationState); // sofort kalibrieren

        ctrl.Apply(MakeSnapshot(fans: fans, calibration: CalDone("f1")));
        await calTask;

        Assert.Equal(OnboardingCalibrationState.Done, ctrl.ControllableFans[0].CalibrationState);
    }
}
