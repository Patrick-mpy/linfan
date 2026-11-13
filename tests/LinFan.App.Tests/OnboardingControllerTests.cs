// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Localization;
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
            Connected: true,
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
        new("hwmon0/pwm1", "CPU Fan", 1200, 128, CanControl: true),
        new("hwmon0/pwm2", "Case Fan", 900, 100, CanControl: true),
        new("hwmon0/pwm3", "Read-Only Fan", 600, 80, CanControl: false),
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
        Assert.DoesNotContain(ctrl.ControllableFans, f => f.FanId == "hwmon0/pwm3");
    }

    [Fact]
    public void Apply_FirstCall_PopulatesAllFans_IncludingReadOnly()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());

        // Positions-Liste umfasst ALLE Lüfter (auch read-only), die Kalibrier-Liste nur steuerbare.
        Assert.Equal(3, ctrl.Fans.Count);
        Assert.Equal(2, ctrl.ControllableFans.Count);
        Assert.Contains(ctrl.Fans, f => f.FanId == "hwmon0/pwm3");
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

        OnboardingFanRow controllable = ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1");
        OnboardingFanRow readOnly = ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm3");
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

        // Finish no longer closes - it navigates to the Done receipt page; Close ends the wizard.
        Assert.False(closeCalled);
        Assert.Equal(OnboardingStep.Done, ctrl.CurrentStep);

        ctrl.CloseCommand.Execute(null);
        Assert.True(closeCalled);
    }

    // --- Fan names: an own name typed in the devices step, „Lüfter N" for an empty field ------------------------------

    [Fact]
    public async Task Finish_SavesTheTypedName_AndLüfterNForAnEmptyField()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig
        {
            Fans =
            [
                new FanConfig { FanId = "hwmon0/pwm2", Name = "Heck" },                // an own name is prefilled
                new FanConfig { FanId = "hidden", Name = "Lüfter 1", Hidden = true }, // no row, but its name is taken
            ],
        }));
        OnboardingFanRow cpu = ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1");
        OnboardingFanRow back = ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm2");
        OnboardingFanRow readOnly = ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm3");
        Assert.Equal(("", "Lüfter 2"), (cpu.Name, cpu.DefaultName));
        Assert.Equal("Heck", back.Name);

        cpu.Name = "  CPU-Kühler ";
        readOnly.Name = " ";
        await ctrl.FinishCommand.ExecuteAsync(null);

        Dictionary<string, string> names = Assert.Single(sent).Fans.ToDictionary(f => f.FanId, f => f.Name);
        Assert.Equal("CPU-Kühler", names["hwmon0/pwm1"]);
        Assert.Equal("Heck", names["hwmon0/pwm2"]);
        Assert.Equal("Lüfter 2", names["hwmon0/pwm3"]); // the lowest number „CPU-Kühler" left free
        Assert.Equal("Lüfter 1", names["hidden"]);
    }

    // A hidden fan has no row, but takes its number all the same - the settings and the overview count it.
    [Fact]
    public void AHiddenFanWithoutAName_KeepsItsNumber()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig
        {
            Fans = [new FanConfig { FanId = "hwmon0/pwm1", Name = "", Hidden = true }],
        }));

        Assert.Equal(new[] { "Lüfter 2", "Lüfter 3" }, ctrl.Fans.Select(f => f.DisplayName));
    }

    [Fact]
    public void TypingANameThatAPlaceholderShowed_MovesThatPlaceholderOn()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());
        Assert.Equal(new[] { "Lüfter 1", "Lüfter 2", "Lüfter 3" }, ctrl.Fans.Select(f => f.DisplayName));

        ctrl.Fans[2].Name = "Lüfter 1";

        Assert.Equal(new[] { "Lüfter 2", "Lüfter 3", "Lüfter 1" }, ctrl.Fans.Select(f => f.DisplayName));
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
            fans: [new FanReading("f1", "Fan", 1200, 128, CanControl: true)],
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
            fans: [new FanReading("f1", "Case Fan", 900, 100, CanControl: true)]));

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
        // steuerbaren zu - read-only Kanäle bleiben ohne Zuordnung.
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
            fans: [new FanReading("f1", "CPU Fan", 1200, 128, CanControl: true)],
            config: new AppConfig { Fans = [existing] }));

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        FanConfig fan = cfg.Fans.Single(f => f.FanId == "f1");
        Assert.Equal(42, fan.MinPwm);
        Assert.Equal(230, fan.MaxPwm);
    }

    // ---------------------------------------------------------------------------
    // Airflow mode in the profile step: positions → role-based profiles (fallback = primary sensor)
    // ---------------------------------------------------------------------------

    [Fact]
    public void EnterChooseProfile_WithRoleLocation_EnablesAirflowMode_AndFillsSummary()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);

        ctrl.CurrentStep = OnboardingStep.ChooseProfile;

        Assert.True(ctrl.UseAirflowProfiles);
        // A CPU cooler is no case fan: the verdict only says there is no balance, one hint says why.
        Assert.Equal("Druckbilanz: Nicht bestimmbar.", ctrl.AirflowPressureText);
        Assert.Single(ctrl.AirflowHints, h => h.Contains("Gehäuselüfter", StringComparison.Ordinal));
        // Summary lists only controllable fans (pwm1, pwm2) - the read-only pwm3 gets no assignment.
        Assert.Equal(new[] { "hwmon0/pwm1", "hwmon0/pwm2" }, ctrl.AirflowSummary.Select(r => r.FanId));
    }

    [Fact]
    public void EnterChooseProfile_WithoutRoleLocations_StaysInFallbackMode()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot()); // all locations Unspecified

        ctrl.CurrentStep = OnboardingStep.ChooseProfile;

        Assert.False(ctrl.UseAirflowProfiles);
        Assert.Empty(ctrl.AirflowSummary);
        Assert.Equal("", ctrl.AirflowPressureText);
    }

    [Fact]
    public async Task Finish_AirflowMode_BuildsThreeAirflowProfiles_AssignsByRole()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot());
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm2").Location = FanLocationOption.For(FanLocation.CaseRearExhaust);

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal(new[] { "silent", "balanced", "performance" }, cfg.Profiles.Select(p => p.Id));
        Assert.All(cfg.Profiles, p =>
        {
            Assert.Contains(p.Curves, c => c.Id == "airflow-cpu");
            Assert.Contains(p.Curves, c => c.Id == "airflow-exhaust");
            Assert.Equal("airflow-cpu", p.Assignments.Single(a => a.FanId == "hwmon0/pwm1").CurveId);
            Assert.Equal("airflow-exhaust", p.Assignments.Single(a => a.FanId == "hwmon0/pwm2").CurveId);
            // read-only pwm3 gets no assignment in any profile
            Assert.DoesNotContain(p.Assignments, a => a.FanId == "hwmon0/pwm3");
        });
        // ProfileService.Apply materialized the balanced default into the fans.
        Assert.Equal("airflow-cpu", cfg.Fans.Single(f => f.FanId == "hwmon0/pwm1").AssignedCurveId);
        Assert.Equal("airflow-exhaust", cfg.Fans.Single(f => f.FanId == "hwmon0/pwm2").AssignedCurveId);
        Assert.Null(cfg.Fans.Single(f => f.FanId == "hwmon0/pwm3").AssignedCurveId);
    }

    [Fact]
    public async Task Finish_AirflowMode_DoesNotRequirePrimarySensor()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot());
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);
        ctrl.SelectedPrimarySensor = null;

        await ctrl.FinishCommand.ExecuteAsync(null);

        Assert.Single(sent); // sent despite missing primary sensor - sources come from the heuristic
    }

    [Fact]
    public async Task Finish_AirflowMode_AllSensorsHidden_BlocksWithMessage()
    {
        // With every temperature sensor hidden the role curves would get empty source lists and
        // silently regulate nothing - Finish must block like the fallback branch does.
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot());
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);
        foreach (SensorOption sensor in ctrl.TemperatureSensors)
            sensor.Visible = false;

        await ctrl.FinishCommand.ExecuteAsync(null);

        Assert.Empty(sent);
        Assert.False(string.IsNullOrEmpty(ctrl.StatusMessage));
        Assert.NotEqual(OnboardingStep.Done, ctrl.CurrentStep);
    }

    [Fact]
    public void CanFinish_TracksModeAndSensor()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());

        Assert.True(ctrl.CanFinish); // fallback mode with auto-picked primary sensor

        ctrl.SelectedPrimarySensor = null;
        Assert.False(ctrl.CanFinish); // fallback mode without sensor

        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);
        ctrl.CurrentStep = OnboardingStep.ChooseProfile; // gate re-evaluates on step entry
        Assert.True(ctrl.CanFinish); // airflow mode needs no sensor
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

        // Done is a receipt page reachable only via a successful Finish - Next stops here.
        ctrl.NextCommand.Execute(null);
        Assert.Equal(OnboardingStep.ChooseProfile, ctrl.CurrentStep);
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
        bool closeCalled = false;
        var (ctrl, sent) = MakeController(onClose: () => closeCalled = true);
        ctrl.Apply(MakeSnapshot(config: new AppConfig { Fans = [new FanConfig { FanId = "f1", Name = "Fan" }] }));

        await ctrl.FinishCommand.ExecuteAsync(null);
        // Das Schließen des Fensters (X auf der Done-Seite) ruft OnClosing → SkipCommand; der Latch muss
        // das Senden unterdrücken, sonst überschriebe eine profillose Config die gesendeten Profile -
        // aber der Wizard muss trotzdem sauber schließen (Localizer-Abo lösen, Besitzer zurücksetzen).
        await ctrl.SkipCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);  // nur EINE Sendung (Finish)
        Assert.Equal(3, cfg.Profiles.Count);  // und es ist die Profil-Config, nicht profillos
        Assert.True(closeCalled);             // Fenster-X auf Done schließt trotzdem
    }

    [Fact]
    public async Task Finish_PopulatesDoneSummary()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot()); // 2 controllable fans (pwm1, pwm2) + 1 read-only, no calibration run
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);

        await ctrl.FinishCommand.ExecuteAsync(null);

        Assert.Equal(OnboardingStep.Done, ctrl.CurrentStep);
        Assert.Contains("0", ctrl.DoneCalibrationSummary);  // nothing calibrated in this run
        Assert.Contains("2", ctrl.DoneCalibrationSummary);  // of 2 controllable fans
        Assert.Contains("1", ctrl.DonePositionsSummary);    // one position assigned (of 3 fans)
        Assert.Contains("3", ctrl.DonePositionsSummary);
        Assert.False(string.IsNullOrWhiteSpace(ctrl.DoneProfileSummary));
    }

    // ---------------------------------------------------------------------------
    // Kalibrier-Fortschritt: zeigt den Anzeigenamen, nicht die Hardware-Id
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CalibrateAll_DoneMessage_ShowsFanDisplayName_NotHardwareId()
    {
        var (ctrl, _) = MakeController();
        // Without an own name the fan is „Lüfter 1" - the message names it so, not by its hardware id.
        var fans = new[] { new FanReading("hwmon8/pwm1", "thinkpad pwm1", 0, 0, CanControl: true) };
        ctrl.Apply(MakeSnapshot(fans: fans));

        // Kalibrierung starten - blockiert bei „warte auf Done", bis wir unten einen Done-Snapshot einspielen.
        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);

        var done = new CalibrationStatus("hwmon8/pwm1", CalibrationPhase.Done, 0, 0, Running: false, Done: true, StartPwm: 96, FailReason: null);
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: done));

        await calTask;

        Assert.Contains("Lüfter 1", ctrl.CalibrationProgress);
        Assert.DoesNotContain("hwmon8/pwm1", ctrl.CalibrationProgress);
    }

    [Fact]
    public async Task CalibrateAll_ErrorMessage_ShowsFanDisplayName_NotHardwareId()
    {
        var (ctrl, _) = MakeController();
        var fans = new[] { new FanReading("hwmon8/pwm1", "thinkpad pwm1", 0, 0, CanControl: true) };
        ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);

        var failed = new CalibrationStatus("hwmon8/pwm1", CalibrationPhase.Failed, 0, 0, Running: false, Done: false, StartPwm: null,
            FailReason: CalibrationFailReason.OverTemperature, OverTempC: 95, OverLimitC: 90);
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: failed));

        await calTask;

        Assert.Contains("Lüfter 1", ctrl.CalibrationProgress);
        Assert.DoesNotContain("hwmon8/pwm1", ctrl.CalibrationProgress);
    }

    // ---------------------------------------------------------------------------
    // Kalibrier-Fortschritt: strukturierte Anzeige (Index/Name/Balken + Pro-Lüfter-Status)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CalibrateAll_TracksPerFanAndOverallProgress()
    {
        var (ctrl, _) = MakeController();
        var fans = new[] { new FanReading("hwmon8/pwm1", "thinkpad pwm1", 0, 0, CanControl: true) };
        ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);

        // Sequenz-Kopf ist sofort (vor dem ersten Snapshot) gesetzt.
        Assert.Equal(1, ctrl.CalibrationFanCount);
        Assert.Equal(1, ctrl.CalibrationFanIndex);
        Assert.Equal("Lüfter 1", ctrl.CalibrationFanName);
        Assert.Equal("Lüfter 1 von 1: Lüfter 1", ctrl.CalibrationHeadline);
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
            new FanReading("f1", "Fan A", 0, 0, CanControl: true),
            new FanReading("f2", "Fan B", 0, 0, CanControl: true),
        };
        ctrl.Apply(MakeSnapshot(fans: fans));

        Task calTask = ctrl.CalibrateAllCommand.ExecuteAsync(null);

        Assert.Equal(2, ctrl.CalibrationFanCount);
        Assert.Equal(1, ctrl.CalibrationFanIndex);
        Assert.Equal("Lüfter 1", ctrl.CalibrationFanName);
        Assert.Equal(OnboardingCalibrationState.Running, ctrl.ControllableFans[0].CalibrationState);

        // Fan A fertig → Sequenz rückt (asynchron) auf Fan B vor.
        ctrl.Apply(MakeSnapshot(fans: fans, calibration: new CalibrationStatus("f1", CalibrationPhase.Done, 0, 0, false, true, 96, null)));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref startCount) == 2, 2000), "Start für Fan B kam nicht");

        Assert.Equal(2, ctrl.CalibrationFanIndex);
        Assert.Equal("Lüfter 2", ctrl.CalibrationFanName);
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
        var fans = new[] { new FanReading("hwmon8/pwm1", "thinkpad pwm1", 0, 0, CanControl: true) };
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

    /// <summary>The assistant opened because the daemon set the config aside: the welcome page says where it went.</summary>
    [Fact]
    public void Apply_ConfigSetAside_ShowsWhereItWent_ForTheAssistantsLifetime()
    {
        OnboardingController ctrl = MakeControllerWithManual(new());
        Assert.False(ctrl.HasConfigSetAsideNote);
        Assert.Null(ctrl.ConfigSetAsideNote);

        ctrl.Apply(MakeSnapshot() with
        {
            ConfigProblem = ConfigLoadProblem.SetAside,
            ConfigSetAsidePath = "/etc/linfan/config.json.invalid",
        });
        Assert.Contains("/etc/linfan/config.json.invalid", ctrl.ConfigSetAsideNote);

        ctrl.Apply(MakeSnapshot()); // a calibration saved meanwhile: the daemon dropped the report
        Assert.True(ctrl.HasConfigSetAsideNote);
    }

    /// <summary>A dry-run daemon refuses calibration and identification: the assistant does not wait for results.</summary>
    [Fact]
    public void Apply_DryRun_LocksCalibrationAndIdentify()
    {
        OnboardingController ctrl = MakeControllerWithManual(new());

        ctrl.Apply(MakeSnapshot() with { DryRun = true });

        Assert.True(ctrl.IsDryRun);
        Assert.False(ctrl.CalibrateAllCommand.CanExecute(null));
        OnboardingFanRow fan = ctrl.ControllableFans.First(f => f.FanId == "hwmon0/pwm1");
        Assert.False(fan.IdentifyCommand.CanExecute(null));
        Assert.StartsWith("Im Dry-Run nicht möglich", fan.IdentifyTooltip);

        ctrl.Apply(MakeSnapshot());
        Assert.True(ctrl.CalibrateAllCommand.CanExecute(null));
        Assert.True(fan.IdentifyCommand.CanExecute(null));
    }

    /// <summary>Offline the daemon drops the commands - the sequence would wait 60 s per fan for nothing.</summary>
    [Fact]
    public void Apply_Offline_LocksCalibration_Identify_AndTheFanTest()
    {
        OnboardingController ctrl = MakeControllerWithManual(new());

        ctrl.Apply(MakeSnapshot() with { Connected = false });

        Assert.False(ctrl.CalibrateAllCommand.CanExecute(null));
        OnboardingFanRow fan = ctrl.ControllableFans.First(f => f.FanId == "hwmon0/pwm1");
        Assert.False(fan.IdentifyCommand.CanExecute(null));
        Assert.True(fan.Manual.EngageBlocked);
        Assert.Equal("Gesperrt - der Hintergrunddienst ist nicht erreichbar.", fan.IdentifyTooltip);
    }

    [Fact]
    public void Apply_DriverLockedFans_NameTheFix()
    {
        OnboardingController ctrl = MakeControllerWithManual(new());
        Assert.False(ctrl.DriverLocked);

        ctrl.Apply(MakeSnapshot(fans: new FanReading[]
        {
            new("thinkpad/pwm1", "Fan", 2100, 0, CanControl: false, ReadOnlyReason: FanReadOnlyReason.DriverLocked),
        }));

        Assert.True(ctrl.DriverLocked);
        Assert.False(ctrl.HasControllableFans);
    }

    [Fact]
    public void Apply_FeedsLiveRpmIntoManualControl()
    {
        OnboardingController ctrl = MakeControllerWithManual(new());

        ctrl.Apply(MakeSnapshot()); // CPU Fan @ 1200 RPM (steuerbar)

        OnboardingFanRow fan = ctrl.ControllableFans.First(f => f.FanId == "hwmon0/pwm1");
        Assert.Equal("1200 RPM", fan.Manual.LiveRpm);
    }

    [Theory]
    [InlineData(FanControlState.Curve, 50.0)]
    [InlineData(FanControlState.HardwareAuto, null)] // the reported PWM is not what the fan runs at
    public void Apply_FeedsTheDaemonDrivenPowerIntoManualControl(FanControlState state, double? expected)
    {
        OnboardingController ctrl = MakeControllerWithManual(new());

        ctrl.Apply(MakeSnapshot(fans: [new FanReading("hwmon0/pwm1", "CPU Fan", 1200, 128, true, ControlState: state)]));

        Assert.Equal(expected, ctrl.ControllableFans.Single().Manual.LivePercent);
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
    // Hidden devices: primary-sensor combo/heuristic filter + hidden fans stay out of the wizard
    // ---------------------------------------------------------------------------

    [Fact]
    public void SelectPrimarySensorId_SkipsHiddenSensors()
    {
        var sensors = new[]
        {
            new SensorOption("cpu/temp1", "cpu package", visible: false), // hidden CPU match
            new SensorOption("mb/temp1", "Motherboard"),
        };
        var readings = new[]
        {
            new SensorReading("cpu/temp1", "cpu package", SensorKind.Temperature, "°C", 70),
            new SensorReading("mb/temp1", "Motherboard", SensorKind.Temperature, "°C", 40),
        };

        Assert.Equal("mb/temp1", OnboardingController.SelectPrimarySensorId(sensors, readings));
    }

    [Fact]
    public void SelectPrimarySensorId_AllHidden_FallsBackToAll()
    {
        var sensors = new[]
        {
            new SensorOption("cpu/temp1", "cpu package", visible: false),
            new SensorOption("mb/temp1", "Motherboard", visible: false),
        };

        // Everything hidden → fall back so Finish cannot dead-end without a primary sensor.
        Assert.Equal("cpu/temp1", OnboardingController.SelectPrimarySensorId(sensors, Array.Empty<SensorReading>()));
    }

    [Fact]
    public void Apply_ExcludesHiddenSensor_FromPrimarySensorChoices()
    {
        (OnboardingController ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp2", Name = "GPU Temp", Hidden = true } },
        }));

        Assert.Equal(2, ctrl.TemperatureSensors.Count); // visibility checkbox list keeps everything
        Assert.Equal("hwmon0/temp1", Assert.Single(ctrl.PrimarySensorChoices).Id);
    }

    [Fact]
    public void SensorVisibilityToggle_UpdatesPrimarySensorChoices_Live()
    {
        (OnboardingController ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());
        Assert.Equal(2, ctrl.PrimarySensorChoices.Count);

        SensorOption gpu = ctrl.TemperatureSensors.First(s => s.Id == "hwmon0/temp2");
        gpu.Visible = false;
        Assert.Equal("hwmon0/temp1", Assert.Single(ctrl.PrimarySensorChoices).Id);

        gpu.Visible = true; // re-inserted at its original position, not appended
        Assert.Equal(new[] { "hwmon0/temp1", "hwmon0/temp2" }, ctrl.PrimarySensorChoices.Select(s => s.Id));
    }

    [Fact]
    public void HidingSelectedPrimarySensor_KeepsItInChoices_UntilReplaced()
    {
        (OnboardingController ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());

        SensorOption cpu = ctrl.TemperatureSensors.First(s => s.Id == "hwmon0/temp1");
        SensorOption gpu = ctrl.TemperatureSensors.First(s => s.Id == "hwmon0/temp2");
        ctrl.SelectedPrimarySensor = gpu;

        gpu.Visible = false; // hidden but selected → stays offered (and selected)
        Assert.Contains(gpu, ctrl.PrimarySensorChoices);
        Assert.Same(gpu, ctrl.SelectedPrimarySensor);

        ctrl.SelectedPrimarySensor = cpu; // replacing the selection drops the hidden sensor
        Assert.DoesNotContain(gpu, ctrl.PrimarySensorChoices);
    }

    [Fact]
    public void PrimarySensorMenu_ListsTheChoices_UnderTheirGroups_WithLiveValues()
    {
        (OnboardingController ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU Package", Group = "CPU" },
                new SensorConfig { SensorId = "hwmon0/temp2", Name = "GPU Temp", Group = "GPU" },
            },
        }));

        ctrl.RefreshPrimarySensorMenu();

        Assert.Equal(new[] { "CPU", "CPU Package", "GPU", "GPU Temp" }, ctrl.PrimarySensorMenu.Select(m => m.Header));
        Assert.Equal(new[] { false, true, false, true }, ctrl.PrimarySensorMenu.Select(m => m.IsEnabled)); // headings are disabled
        MenuChoice cpu = ctrl.PrimarySensorMenu[1];
        Assert.True(cpu.IsCurrent); // the heuristic picked the CPU sensor
        Assert.Equal("55.0 °C", cpu.Detail);
    }

    [Fact]
    public void PrimarySensorMenu_Pick_SelectsTheSensor_AndLeavesHiddenOnesOut()
    {
        (OnboardingController ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());
        SensorOption gpu = ctrl.TemperatureSensors.First(s => s.Id == "hwmon0/temp2");

        ctrl.RefreshPrimarySensorMenu();
        ctrl.PrimarySensorMenu.Single(m => m.Header == "GPU Temp").Command!.Execute(null);

        Assert.Same(gpu, ctrl.SelectedPrimarySensor);
        Assert.Equal("Primärer Temperatursensor: GPU Temp", ctrl.PrimarySensorChipName);

        ctrl.TemperatureSensors.First(s => s.Id == "hwmon0/temp1").Visible = false;
        ctrl.RefreshPrimarySensorMenu();
        Assert.DoesNotContain(ctrl.PrimarySensorMenu, m => m.Header == "CPU Package");
    }

    [Fact]
    public void Apply_KeepsTheSensorLiveValues_Current()
    {
        (OnboardingController ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot());

        ctrl.Apply(MakeSnapshot(sensors:
        [
            new("hwmon0/temp1", "CPU Package", SensorKind.Temperature, "°C", 71.5),
            new("hwmon0/temp2", "GPU Temp", SensorKind.Temperature, "°C", double.NaN),
        ]));

        Assert.Equal("71.5 °C", ctrl.TemperatureSensors.First(s => s.Id == "hwmon0/temp1").LiveValue);
        Assert.Equal("n/a", ctrl.TemperatureSensors.First(s => s.Id == "hwmon0/temp2").LiveValue);
    }

    [Fact]
    public async Task Finish_KeepsTheCaseTypeOfTheConfig()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig { CaseType = CaseType.Server }));

        Assert.Equal(CaseType.Server, ctrl.CaseType);
        await ctrl.FinishCommand.ExecuteAsync(null);

        Assert.Equal(CaseType.Server, Assert.Single(sent).CaseType);
    }

    [Fact]
    public async Task ALocationPick_SetsTheCaseType_ForFinishAndTheNextDialog()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot());
        OnboardingFanRow row = ctrl.Fans.First(f => f.FanId == "hwmon0/pwm2");

        ctrl.ApplyLocationPick(row, new LocationPick(FanLocation.FanWall, CaseType.Server));

        Assert.Equal(CaseType.Server, ctrl.CreateLocationPick(ctrl.Fans.First()).CaseType);
        await ctrl.FinishCommand.ExecuteAsync(null);
        AppConfig cfg = Assert.Single(sent);
        Assert.Equal(CaseType.Server, cfg.CaseType);
        Assert.Equal(FanLocation.FanWall, cfg.Fans.Single(f => f.FanId == "hwmon0/pwm2").Location);
    }

    [Fact]
    public void Apply_InitialPrimarySelection_IgnoresHiddenCpuSensor()
    {
        (OnboardingController ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU Package", Hidden = true } },
        }));

        Assert.Equal("hwmon0/temp2", ctrl.SelectedPrimarySensor?.Id);
    }

    [Fact]
    public void Apply_ExcludesHiddenFans_FromPositionAndCalibrationLists()
    {
        (OnboardingController ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "hwmon0/pwm2", Name = "Case Fan", Hidden = true } },
        }));

        Assert.DoesNotContain(ctrl.Fans, f => f.FanId == "hwmon0/pwm2");
        Assert.Equal("hwmon0/pwm1", Assert.Single(ctrl.ControllableFans).FanId);
        Assert.Contains(ctrl.Fans, f => f.FanId == "hwmon0/pwm3"); // read-only but visible stays listed
    }

    [Fact]
    public async Task Finish_KeepsHiddenFanConfig_NoDataLoss()
    {
        var hidden = new FanConfig
        {
            FanId = "hwmon0/pwm2",
            Name = "Ghost",
            Hidden = true,
            Location = FanLocation.CaseFrontIntake,
            MinPwm = 30,
            MaxPwm = 200,
            Calibration = new FanCalibration { StartPwm = 40, MinRpm = 500, MaxRpm = 1800 },
        };
        (OnboardingController ctrl, List<AppConfig> sent) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig { Fans = new[] { hidden } }));

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig result = Assert.Single(sent);
        FanConfig kept = result.Fans.Single(f => f.FanId == "hwmon0/pwm2");
        Assert.True(kept.Hidden);
        Assert.Equal("Ghost", kept.Name);
        Assert.Equal(FanLocation.CaseFrontIntake, kept.Location);
        Assert.Equal((byte)30, kept.MinPwm);
        Assert.Equal((byte)200, kept.MaxPwm);
        Assert.Equal((byte)40, kept.Calibration?.StartPwm);
    }

    [Fact]
    public async Task Finish_NullsDanglingCurveId_OnHiddenFan()
    {
        var hidden = new FanConfig
        {
            FanId = "hwmon0/pwm2",
            Name = "Ghost",
            Hidden = true,
            AssignedCurveId = "old-curve",
        };
        (OnboardingController ctrl, List<AppConfig> sent) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig { Fans = new[] { hidden } }));

        await ctrl.FinishCommand.ExecuteAsync(null);

        // Finish replaces the whole curve set → "old-curve" no longer exists. The persisted config
        // must not keep the dangling reference: null = hardware auto, a regular state. (The
        // ControlLoop additionally treats dangling ids like null at runtime - defense in depth.)
        AppConfig result = Assert.Single(sent);
        Assert.Null(result.Fans.Single(f => f.FanId == "hwmon0/pwm2").AssignedCurveId);
    }

    [Fact]
    public async Task Finish_KeepsConfigOfUndiscoveredSensor()
    {
        // A config entry whose sensor is missing from the current discovery (unplugged, EIO) has no
        // wizard row - Finish must carry it over verbatim instead of dropping name/group/visibility.
        var gone = new SensorConfig { SensorId = "gone/temp1", Name = "USB Probe", Group = "Extern", Hidden = true };
        (OnboardingController ctrl, List<AppConfig> sent) = MakeController();
        ctrl.Apply(MakeSnapshot(config: new AppConfig { Sensors = new[] { gone } }));

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig result = Assert.Single(sent);
        SensorConfig kept = result.Sensors.Single(s => s.SensorId == "gone/temp1");
        Assert.Equal("USB Probe", kept.Name);
        Assert.Equal("Extern", kept.Group);
        Assert.True(kept.Hidden);
    }

    [Fact]
    public async Task WindowClose_DuringFinishSend_DoesNotSendProfilelessConfig()
    {
        // Closing the window while Finish's send is in flight runs OnClosing→Skip. The latch must
        // already be set, otherwise Skip races a profile-less config against the profile config.
        var sent = new List<AppConfig>();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool closeCalled = false;
        var ctrl = new OnboardingController(
            sendStartCalibration: _ => Task.CompletedTask,
            sendCancelCalibration: () => Task.CompletedTask,
            sendConfig: cfg => { sent.Add(cfg); return gate.Task; },
            onClose: () => closeCalled = true);
        ctrl.Apply(MakeSnapshot(config: new AppConfig { Fans = [new FanConfig { FanId = "f1", Name = "Fan" }] }));

        Task finish = ctrl.FinishCommand.ExecuteAsync(null); // blocks on the in-flight send
        await ctrl.SkipCommand.ExecuteAsync(null);           // window-X while sending

        gate.SetResult(true);
        await finish;

        AppConfig cfg = Assert.Single(sent);   // only Finish's config went out
        Assert.Equal(3, cfg.Profiles.Count);
        Assert.True(closeCalled);              // and the wizard still tore down
    }

    [Fact]
    public async Task Finish_SendFails_ReArmsLatch_SoSkipStillSends()
    {
        var results = new Queue<bool>([false, true]);
        var sent = new List<AppConfig>();
        var ctrl = new OnboardingController(
            sendStartCalibration: _ => Task.CompletedTask,
            sendCancelCalibration: () => Task.CompletedTask,
            sendConfig: cfg => { sent.Add(cfg); return Task.FromResult(results.Dequeue()); },
            onClose: () => { });
        ctrl.Apply(MakeSnapshot(config: new AppConfig { Fans = [new FanConfig { FanId = "f1", Name = "Fan" }] }));

        await ctrl.FinishCommand.ExecuteAsync(null); // save fails → stays on the profile step
        Assert.NotEqual(OnboardingStep.Done, ctrl.CurrentStep);
        Assert.False(string.IsNullOrEmpty(ctrl.StatusMessage));

        await ctrl.SkipCommand.ExecuteAsync(null);   // latch was re-armed → Skip can still persist

        Assert.Equal(2, sent.Count);
        Assert.True(sent[^1].OnboardingCompleted);
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
        [new("f1", "Fan A", 0, 0, CanControl: true)];

    /// <summary>Ein Lüfter, der bereits eine positive Live-Drehzahl liefert → ein brauchbarer Tacho ist vorhanden.</summary>
    private static FanReading[] OneFanWithTacho(double rpm = 1500) =>
        [new("f1", "Fan A", rpm, 0, CanControl: true)];

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
            new("f1", "Fan A", 0, 0, CanControl: true),
            new("f2", "Fan B", 0, 0, CanControl: true),
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

    // ---------------------------------------------------------------------------
    // Step indicator
    // ---------------------------------------------------------------------------

    [Fact]
    public void ShowStepper_HiddenOnWelcomeOnly()
    {
        var (ctrl, _) = MakeController();
        var changed = new List<string?>();
        ctrl.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(ctrl.ShowStepper); // the welcome page is not one of the four steps

        ctrl.NextCommand.Execute(null);

        Assert.True(ctrl.ShowStepper);
        Assert.Contains(nameof(OnboardingController.ShowStepper), changed);
        Assert.Equal(
            new[] { "1 · Kalibrierung", "2 · Geräte", "3 · Profil", "4 · Fertig" },
            ctrl.Steps.Select(s => s.Label));
    }

    [Theory]
    [InlineData(OnboardingStep.Welcome, "----")]
    [InlineData(OnboardingStep.Calibration, "C---")]
    [InlineData(OnboardingStep.Devices, "DC--")]
    [InlineData(OnboardingStep.ChooseProfile, "DDC-")]
    [InlineData(OnboardingStep.Done, "DDDC")]
    public void Steps_MarkDoneAndCurrent_PerStep(OnboardingStep step, string expected)
    {
        var (ctrl, _) = MakeController();

        ctrl.CurrentStep = step;

        // D = done, C = current, - = still ahead; never both done and current.
        string actual = string.Concat(ctrl.Steps.Select(s =>
            s.IsDone && s.IsCurrent ? '!' : s.IsDone ? 'D' : s.IsCurrent ? 'C' : '-'));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Steps_FollowBackNavigation()
    {
        var (ctrl, _) = MakeController();
        ctrl.CurrentStep = OnboardingStep.ChooseProfile;

        ctrl.CurrentStep = OnboardingStep.Devices; // what Back does, without the calibration abort

        Assert.Equal(new[] { true, false, false, false }, ctrl.Steps.Select(s => s.IsDone));
        Assert.Equal(new[] { false, true, false, false }, ctrl.Steps.Select(s => s.IsCurrent));
    }

    // ---------------------------------------------------------------------------
    // Sensor groups of the devices step
    // ---------------------------------------------------------------------------

    private static SensorReading Temp(string id, string name, double value = 40) =>
        new(id, name, SensorKind.Temperature, "°C", value);

    private static IEnumerable<string> GroupNames(OnboardingController ctrl) =>
        ctrl.TemperatureSensorGroups.Select(g => g.Name);

    private static IEnumerable<string> Members(OnboardingController ctrl, string group) =>
        ctrl.TemperatureSensorGroups.Single(g => g.Name == group).Sensors.Select(s => s.Id);

    [Fact]
    public void SensorGroups_SuggestedByClassifier_InCategoryOrder()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(sensors:
        [
            Temp("nvme@nvme0/temp1", "Composite"),
            Temp("iwlwifi_1/temp1", "WLAN"),
            Temp("k10temp/temp1", "Tctl"),
            Temp("nct6798/temp2", "SYSTIN"),
            Temp("amdgpu@0000:03:00.0/temp1", "edge"),
            Temp("k10temp/temp3", "Tccd1"),
            new("k10temp/fan1", "Fan", SensorKind.FanRpm, "RPM", 900), // not a temperature: no row
        ]));

        // SensorCategory declaration order, not discovery order; within a group discovery order.
        Assert.Equal(new[] { "CPU", "GPU", "Speicher", "Mainboard", "Sonstige" }, GroupNames(ctrl));
        Assert.Equal(new[] { "k10temp/temp1", "k10temp/temp3" }, Members(ctrl, "CPU"));
        Assert.Equal(new[] { "iwlwifi_1/temp1" }, Members(ctrl, "Sonstige"));
        // The group rows are the wizard's sensor rows - one visibility for both.
        Assert.Same(ctrl.TemperatureSensors.Single(s => s.Id == "k10temp/temp1"),
                    ctrl.TemperatureSensorGroups[0].Sensors[0]);
    }

    [Fact]
    public void SensorGroups_ExistingGroupWins_OverSuggestion()
    {
        // Re-run of the wizard: a sensor that already has a group stays in it, even if the classifier
        // would put it elsewhere; the configured list orders the groups.
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(
            sensors: [Temp("k10temp/temp1", "Tctl"), Temp("k10temp/temp3", "Tccd1"), Temp("nvme@nvme0/temp1", "SSD")],
            config: new AppConfig
            {
                Sensors = [new SensorConfig { SensorId = "k10temp/temp3", Name = "Tccd1", Group = "Prozessor" }],
                SensorGroups = ["Prozessor"],
            }));

        Assert.Equal(new[] { "Prozessor", "CPU", "Speicher" }, GroupNames(ctrl));
        Assert.Equal(new[] { "k10temp/temp3" }, Members(ctrl, "Prozessor"));
        Assert.Equal(new[] { "k10temp/temp1" }, Members(ctrl, "CPU"));
    }

    [Fact]
    public void SensorGroups_SuggestionJoinsExistingGroup_CaseInsensitively()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(
            sensors: [Temp("k10temp/temp1", "Tctl"), Temp("k10temp/temp3", "Tccd1")],
            config: new AppConfig
            {
                Sensors = [new SensorConfig { SensorId = "k10temp/temp1", Name = "Tctl", Group = "cpu" }],
                SensorGroups = ["cpu"],
            }));

        OnboardingSensorGroup group = Assert.Single(ctrl.TemperatureSensorGroups);
        Assert.Equal("cpu", group.Name); // the configured spelling names the group
        Assert.Equal(new[] { "k10temp/temp1", "k10temp/temp3" }, group.Sensors.Select(s => s.Id));
    }

    [Fact]
    public void HasSuggestedGroups_OnlyWhenASensorHadNoGroup()
    {
        var (fresh, _) = MakeController();
        fresh.Apply(MakeSnapshot(sensors: [Temp("k10temp/temp1", "Tctl")]));
        Assert.True(fresh.HasSuggestedGroups);

        // Re-run where every sensor already has its group: nothing was suggested, so no hint.
        var (rerun, _) = MakeController();
        rerun.Apply(MakeSnapshot(
            sensors: [Temp("k10temp/temp1", "Tctl")],
            config: new AppConfig
            {
                Sensors = [new SensorConfig { SensorId = "k10temp/temp1", Name = "Tctl", Group = "CPU" }],
                SensorGroups = ["CPU"],
            }));
        Assert.False(rerun.HasSuggestedGroups);
        Assert.Single(rerun.TemperatureSensorGroups);
    }

    [Fact]
    public void SensorGroups_UnrecognizedDriver_GoesToOther()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot()); // hwmon0/temp1, hwmon0/temp2 - no recognizable chip

        OnboardingSensorGroup group = Assert.Single(ctrl.TemperatureSensorGroups);
        Assert.Equal("Sonstige", group.Name);
        Assert.Equal(2, group.Sensors.Count);
    }

    [Fact]
    public void SensorGroups_CountText_TracksVisibility()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(sensors:
            [Temp("k10temp/temp1", "Tctl"), Temp("k10temp/temp3", "Tccd1"), Temp("nvme@nvme0/temp1", "SSD")]));
        OnboardingSensorGroup cpu = ctrl.TemperatureSensorGroups.Single(g => g.Name == "CPU");
        var changed = new List<string?>();
        cpu.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("2 Sensoren", cpu.CountText);
        Assert.Equal("1 Sensor", ctrl.TemperatureSensorGroups.Single(g => g.Name == "Speicher").CountText);

        cpu.Sensors[1].Visible = false;

        Assert.Contains(nameof(OnboardingSensorGroup.CountText), changed);
        Assert.Equal("1 von 2 sichtbar", cpu.CountText);
    }

    [Fact]
    public void SensorGroups_LanguageSwitch_RenamesSuggestions()
    {
        var (ctrl, _) = MakeController();
        ctrl.Apply(MakeSnapshot(sensors:
            [Temp("k10temp/temp1", "Tctl"), Temp("amdgpu/temp1", "edge"), Temp("nvme@nvme0/temp1", "SSD")]));
        ctrl.CurrentStep = OnboardingStep.Devices;
        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);

            // CPU and GPU stay as they are in every language - the airflow analysis matches those words.
            Assert.Equal(new[] { "CPU", "GPU", "Storage" }, GroupNames(ctrl));
            Assert.Equal("2 · Devices", ctrl.Steps.Single(s => s.IsCurrent).Label);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German);
        }
        Assert.Equal(new[] { "CPU", "GPU", "Speicher" }, GroupNames(ctrl));
    }

    [Fact]
    public async Task Finish_WritesSuggestedGroups_OnlyForUngroupedSensors()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(
            sensors: [Temp("k10temp/temp1", "Tctl"), Temp("k10temp/temp3", "Tccd1"), Temp("iwlwifi_1/temp1", "WLAN")],
            config: new AppConfig
            {
                Sensors = [new SensorConfig { SensorId = "k10temp/temp3", Name = "Tccd1", Group = "Prozessor" }],
                SensorGroups = ["Prozessor"],
            }));

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal("Prozessor", cfg.Sensors.Single(s => s.SensorId == "k10temp/temp3").Group); // untouched
        Assert.Equal("CPU", cfg.Sensors.Single(s => s.SensorId == "k10temp/temp1").Group);
        Assert.Equal("Sonstige", cfg.Sensors.Single(s => s.SensorId == "iwlwifi_1/temp1").Group);
    }

    [Fact]
    public async Task Finish_MergesSensorGroups_WithoutReordering()
    {
        // Configured entries keep their place (also unused ones and ones a suggestion reuses), groups sensors
        // carry without the list naming them follow alphabetically, then the new suggestions in category order.
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(
            sensors:
            [
                Temp("nvme@nvme0/temp1", "SSD"),
                Temp("amdgpu@0000:03:00.0/temp1", "edge"),
                Temp("k10temp/temp1", "Tctl"),
                Temp("usb/temp1", "Probe B"),
                Temp("usb/temp2", "Probe A"),
            ],
            config: new AppConfig
            {
                Sensors =
                [
                    new SensorConfig { SensorId = "usb/temp1", Name = "Probe B", Group = "Zubehör" },
                    new SensorConfig { SensorId = "usb/temp2", Name = "Probe A", Group = "Extern" },
                ],
                SensorGroups = ["Zeta", "gpu", "Alpha"],
            }));

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal(new[] { "Zeta", "gpu", "Alpha", "Extern", "Zubehör", "CPU", "Speicher" }, cfg.SensorGroups);
        Assert.Equal("gpu", cfg.Sensors.Single(s => s.SensorId == "amdgpu@0000:03:00.0/temp1").Group);
        // The display shows the groups in the same order (only the ones with sensors).
        Assert.Equal(new[] { "gpu", "Extern", "Zubehör", "CPU", "Speicher" }, GroupNames(ctrl));
    }

    [Fact]
    public async Task Finish_GroupListStaysWithinLimit()
    {
        // A full list must not get the config refused: the new name is dropped from the list, the sensor
        // still carries it (a group the list lacks still exists).
        string[] full = Enumerable.Range(1, ConfigLimits.MaxSensorGroups).Select(i => $"G{i}").ToArray();
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(
            sensors: [Temp("k10temp/temp1", "Tctl")],
            config: new AppConfig { SensorGroups = full }));

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal(full, cfg.SensorGroups);
        Assert.Equal("CPU", Assert.Single(cfg.Sensors).Group);
        Assert.Null(ConfigLimits.Check(cfg));
    }

    // Windows (LibreHardwareMonitor) ids of an AMD CPU: both classify as CPU, only Tctl says so in its name.
    private const string WinTctl = "/amdcpu/0/temperature/2";
    private const string WinSoc = "/amdcpu/0/temperature/3";

    [Fact]
    public async Task Finish_AirflowMode_GroupOnlySensor_StaysOutWhenOthersMatchByName()
    {
        // Name before group: the suggested group "CPU" also lands on the SoC sensor, but the CPU curve averages
        // its sources - the SoC runs far below Tctl and would pull the curve down. With a sensor that names the
        // CPU, the group-only SoC stays out.
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(sensors:
            [Temp(WinTctl, "AMD Ryzen 7 5800X Core (Tctl/Tdie)", 70), Temp(WinSoc, "AMD Ryzen 7 5800X SoC", 45)]));
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal("CPU", cfg.Sensors.Single(s => s.SensorId == WinSoc).Group); // still suggested and saved
        Assert.Equal(new[] { WinTctl }, cfg.Curves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
    }

    // The same with a daemon that sends the hardware labels: the sensors are saved without own names, so the analysis has
    // to get the labels - otherwise both would match by group only.
    [Fact]
    public async Task Finish_AirflowMode_SensorsWithoutOwnNames_AreMatchedByTheirLabels()
    {
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(sensors:
        [
            Temp(WinTctl, "AMD Ryzen 7 5800X Core (Tctl/Tdie)", 70) with { Label = "AMD Ryzen 7 5800X Core (Tctl/Tdie)" },
            Temp(WinSoc, "AMD Ryzen 7 5800X SoC", 45) with { Label = "AMD Ryzen 7 5800X SoC" },
        ]));
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.All(cfg.Sensors, s => Assert.Equal("", s.Name)); // the labels are no own names
        Assert.Equal(new[] { WinTctl }, cfg.Curves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
    }

    [Fact]
    public async Task Finish_AirflowMode_SuggestedGroup_IsFallbackWithoutNameMatch()
    {
        // No sensor names the CPU: then the suggested group decides - in the preview and in what Finish saves.
        // Without it the curve would fall back to the first sensor, the SSD.
        var (ctrl, sent) = MakeController();
        ctrl.Apply(MakeSnapshot(sensors:
            [Temp("nvme@nvme0/temp1", "Composite", 38), Temp(WinSoc, "AMD Ryzen 7 5800X SoC", 45)]));
        ctrl.Fans.Single(f => f.FanId == "hwmon0/pwm1").Location = FanLocationOption.For(FanLocation.CpuCooler);

        await ctrl.FinishCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent);
        Assert.Equal(new[] { WinSoc }, cfg.Curves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
    }

    [Fact]
    public async Task Skip_WritesNoGroups()
    {
        var (ctrl, sent) = MakeController();
        var config = new AppConfig
        {
            Sensors = [new SensorConfig { SensorId = "k10temp/temp1", Name = "Tctl" }],
            SensorGroups = ["Zeta"],
        };
        ctrl.Apply(MakeSnapshot(sensors: [Temp("k10temp/temp1", "Tctl"), Temp("nvme@nvme0/temp1", "SSD")], config: config));
        Assert.NotEmpty(ctrl.TemperatureSensorGroups); // suggestions were shown …

        await ctrl.SkipCommand.ExecuteAsync(null);

        AppConfig cfg = Assert.Single(sent); // … but Skip only marks the wizard as done
        Assert.Equal(new[] { "Zeta" }, cfg.SensorGroups);
        Assert.Null(Assert.Single(cfg.Sensors).Group);
        Assert.Equal(config with { OnboardingCompleted = true }, cfg);
    }
}
