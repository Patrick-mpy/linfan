// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.Core.Tests;

public sealed class AirflowTuneServiceTests
{
    // ── Helfer ──────────────────────────────────────────────────────────────────

    private static FanConfig Fan(string id, FanLocation location, FanCalibration? calibration = null) =>
        new() { FanId = id, Name = id, Location = location, Calibration = calibration };

    private static FanCalibration Cal(int maxRpm) =>
        new() { StartPwm = 60, MinRpm = 300, MaxRpm = maxRpm };

    private static AppConfig ConfigWith(params FanConfig[] fans) =>
        AppConfig.Empty with { Fans = fans };

    // ── A · Richtungs-Klassifikation ────────────────────────────────────────────

    [Theory]
    [InlineData(FanLocation.CaseFrontIntake, AirflowDirection.Intake)]
    [InlineData(FanLocation.CaseBottomIntake, AirflowDirection.Intake)]
    [InlineData(FanLocation.CaseSideIntake, AirflowDirection.Intake)]
    [InlineData(FanLocation.CaseRearExhaust, AirflowDirection.Exhaust)]
    [InlineData(FanLocation.CaseTopExhaust, AirflowDirection.Exhaust)]
    [InlineData(FanLocation.CaseFrontExhaust, AirflowDirection.Exhaust)]
    [InlineData(FanLocation.CaseBottomExhaust, AirflowDirection.Exhaust)]
    [InlineData(FanLocation.CaseSideExhaust, AirflowDirection.Exhaust)]
    [InlineData(FanLocation.CaseTopIntake, AirflowDirection.Intake)]
    [InlineData(FanLocation.CaseRearIntake, AirflowDirection.Intake)]
    [InlineData(FanLocation.CpuCooler, AirflowDirection.Internal)]
    [InlineData(FanLocation.GpuCooler, AirflowDirection.Internal)]
    [InlineData(FanLocation.Radiator, AirflowDirection.Internal)]
    [InlineData(FanLocation.Psu, AirflowDirection.Internal)]
    [InlineData(FanLocation.FanWall, AirflowDirection.Internal)]
    [InlineData(FanLocation.Unspecified, AirflowDirection.Unknown)]
    [InlineData(FanLocation.Other, AirflowDirection.Unknown)]
    public void DirectionOf_MapsEveryLocation(FanLocation location, AirflowDirection expected)
    {
        Assert.Equal(expected, AirflowTuneService.DirectionOf(location));
    }

    // ── B · Druckbilanz ─────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_MoreIntakeThanExhaust_IsPositivePressure()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CaseFrontIntake),
            Fan("f2", FanLocation.CaseBottomIntake),
            Fan("f3", FanLocation.CaseRearExhaust));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.SlightPositive, result.Verdict);
        Assert.Equal(2, result.IntakeWeight);
        Assert.Equal(1, result.ExhaustWeight);
    }

    [Fact]
    public void Analyze_MoreExhaustThanIntake_IsNegativePressure()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CaseFrontIntake),
            Fan("f2", FanLocation.CaseRearExhaust),
            Fan("f3", FanLocation.CaseTopExhaust));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.Negative, result.Verdict);
        Assert.Contains(AirflowHint.NegativePressure, result.Hints);
    }

    [Fact]
    public void Analyze_EqualIntakeAndExhaust_IsBalanced()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CaseFrontIntake),
            Fan("f2", FanLocation.CaseRearExhaust));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.Balanced, result.Verdict);
    }

    [Fact]
    public void Analyze_NoCaseFans_PressureUnknown()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CpuCooler),
            Fan("f2", FanLocation.GpuCooler));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.Unknown, result.Verdict);
        Assert.Contains(AirflowHint.NoCaseFans, result.Hints);
    }

    [Fact]
    public void Analyze_OnlyExhaust_WarnsNoIntake()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CaseRearExhaust));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.OnlyExhaust, result.Verdict);
        Assert.Contains(AirflowHint.NoIntakeFan, result.Hints);
    }

    [Fact]
    public void Analyze_OnlyIntake_WarnsNoExhaust()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CaseFrontIntake));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.OnlyIntake, result.Verdict);
        Assert.Contains(AirflowHint.NoExhaustFan, result.Hints);
    }

    [Fact]
    public void Analyze_WhenAllCalibrated_WeightsByMaxRpmNotCount()
    {
        // Anzahl würde Unterdruck ergeben (1 Einlass vs. 2 Auslass), aber die starke Einlass-RPM dreht das.
        AppConfig config = ConfigWith(
            Fan("in", FanLocation.CaseFrontIntake, Cal(3000)),
            Fan("ex1", FanLocation.CaseRearExhaust, Cal(1000)),
            Fan("ex2", FanLocation.CaseTopExhaust, Cal(1000)));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.SlightPositive, result.Verdict);
        Assert.Equal(3000, result.IntakeWeight);
        Assert.Equal(2000, result.ExhaustWeight);
    }

    [Fact]
    public void Analyze_WhenNotAllCalibrated_FallsBackToCountAndHints()
    {
        AppConfig config = ConfigWith(
            Fan("in", FanLocation.CaseFrontIntake, Cal(3000)),
            Fan("ex", FanLocation.CaseRearExhaust)); // unkalibriert → Zähl-Fallback

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(1, result.IntakeWeight);
        Assert.Equal(1, result.ExhaustWeight);
        Assert.Equal(PressureVerdict.Balanced, result.Verdict);
        Assert.Contains(AirflowHint.CountEstimateOnly, result.Hints);
    }

    // ── C · Rollenbasierte Kurven ───────────────────────────────────────────────

    [Theory]
    [InlineData(FanLocation.CpuCooler, "airflow-cpu")]
    [InlineData(FanLocation.Radiator, "airflow-cpu")]
    [InlineData(FanLocation.GpuCooler, "airflow-gpu")]
    [InlineData(FanLocation.CaseFrontIntake, "airflow-intake")]
    [InlineData(FanLocation.CaseRearExhaust, "airflow-exhaust")]
    [InlineData(FanLocation.Unspecified, "airflow-default")]
    [InlineData(FanLocation.Other, "airflow-default")]
    public void Analyze_AssignsRoleCurvePerLocation(FanLocation location, string expectedCurveId)
    {
        AppConfig config = ConfigWith(Fan("f1", location));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(expectedCurveId, result.Fans.Single().SuggestedCurveId);
        Assert.Contains(result.SuggestedCurves, c => c.Id == expectedCurveId);
    }

    [Fact]
    public void Analyze_CurveNames_DefaultToNeutralEnglish()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CaseFrontIntake));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal("Airflow · Intake", result.SuggestedCurves.Single().Name);
    }

    [Fact]
    public void Analyze_CurveNames_OverrideReachesSuggestedCurves()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CaseFrontIntake));
        var names = new Dictionary<string, string> { ["airflow-intake"] = "Airflow · Einlass" };

        AirflowTuneResult result = AirflowTuneService.Analyze(config, names);

        Assert.Equal("Airflow · Einlass", result.SuggestedCurves.Single().Name);
    }

    [Fact]
    public void Analyze_CpuAndRadiator_ShareOneCurve()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CpuCooler),
            Fan("f2", FanLocation.Radiator));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Single(result.SuggestedCurves);
        Assert.Equal("airflow-cpu", result.SuggestedCurves[0].Id);
    }

    [Fact]
    public void Analyze_OnlyEmitsCurvesForUsedRoles()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CpuCooler),
            Fan("f2", FanLocation.GpuCooler));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(2, result.SuggestedCurves.Count);
        Assert.Contains(result.SuggestedCurves, c => c.Id == "airflow-cpu");
        Assert.Contains(result.SuggestedCurves, c => c.Id == "airflow-gpu");
    }

    [Fact]
    public void Analyze_Curves_AreMonotonicAndEndAtFullSpeed()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CpuCooler),
            Fan("f2", FanLocation.GpuCooler),
            Fan("f3", FanLocation.CaseFrontIntake),
            Fan("f4", FanLocation.CaseRearExhaust),
            Fan("f5", FanLocation.Other));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        foreach (CurveConfig curve in result.SuggestedCurves)
        {
            Assert.NotEmpty(curve.Points);
            Assert.Equal(100, curve.Points[^1].Percent); // bei hoher Temp volle Kühlung (fail-safe-freundlich)
            // Volle Drehzahl deutlich vor dem Watchdog-Default (FailSafeTempC 90 °C), damit die Kurve
            // sanft auf 100 % fährt, statt dass der Fail-Safe abrupt übernimmt.
            Assert.True(curve.Points[^1].TemperatureC <= AppConfig.Empty.FailSafeTempC - 4,
                $"{curve.Id} erreicht 100 % erst bei {curve.Points[^1].TemperatureC} °C");
            for (int i = 1; i < curve.Points.Count; i++)
            {
                Assert.True(curve.Points[i].TemperatureC > curve.Points[i - 1].TemperatureC);
                Assert.True(curve.Points[i].Percent >= curve.Points[i - 1].Percent);
            }
        }
    }

    [Fact]
    public void Analyze_Psu_GetsNoCurveAndStaysOnAuto()
    {
        AppConfig config = ConfigWith(Fan("psu", FanLocation.Psu));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Null(result.Fans.Single().SuggestedCurveId);
        Assert.DoesNotContain(result.SuggestedCurves, c => c.Id.Contains("psu"));
    }

    [Fact]
    public void Analyze_Unspecified_ReasonIsNoPositionDefaultCurve()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.Unspecified));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(AirflowReason.NoPositionDefaultCurve, result.Fans.Single().Reason);
    }

    // ── C2 · Ausgeblendete Kanäle ───────────────────────────────────────────────

    [Fact]
    public void Analyze_HiddenFan_GetsNoSuggestionNoPressureWeightNoRoleCurve()
    {
        // Realer Fall: totes GPU-PWM-Interface, vom Nutzer ausgeblendet - darf in der Analyse
        // nirgends auftauchen (Liste, Druckbilanz, Rollen-Kurven).
        AppConfig config = ConfigWith(
            Fan("front", FanLocation.CaseFrontIntake),
            Fan("ghost-exhaust", FanLocation.CaseRearExhaust) with { Hidden = true },
            Fan("ghost-gpu", FanLocation.GpuCooler) with { Hidden = true });

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(new[] { "front" }, result.Fans.Select(f => f.FanId));
        Assert.Equal(0, result.ExhaustWeight); // der versteckte Auslass zählt nicht in die Bilanz
        Assert.Equal(PressureVerdict.OnlyIntake, result.Verdict);
        Assert.DoesNotContain(result.SuggestedCurves, c => c.Id == "airflow-gpu");
    }

    [Fact]
    public void Analyze_HiddenSensor_IsNotUsedAsCurveSource()
    {
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "dead", Name = "CPU Package", Hidden = true },
                new SensorConfig { SensorId = "alive", Name = "Board Temp" },
            },
            Fans = new[] { Fan("f1", FanLocation.CpuCooler) },
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        CurveConfig cpu = result.SuggestedCurves.Single();
        Assert.Equal(new[] { "alive" }, cpu.SourceSensorIds); // Fallback statt des versteckten CPU-Sensors
        Assert.Contains(AirflowHint.NoCpuSensorDetected, result.Hints);
    }

    // ── D · Sensor-Zuordnung (Namens-/Gruppen-Heuristik) ────────────────────────

    [Fact]
    public void Analyze_PairsCpuAndGpuSensorsByName()
    {
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "t1", Name = "CPU Package" },
                new SensorConfig { SensorId = "t2", Name = "amdgpu edge" },
            },
            Fans = new[]
            {
                Fan("f1", FanLocation.CpuCooler),
                Fan("f2", FanLocation.GpuCooler),
            },
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        CurveConfig cpu = result.SuggestedCurves.Single(c => c.Id == "airflow-cpu");
        CurveConfig gpu = result.SuggestedCurves.Single(c => c.Id == "airflow-gpu");
        Assert.Equal(new[] { "t1" }, cpu.SourceSensorIds);
        Assert.Equal(new[] { "t2" }, gpu.SourceSensorIds);
    }

    [Fact]
    public void Analyze_GroupOnlyCpuSensor_StaysOutOfTheAverage_WhenOthersMatchByName()
    {
        // Windows/LHM 5800X after automatic grouping: the SoC sits in group "CPU" without a CPU keyword in its
        // name and runs ~20 K below Tctl under load. Averaged in, it would push 100 % to Tctl ≈ 89.5 °C - right
        // at the 90 °C fail-safe instead of with soft-ramp headroom below it.
        const string tctl = "/amdcpu/0/temperature/2", ccd1 = "/amdcpu/0/temperature/3", soc = "/amdcpu/0/temperature/4";
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = tctl, Name = "AMD Ryzen 7 5800X Core (Tctl/Tdie)", Group = "CPU" },
                new SensorConfig { SensorId = ccd1, Name = "AMD Ryzen 7 5800X CCD1 (Tdie)", Group = "CPU" },
                new SensorConfig { SensorId = soc, Name = "AMD Ryzen 7 5800X SoC", Group = "CPU" },
            },
            Fans = new[] { Fan("f1", FanLocation.CpuCooler) },
        };

        CurveConfig cpu = AirflowTuneService.Analyze(config).SuggestedCurves.Single(c => c.Id == "airflow-cpu");

        Assert.Equal(new[] { tctl, ccd1 }, cpu.SourceSensorIds);
        Assert.Equal(SensorAggregation.Avg, cpu.Aggregation);
        double fullSpeedAt = FullSpeedHottestTemp(cpu, t => new Dictionary<string, double>
        {
            [tctl] = t,
            [ccd1] = t - 2,
            [soc] = t - 20,
        });
        // Tctl is the hottest sensor, so it must get the headroom of a Max curve (100 % by 86 °C): the Avg
        // curves end 4 K earlier exactly so that the average may lag it by that much.
        Assert.True(fullSpeedAt <= AppConfig.Empty.FailSafeTempC - 4, $"100 % only at Tctl {fullSpeedAt} °C");
    }

    [Theory]
    [InlineData("nct6798 CPUTIN")] // Linux: chip + label
    [InlineData("CPUTIN")]         // Windows/LHM
    public void Analyze_SuperIoCputin_IsNoCpuSource(string cputinName)
    {
        // A board sensor next to the socket: it matches "cpu" by name, but runs far below Tctl under load.
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "k10temp/temp1", Name = "k10temp Tctl", Group = "CPU" },
                new SensorConfig { SensorId = "nct6798/temp2", Name = cputinName, Group = "CPU" },
            },
            Fans = new[] { Fan("f1", FanLocation.CpuCooler), Fan("f2", FanLocation.CaseFrontIntake) },
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(new[] { "k10temp/temp1" }, result.SuggestedCurves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
        Assert.All(result.SuggestedCurves, c => Assert.DoesNotContain("nct6798/temp2", c.SourceSensorIds));
    }

    [Fact]
    public void Analyze_SuperIoCputin_IsTheLastCpuFallback_BeforeTheGpu()
    {
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "amdgpu/temp1", Name = "amdgpu edge", Group = "GPU" },
                new SensorConfig { SensorId = "nct6798/temp2", Name = "nct6798 CPUTIN", Group = "Mainboard" },
            },
            Fans = new[] { Fan("f1", FanLocation.CpuCooler) },
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(new[] { "nct6798/temp2" }, result.SuggestedCurves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
        Assert.Contains(AirflowHint.NoCpuSensorDetected, result.Hints); // still no real CPU sensor
    }

    // A sensor without an own name is called by its hardware label, which the config does not hold - the caller passes it.
    // Without it every such sensor would fall back to its group, and the SoC would be averaged into the CPU curve again.
    [Fact]
    public void Analyze_NamesASensorWithoutAnOwnNameByItsLabel()
    {
        const string tctl = "/amdcpu/0/temperature/2", soc = "/amdcpu/0/temperature/4";
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = tctl, Name = "", Group = "CPU" },
                new SensorConfig { SensorId = soc, Name = "", Group = "CPU" },
            },
            Fans = new[] { Fan("f1", FanLocation.CpuCooler) },
        };
        var labels = new Dictionary<string, string>
        {
            [tctl] = "AMD Ryzen 7 5800X Core (Tctl/Tdie)",
            [soc] = "AMD Ryzen 7 5800X SoC",
        };

        CurveConfig Cpu(IReadOnlyDictionary<string, string>? sensorLabels) =>
            AirflowTuneService.Analyze(config, sensorLabels: sensorLabels).SuggestedCurves.Single(c => c.Id == "airflow-cpu");

        Assert.Equal(new[] { tctl }, Cpu(labels).SourceSensorIds);
        Assert.Equal(new[] { tctl, soc }, Cpu(null).SourceSensorIds); // the group fallback the labels prevent
    }

    [Fact]
    public void Analyze_CpuSensors_FallBackToGroup_WhenNoneMatchesByName()
    {
        // A board sensor without a keyword in its name, filed under "CPU" by the user. The GPU's "… GPU Core"
        // matches the CPU keyword "core" by name, but belongs to the GPU and must not suppress the fallback.
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "nct6798/temp3", Name = "nct6798 temp3", Group = "CPU" },
                new SensorConfig { SensorId = "nct6798/temp1", Name = "nct6798 temp1", Group = "Mainboard" },
                new SensorConfig { SensorId = "gpucore", Name = "NVIDIA GeForce RTX 3080 GPU Core", Group = "GPU" },
            },
            Fans = new[] { Fan("f1", FanLocation.CpuCooler), Fan("f2", FanLocation.GpuCooler) },
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(new[] { "nct6798/temp3" }, result.SuggestedCurves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
        Assert.Equal(new[] { "gpucore" }, result.SuggestedCurves.Single(c => c.Id == "airflow-gpu").SourceSensorIds);
        Assert.DoesNotContain(AirflowHint.NoCpuSensorDetected, result.Hints);
    }

    [Fact]
    public void Analyze_GroupOnlyGpuSensor_StaysOutOfTheAverage_WhenOthersMatchByName()
    {
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "amdgpu/edge", Name = "amdgpu edge", Group = "GPU" },
                new SensorConfig { SensorId = "amdgpu/junction", Name = "amdgpu junction", Group = "GPU" },
                new SensorConfig { SensorId = "nct6798/temp5", Name = "nct6798 temp5", Group = "GPU" },
            },
            Fans = new[] { Fan("f1", FanLocation.GpuCooler) },
        };

        CurveConfig gpu = AirflowTuneService.Analyze(config).SuggestedCurves.Single(c => c.Id == "airflow-gpu");

        Assert.Equal(new[] { "amdgpu/edge", "amdgpu/junction" }, gpu.SourceSensorIds);
        Assert.Equal(SensorAggregation.Avg, gpu.Aggregation);
    }

    [Fact]
    public void Analyze_GpuSensors_FallBackToGroup_WhenNoneMatchesByName()
    {
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "cpu", Name = "CPU Package", Group = "CPU" },
                new SensorConfig { SensorId = "it8688/temp4", Name = "it8688 temp4", Group = "GPU" },
            },
            Fans = new[] { Fan("f1", FanLocation.GpuCooler) },
        };

        CurveConfig gpu = AirflowTuneService.Analyze(config).SuggestedCurves.Single(c => c.Id == "airflow-gpu");

        Assert.Equal(new[] { "it8688/temp4" }, gpu.SourceSensorIds);
    }

    /// <summary>
    /// The lowest temperature of the hottest sensor (0.5 K steps) at which <paramref name="curve"/>, fed with its
    /// aggregated sources, demands 100 % - <see cref="double.PositiveInfinity"/> if it never does.
    /// </summary>
    private static double FullSpeedHottestTemp(CurveConfig curve, Func<double, IReadOnlyDictionary<string, double>> readingsAt)
    {
        var engineCurve = new Curve(curve.Name, curve.Points, curve.InterpolationMode);
        for (double t = 30; t <= 110; t += 0.5)
        {
            IReadOnlyDictionary<string, double> readings = readingsAt(t);
            double input = SensorAggregator.Aggregate(curve.SourceSensorIds.Select(id => readings[id]), curve.Aggregation);
            if (CurveEngine.Evaluate(engineCurve, input) >= 100)
                return t;
        }
        return double.PositiveInfinity;
    }

    [Fact]
    public void Analyze_GpuCoreSensor_IsNotClassifiedAsCpu()
    {
        // „AMD Radeon RX 6600 XT GPU Core" enthält das CPU-Schlüsselwort „core" - GPU-Treffer
        // haben Vorrang und dürfen nicht zusätzlich in der CPU-Kurve landen.
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "cpu", Name = "CPU Package" },
                new SensorConfig { SensorId = "gpucore", Name = "AMD Radeon RX 6600 XT GPU Core" },
            },
            Fans = new[] { Fan("f1", FanLocation.CpuCooler), Fan("f2", FanLocation.GpuCooler) },
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(new[] { "cpu" }, result.SuggestedCurves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
        Assert.Equal(new[] { "gpucore" }, result.SuggestedCurves.Single(c => c.Id == "airflow-gpu").SourceSensorIds);
    }

    [Fact]
    public void SensorKeywords_ArePinned()
    {
        // They are built on SensorClassifier's driver lists: a change there must show up here as a deliberate edit,
        // since it changes which sensors feed the proposed CPU/GPU curves.
        Assert.Equal(
            new[] { "cpu", "coretemp", "k10temp", "zenpower", "package", "tctl", "tdie", "core" },
            AirflowTuneService.CpuKeywords);
        Assert.Equal(
            new[] { "gpu", "radeon", "nvidia", "nouveau", "i915", "geforce", "edge", "junction" },
            AirflowTuneService.GpuKeywords);
    }

    [Fact]
    public void Analyze_KnowsTheDriverNamesOfTheSensorClassifier()
    {
        // Linux names an unlabelled channel "<chip> <channel>"; the keywords share the classifier's driver list.
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "zenpower/temp1", Name = "zenpower temp1" },
                new SensorConfig { SensorId = "nouveau/temp1", Name = "nouveau temp1" },
            },
            Fans = new[] { Fan("f1", FanLocation.CpuCooler), Fan("f2", FanLocation.GpuCooler) },
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(new[] { "zenpower/temp1" }, result.SuggestedCurves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
        Assert.Equal(new[] { "nouveau/temp1" }, result.SuggestedCurves.Single(c => c.Id == "airflow-gpu").SourceSensorIds);
    }

    [Fact]
    public void Analyze_CaseCurve_AggregatesCpuAndGpuWithMax()
    {
        AppConfig config = AppConfig.Empty with
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "t1", Name = "CPU Package" },
                new SensorConfig { SensorId = "t2", Name = "amdgpu edge" },
            },
            Fans = new[] { Fan("f1", FanLocation.CaseFrontIntake) },
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        CurveConfig intake = result.SuggestedCurves.Single(c => c.Id == "airflow-intake");
        Assert.Equal(SensorAggregation.Max, intake.Aggregation);
        Assert.Contains("t1", intake.SourceSensorIds);
        Assert.Contains("t2", intake.SourceSensorIds);
    }

    [Fact]
    public void Analyze_NoSensors_HintsToPickManually()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CpuCooler));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Empty(result.SuggestedCurves.Single().SourceSensorIds);
        Assert.Contains(AirflowHint.NoSensorsConfigured, result.Hints);
    }

    // ── E · Proposal per target profile (Propose / ApplyTo / AsNewProfile) ──────────────

    private static Profile ProfileWith(string id, IReadOnlyList<CurveConfig> curves, params ProfileAssignment[] assignments) =>
        new() { Id = id, Name = id, Curves = curves, Assignments = assignments };

    private static CurveConfig Curve(string id, string name = "Eigene") =>
        new() { Id = id, Name = name, Points = new[] { new CurvePoint(40, 30), new CurvePoint(70, 80) } };

    [Fact]
    public void Propose_ComparesWithTheTargetProfile_NotWithTheConfigsActiveCurves()
    {
        AppConfig config = ConfigWith(Fan("cpu", FanLocation.CpuCooler), Fan("front", FanLocation.CaseFrontIntake),
            Fan("psu", FanLocation.Psu)) with
        {
            Fans = new[]
            {
                Fan("cpu", FanLocation.CpuCooler) with { AssignedCurveId = "running-curve" },
                Fan("front", FanLocation.CaseFrontIntake),
                Fan("psu", FanLocation.Psu),
            },
        };
        Profile target = ProfileWith("quiet", [Curve("airflow-cpu")],
            new ProfileAssignment("cpu", "airflow-cpu"), new ProfileAssignment("front", "manual-curve"));

        AirflowProposal proposal = AirflowTuneService.Propose(config, target, ["cpu", "front", "psu"]);

        Assert.Equal(
            new[]
            {
                new AirflowChange("cpu", "airflow-cpu", "airflow-cpu"),
                new AirflowChange("front", "manual-curve", "airflow-intake"),
                new AirflowChange("psu", null, null),
            },
            proposal.Changes);
        Assert.Equal(new[] { false, true, false }, proposal.Changes.Select(c => c.Changed));
    }

    [Fact]
    public void Propose_SkipsHiddenAndUnknownFans()
    {
        AppConfig config = ConfigWith(Fan("a", FanLocation.CaseRearExhaust), Fan("b", FanLocation.CpuCooler) with { Hidden = true });

        AirflowProposal proposal = AirflowTuneService.Propose(config, ProfileWith("p", []), ["a", "b", "ghost"]);

        Assert.Equal("a", Assert.Single(proposal.Changes).FanId);
    }

    [Theory]
    [InlineData(FanLocation.Unspecified, "airflow-default")]
    [InlineData(FanLocation.Other, "airflow-default")]
    [InlineData(FanLocation.Psu, null)]
    [InlineData(FanLocation.Radiator, "airflow-cpu")]
    [InlineData(FanLocation.CaseTopIntake, "airflow-intake")]
    public void Propose_MapsEveryPositionToItsRoleCurve(FanLocation location, string? curveId)
    {
        AirflowProposal proposal = AirflowTuneService.Propose(ConfigWith(Fan("f", location)), ProfileWith("p", []), ["f"]);

        Assert.Equal(curveId, proposal.Changes.Single().ProposedCurveId);
    }

    [Fact]
    public void Propose_KeepsTheProfilesOwnRoleCurve_AndTemplatesTheRest_InTheProfilesVariant()
    {
        CurveConfig tuned = Curve("airflow-cpu", "Meine CPU-Kurve");
        AirflowProposal proposal = AirflowTuneService.Propose(ConfigWith(Fan("f", FanLocation.CpuCooler)),
            ProfileWith("silent", [tuned]), ["f"]);

        Assert.Equal(new[] { "airflow-cpu", "airflow-gpu", "airflow-intake", "airflow-exhaust", "airflow-default" },
            proposal.RoleCurves.Select(r => r.Curve.Id));
        Assert.Same(tuned, proposal.RoleCurves[0].Curve);
        Assert.True(proposal.RoleCurves[0].InProfile);
        Assert.All(proposal.RoleCurves.Skip(1), r => Assert.False(r.InProfile));

        // "silent" gets the silent tables: the GPU template starts at 0 %, the balanced one at 20 %.
        Assert.Equal(0, proposal.RoleCurves[1].Curve.Points[0].Percent);
        AirflowProposal balanced = AirflowTuneService.Propose(ConfigWith(Fan("f", FanLocation.CpuCooler)),
            ProfileWith("p-1234", []), ["f"]);
        Assert.Equal(20, balanced.RoleCurves[1].Curve.Points[0].Percent);
    }

    [Theory]
    [InlineData("silent", AirflowAggressiveness.Silent)]
    [InlineData("performance", AirflowAggressiveness.Performance)]
    [InlineData("balanced", AirflowAggressiveness.Balanced)]
    [InlineData("profile-0f3a", AirflowAggressiveness.Balanced)]
    public void VariantFor_FollowsTheOnboardingProfileIds(string profileId, AirflowAggressiveness expected) =>
        Assert.Equal(expected, AirflowTuneService.VariantFor(profileId));

    [Fact]
    public void ApplyTo_AssignsThePickedFans_AddsOnlyNeededMissingCurves_AndLeavesTheRest()
    {
        CurveConfig own = Curve("airflow-cpu", "Meine CPU-Kurve");
        AppConfig config = ConfigWith(Fan("cpu", FanLocation.CpuCooler), Fan("front", FanLocation.CaseFrontIntake),
            Fan("rear", FanLocation.CaseRearExhaust));
        Profile target = ProfileWith("p", [own, Curve("manual")],
            new ProfileAssignment("cpu", "manual"), new ProfileAssignment("front", "manual"), new ProfileAssignment("rear", "manual"));
        AirflowProposal proposal = AirflowTuneService.Propose(config, target, ["cpu", "front", "rear"]);

        Profile applied = AirflowTuneService.ApplyTo(target, proposal, proposal.Changes.Where(c => c.FanId != "rear"));

        Assert.Equal(new[] { ("cpu", (string?)"airflow-cpu"), ("front", "airflow-intake"), ("rear", "manual") },
            applied.Assignments.Select(a => (a.FanId, a.CurveId)));
        Assert.Equal(new[] { "airflow-cpu", "manual", "airflow-intake" }, applied.Curves.Select(c => c.Id));
        Assert.Same(own, applied.Curves[0]); // the user's tuned role curve is not reset to the template
        Assert.Equal(target.Id, applied.Id);
    }

    [Fact]
    public void ApplyTo_AddsAnAssignmentForAFanTheProfileDidNotList()
    {
        AppConfig config = ConfigWith(Fan("new", FanLocation.CaseRearExhaust));
        Profile target = ProfileWith("p", []);
        AirflowProposal proposal = AirflowTuneService.Propose(config, target, ["new"]);

        Profile applied = AirflowTuneService.ApplyTo(target, proposal, proposal.Changes);

        Assert.Equal(new ProfileAssignment("new", "airflow-exhaust"), Assert.Single(applied.Assignments));
    }

    [Fact]
    public void AsNewProfile_LeavesTheSourceAlone_AndDropsCurvesThatLostAllTheirFans()
    {
        AppConfig config = ConfigWith(Fan("cpu", FanLocation.CpuCooler), Fan("front", FanLocation.CaseFrontIntake));
        Profile source = ProfileWith("p", [Curve("manual"), Curve("spare")],
            new ProfileAssignment("cpu", "manual"), new ProfileAssignment("front", "manual"));
        AirflowProposal proposal = AirflowTuneService.Propose(config, source, ["cpu", "front"]);

        Profile created = AirflowTuneService.AsNewProfile(source, proposal, proposal.Changes, "p-new", "p · Airflow");

        Assert.Equal(("p-new", "p · Airflow"), (created.Id, created.Name));
        // "manual" lost both fans and goes; "spare" never had one and stays.
        Assert.Equal(new[] { "spare", "airflow-cpu", "airflow-intake" }, created.Curves.Select(c => c.Id));
        Assert.Equal(new[] { "manual", "spare" }, source.Curves.Select(c => c.Id));
        Assert.All(source.Assignments, a => Assert.Equal("manual", a.CurveId));
    }

    [Fact]
    public void Proposal_NullArguments_Throw()
    {
        Profile target = ProfileWith("p", []);
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.Propose(null!, target, []));
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.Propose(AppConfig.Empty, null!, []));
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.ApplyTo(target, null!, []));
        AirflowProposal proposal = AirflowTuneService.Propose(AppConfig.Empty, target, []);
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.Propose(AppConfig.Empty, target, null!));
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.ApplyTo(null!, proposal, []));
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.ApplyTo(target, proposal, null!));
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.AsNewProfile(null!, proposal, [], "p2", "Copy"));
        Assert.ThrowsAny<ArgumentException>(() => AirflowTuneService.AsNewProfile(target, proposal, [], " ", "Copy"));
        Assert.ThrowsAny<ArgumentException>(() => AirflowTuneService.AsNewProfile(target, proposal, [], "p2", ""));
    }

    [Fact]
    public void ApplyTo_APickedPowerSupplyFan_GoesToHardwareAuto_WithoutAddingACurve()
    {
        AppConfig config = ConfigWith(Fan("psu", FanLocation.Psu));
        Profile target = ProfileWith("p", [Curve("manual")], new ProfileAssignment("psu", "manual"));
        AirflowProposal proposal = AirflowTuneService.Propose(config, target, ["psu"]);

        Profile applied = AirflowTuneService.ApplyTo(target, proposal, proposal.Changes);

        Assert.Equal(new ProfileAssignment("psu", null), Assert.Single(applied.Assignments));
        Assert.Equal(new[] { "manual" }, applied.Curves.Select(c => c.Id)); // the curve stays, no role curve comes in
    }

    // ── F · Pressure verdict and its basis ────────────────────────────────

    [Theory]
    [InlineData(0, 0, PressureVerdict.Unknown)]
    [InlineData(0, 1, PressureVerdict.OnlyExhaust)]
    [InlineData(1, 0, PressureVerdict.OnlyIntake)]
    [InlineData(1000, 1200, PressureVerdict.Negative)]     // ratio 0.83 ≤ 1/1.15
    [InlineData(1000, 1100, PressureVerdict.Balanced)]     // ratio 0.91
    [InlineData(1100, 1000, PressureVerdict.Balanced)]     // ratio 1.10 < 1.15
    [InlineData(1150, 1000, PressureVerdict.SlightPositive)] // ratio 1.15, share 0.535
    [InlineData(2000, 1000, PressureVerdict.SlightPositive)] // share 0.667
    [InlineData(2200, 1000, PressureVerdict.StrongPositive)] // share 0.69 ≥ 0.68
    public void Verdict_FollowsTheIntakeShare(int intakeRpm, int exhaustRpm, PressureVerdict expected)
    {
        var fans = new List<FanConfig>();
        if (intakeRpm > 0)
            fans.Add(Fan("in", FanLocation.CaseFrontIntake, Cal(intakeRpm)));
        if (exhaustRpm > 0)
            fans.Add(Fan("out", FanLocation.CaseRearExhaust, Cal(exhaustRpm)));

        Assert.Equal(expected, AirflowTuneService.Analyze(ConfigWith([.. fans])).Verdict);
    }

    [Fact]
    public void ShareAndBasis_ByCalibratedMaxRpm_WhenEveryCaseFanIsCalibrated()
    {
        AirflowTuneResult result = AirflowTuneService.Analyze(ConfigWith(
            Fan("a", FanLocation.CaseFrontIntake, Cal(1500)), Fan("b", FanLocation.CaseBottomIntake, Cal(1500)),
            Fan("c", FanLocation.CaseRearExhaust, Cal(2000)), Fan("cpu", FanLocation.CpuCooler)));

        Assert.True(result.ByRpm);
        Assert.Equal((2, 1), (result.IntakeFans, result.ExhaustFans));
        Assert.Equal(0.6, result.IntakeShare!.Value, 6);
        Assert.Empty(result.UncalibratedCaseFanIds); // the CPU cooler is no case fan
    }

    [Fact]
    public void ShareAndBasis_ByCount_ListingTheUncalibratedCaseFans()
    {
        AirflowTuneResult result = AirflowTuneService.Analyze(ConfigWith(
            Fan("a", FanLocation.CaseFrontIntake, Cal(1500)), Fan("b", FanLocation.CaseRearExhaust),
            Fan("hidden", FanLocation.CaseTopExhaust) with { Hidden = true }));

        Assert.False(result.ByRpm);
        Assert.Equal(0.5, result.IntakeShare!.Value, 6);
        Assert.Equal(new[] { "b" }, result.UncalibratedCaseFanIds);
    }

    [Fact]
    public void Share_IsNull_WithoutCaseFans()
    {
        AirflowTuneResult result = AirflowTuneService.Analyze(ConfigWith(Fan("cpu", FanLocation.CpuCooler)));

        Assert.Null(result.IntakeShare);
        Assert.Equal(PressureVerdict.Unknown, result.Verdict);
    }

    // ── Grenzfälle & Guards ─────────────────────────────────────────────────────

    [Fact]
    public void Analyze_EmptyConfig_NoThrowAndUnknownPressure()
    {
        AirflowTuneResult result = AirflowTuneService.Analyze(AppConfig.Empty);

        Assert.Equal(PressureVerdict.Unknown, result.Verdict);
        Assert.Empty(result.Fans);
        Assert.Empty(result.SuggestedCurves);
    }

    [Fact]
    public void Analyze_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.Analyze(null!));
    }

    [Fact]
    public void Analyze_IsDeterministic()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CpuCooler),
            Fan("f2", FanLocation.CaseRearExhaust));

        AirflowTuneResult a = AirflowTuneService.Analyze(config);
        AirflowTuneResult b = AirflowTuneService.Analyze(config);

        Assert.Equal(a.Verdict, b.Verdict);
        Assert.Equal(a.SuggestedCurves.Count, b.SuggestedCurves.Count);
        Assert.Equal(
            a.Fans.Select(f => f.SuggestedCurveId),
            b.Fans.Select(f => f.SuggestedCurveId));
    }

    // ── Vollständigkeit: jede Position muss kohärent abgebildet sein ─────────────

    // Guard gegen verstreute/vergessene Zuordnung: für JEDEN FanLocation-Wert darf Analyze nicht werfen,
    // und der Vorschlag muss konsistent sein (Netzteil → keine Kurve; sonst genau eine existierende Kurve).
    [Fact]
    public void Analyze_EveryFanLocation_ProducesCoherentSuggestion()
    {
        foreach (FanLocation location in Enum.GetValues<FanLocation>())
        {
            AirflowTuneResult result = AirflowTuneService.Analyze(ConfigWith(Fan("f", location)));
            AirflowFanSuggestion s = result.Fans.Single();

            if (location == FanLocation.Psu)
            {
                Assert.Null(s.SuggestedCurveId);
                Assert.DoesNotContain(result.SuggestedCurves, c => c.Id == "airflow-psu");
            }
            else
            {
                Assert.NotNull(s.SuggestedCurveId);
                Assert.Contains(result.SuggestedCurves, c => c.Id == s.SuggestedCurveId);
            }
        }
    }

    // ── FilterToFans (selektive Übernahme) ──────────────────────────────────────

    [Fact]
    public void FilterToFans_KeepsOnlySelectedFansAndReferencedCurves()
    {
        AirflowTuneResult full = AirflowTuneService.Analyze(ConfigWith(
            Fan("cpu", FanLocation.CpuCooler),
            Fan("front", FanLocation.CaseFrontIntake),
            Fan("rear", FanLocation.CaseRearExhaust)));

        AirflowTuneResult filtered = AirflowTuneService.FilterToFans(full, new[] { "front" });

        Assert.Equal("front", filtered.Fans.Single().FanId);
        Assert.Equal("airflow-intake", Assert.Single(filtered.SuggestedCurves).Id); // nur die referenzierte Kurve bleibt
    }

    [Fact]
    public void FilterToFans_EmptySelection_YieldsNoFansAndNoCurves()
    {
        AirflowTuneResult full = AirflowTuneService.Analyze(ConfigWith(Fan("cpu", FanLocation.CpuCooler)));

        AirflowTuneResult filtered = AirflowTuneService.FilterToFans(full, Array.Empty<string>());

        Assert.Empty(filtered.Fans);
        Assert.Empty(filtered.SuggestedCurves);
    }

    [Fact]
    public void FilterToFans_DropsPsuCurveReference_WhenOnlyPsuSelected()
    {
        AirflowTuneResult full = AirflowTuneService.Analyze(ConfigWith(
            Fan("cpu", FanLocation.CpuCooler),
            Fan("psu", FanLocation.Psu)));

        AirflowTuneResult filtered = AirflowTuneService.FilterToFans(full, new[] { "psu" });

        Assert.Equal("psu", filtered.Fans.Single().FanId);
        Assert.Empty(filtered.SuggestedCurves); // PSU referenziert keine Kurve
    }

    [Fact]
    public void FilterToFans_NullArguments_Throw()
    {
        AirflowTuneResult result = AirflowTuneService.Analyze(AppConfig.Empty);

        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.FilterToFans(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.FilterToFans(result, null!));
    }

    // ── Case type (tower / server / open bench / laptop) ─────────────────────────

    [Theory]
    [InlineData(FanLocation.CaseTopExhaust, CaseType.Tower, true)]
    [InlineData(FanLocation.CaseSideIntake, CaseType.Tower, true)]
    [InlineData(FanLocation.FanWall, CaseType.Tower, false)]
    [InlineData(FanLocation.CaseFrontIntake, CaseType.Server, true)]
    [InlineData(FanLocation.CaseFrontExhaust, CaseType.Server, true)]
    [InlineData(FanLocation.CaseRearIntake, CaseType.Server, true)]
    [InlineData(FanLocation.FanWall, CaseType.Server, true)]
    [InlineData(FanLocation.CaseTopExhaust, CaseType.Server, false)]
    [InlineData(FanLocation.CaseBottomIntake, CaseType.Server, false)]
    [InlineData(FanLocation.CaseSideIntake, CaseType.Server, false)]
    [InlineData(FanLocation.CaseFrontIntake, CaseType.Open, false)]
    [InlineData(FanLocation.FanWall, CaseType.Open, false)]
    [InlineData(FanLocation.CpuCooler, CaseType.Open, true)]
    [InlineData(FanLocation.Psu, CaseType.Server, true)]
    [InlineData(FanLocation.Unspecified, CaseType.Open, true)]
    [InlineData(FanLocation.Other, CaseType.Server, true)]
    [InlineData(FanLocation.CpuCooler, CaseType.Laptop, true)]
    [InlineData(FanLocation.GpuCooler, CaseType.Laptop, true)]
    [InlineData(FanLocation.Radiator, CaseType.Laptop, false)]
    [InlineData(FanLocation.Psu, CaseType.Laptop, false)]
    [InlineData(FanLocation.FanWall, CaseType.Laptop, false)]
    [InlineData(FanLocation.CaseFrontIntake, CaseType.Laptop, false)]
    [InlineData(FanLocation.CaseFrontExhaust, CaseType.Laptop, false)]
    [InlineData(FanLocation.CaseRearExhaust, CaseType.Laptop, true)]
    [InlineData(FanLocation.CaseRearIntake, CaseType.Laptop, true)]
    [InlineData(FanLocation.CaseSideExhaust, CaseType.Laptop, true)]
    [InlineData(FanLocation.CaseTopIntake, CaseType.Laptop, true)]
    [InlineData(FanLocation.CaseBottomIntake, CaseType.Laptop, true)]
    [InlineData(FanLocation.Other, CaseType.Laptop, true)]
    [InlineData(FanLocation.Unspecified, CaseType.Laptop, true)]
    public void IsInCase_KnowsThePositionsOfEachType(FanLocation location, CaseType caseType, bool expected) =>
        Assert.Equal(expected, AirflowTuneService.IsInCase(location, caseType));

    [Fact]
    public void FanWall_GetsTheIntakeCurve_ButStaysOutOfTheBalance()
    {
        AppConfig config = ConfigWith(
            Fan("front", FanLocation.CaseFrontIntake), Fan("wall", FanLocation.FanWall), Fan("rear", FanLocation.CaseRearExhaust))
            with
        { CaseType = CaseType.Server };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.Balanced, result.Verdict);
        Assert.Equal(1, result.IntakeFans);
        Assert.Equal(1, result.ExhaustFans);
        Assert.Equal(AirflowTuneService.IntakeCurveId, result.Fans.Single(f => f.FanId == "wall").SuggestedCurveId);
        Assert.Empty(result.OutOfCaseFanIds);
    }

    [Fact]
    public void Server_LeavesATopFanOutOfTheBalance_AndSaysSo()
    {
        AppConfig config = ConfigWith(
            Fan("front", FanLocation.CaseFrontIntake), Fan("rear", FanLocation.CaseRearExhaust), Fan("top", FanLocation.CaseTopExhaust))
            with
        { CaseType = CaseType.Server };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.Balanced, result.Verdict); // 1 : 1 - the top fan does not count
        Assert.Equal(new[] { "top" }, result.OutOfCaseFanIds);
        Assert.Contains(AirflowHint.PositionNotInCase, result.Hints);
        // It keeps its role curve: the position is only missing from the schema.
        Assert.Equal(AirflowTuneService.ExhaustCurveId, result.Fans.Single(f => f.FanId == "top").SuggestedCurveId);
    }

    [Fact]
    public void Tower_CountsTheSameFans_AsBefore()
    {
        AppConfig config = ConfigWith(
            Fan("front", FanLocation.CaseFrontIntake), Fan("rear", FanLocation.CaseRearExhaust), Fan("top", FanLocation.CaseTopExhaust));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.Negative, result.Verdict);
        Assert.Empty(result.OutOfCaseFanIds);
        Assert.DoesNotContain(AirflowHint.PositionNotInCase, result.Hints);
    }

    [Fact]
    public void OpenBench_HasNoBalance_AndNoCaseFanHints()
    {
        AppConfig config = ConfigWith(Fan("cpu", FanLocation.CpuCooler), Fan("front", FanLocation.CaseFrontIntake))
            with
        { CaseType = CaseType.Open };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.NotApplicable, result.Verdict);
        Assert.Null(result.IntakeShare);
        Assert.Equal(0, result.IntakeFans + result.ExhaustFans);
        Assert.Equal(new[] { "front" }, result.OutOfCaseFanIds);
        Assert.DoesNotContain(AirflowHint.NoCaseFans, result.Hints);
        Assert.DoesNotContain(AirflowHint.NoExhaustFan, result.Hints);
        Assert.Contains(AirflowHint.PositionNotInCase, result.Hints);
    }

    [Fact]
    public void OpenBench_WithComponentFansOnly_HasNoCaseHint()
    {
        AppConfig config = ConfigWith(Fan("cpu", FanLocation.CpuCooler), Fan("gpu", FanLocation.GpuCooler))
            with
        { CaseType = CaseType.Open, Sensors = [new SensorConfig { SensorId = "k10temp/temp1", Name = "Tctl" }] };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.NotApplicable, result.Verdict);
        Assert.Empty(result.Hints);
    }

    [Fact]
    public void MixedSide_IsReported_AndEachFanCountsByItsOwnDirection()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CaseFrontIntake), Fan("f2", FanLocation.CaseFrontIntake), Fan("f3", FanLocation.CaseFrontExhaust),
            Fan("rear", FanLocation.CaseRearExhaust));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(2, result.IntakeFans);
        Assert.Equal(2, result.ExhaustFans);
        AirflowMixedSide front = Assert.Single(result.MixedSides);
        Assert.Equal(CaseSide.Front, front.Side);
        Assert.Equal(new[] { "f1", "f2" }, front.IntakeFanIds);
        Assert.Equal(new[] { "f3" }, front.ExhaustFanIds);
        Assert.Contains(AirflowHint.MixedSide, result.Hints);
    }

    [Fact]
    public void Server_FlagsFansAgainstTheFrontToRearFlow()
    {
        AppConfig config = ConfigWith(
            Fan("front", FanLocation.CaseFrontIntake), Fan("backwards", FanLocation.CaseRearIntake), Fan("rear", FanLocation.CaseRearExhaust))
            with
        { CaseType = CaseType.Server };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(new[] { "backwards" }, result.AgainstFlowFanIds);
        Assert.Contains(AirflowHint.AgainstCaseFlow, result.Hints);
        // The mixed rear is the same mistake - said once, as the more precise against-the-flow notice.
        Assert.Empty(result.MixedSides);
        Assert.DoesNotContain(AirflowHint.MixedSide, result.Hints);
    }

    [Fact]
    public void Server_WithOnlyAFanWall_IsSetUp_NotMissingCaseFans()
    {
        AppConfig config = ConfigWith(Fan("wall", FanLocation.FanWall), Fan("cpu", FanLocation.CpuCooler))
            with
        { CaseType = CaseType.Server };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.FanWallOnly, result.Verdict);
        Assert.DoesNotContain(AirflowHint.NoCaseFans, result.Hints);
        Assert.DoesNotContain(AirflowHint.NoIntakeFan, result.Hints);
    }

    [Fact]
    public void Laptop_HasNoBalance_AndNoMixedVents_ButNamesWhatItLacks()
    {
        AppConfig config = ConfigWith(
            Fan("cpu", FanLocation.CpuCooler), Fan("gpu", FanLocation.GpuCooler), Fan("front", FanLocation.CaseFrontIntake),
            Fan("in", FanLocation.CaseRearIntake), Fan("out", FanLocation.CaseRearExhaust))
            with
        { CaseType = CaseType.Laptop };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(CaseType.Laptop, result.CaseType);
        Assert.Equal(PressureVerdict.NotApplicable, result.Verdict);
        Assert.Null(result.IntakeShare);
        Assert.DoesNotContain(AirflowHint.NoCaseFans, result.Hints);
        Assert.DoesNotContain(AirflowHint.CountEstimateOnly, result.Hints);
        Assert.Equal(new[] { "front" }, result.OutOfCaseFanIds);
        Assert.Empty(result.MixedSides); // the schema still shows the rear as mixed; without a balance there is nothing to warn
        Assert.DoesNotContain(AirflowHint.MixedSide, result.Hints);
        Assert.Empty(result.AgainstFlowFanIds); // only a server has a usual flow direction
        // The blowers keep their component curves, the vents their role curves.
        Assert.Equal(AirflowTuneService.CpuCurveId, result.Fans.Single(f => f.FanId == "cpu").SuggestedCurveId);
        Assert.Equal(AirflowTuneService.ExhaustCurveId, result.Fans.Single(f => f.FanId == "out").SuggestedCurveId);
    }

    [Fact]
    public void Laptop_WithItsTwoBlowersOnly_HasNoCaseHint()
    {
        AppConfig config = ConfigWith(Fan("cpu", FanLocation.CpuCooler), Fan("gpu", FanLocation.GpuCooler))
            with
        { CaseType = CaseType.Laptop, Sensors = [new SensorConfig { SensorId = "k10temp/temp1", Name = "Tctl" }] };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureVerdict.NotApplicable, result.Verdict);
        Assert.Empty(result.Hints);
    }

    [Fact]
    public void Tower_HasNoUsualFlowDirection()
    {
        AppConfig config = ConfigWith(Fan("front", FanLocation.CaseFrontExhaust), Fan("rear", FanLocation.CaseRearIntake));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Empty(result.AgainstFlowFanIds);
        Assert.DoesNotContain(AirflowHint.AgainstCaseFlow, result.Hints);
    }

    [Fact]
    public void HiddenFans_StayOutOfTheCaseTypeHints()
    {
        AppConfig config = ConfigWith(Fan("top", FanLocation.CaseTopExhaust) with { Hidden = true })
            with
        { CaseType = CaseType.Server };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Empty(result.OutOfCaseFanIds);
        Assert.DoesNotContain(AirflowHint.PositionNotInCase, result.Hints);
    }

    // ── Aggressiveness variants + BuildProfiles (airflow-driven onboarding profiles) ────────────

    /// <summary>Config using every curve-producing role (CPU, GPU, intake, exhaust, default) + PSU.</summary>
    private static AppConfig AllRolesConfig() => ConfigWith(
        Fan("cpu", FanLocation.CpuCooler),
        Fan("gpu", FanLocation.GpuCooler),
        Fan("front", FanLocation.CaseFrontIntake),
        Fan("rear", FanLocation.CaseRearExhaust),
        Fan("loose", FanLocation.Unspecified),
        Fan("psu", FanLocation.Psu));

    private static string[] AllRolesFanIds() => ["cpu", "gpu", "front", "rear", "loose", "psu"];

    [Fact]
    public void Analyze_DefaultOverload_EqualsBalancedVariant()
    {
        AppConfig config = AllRolesConfig();

        AirflowTuneResult byDefault = AirflowTuneService.Analyze(config);
        AirflowTuneResult balanced = AirflowTuneService.Analyze(config, AirflowAggressiveness.Balanced);

        // Pins the default overload's forwarding target: the settings' airflow section (default
        // overload) must keep getting the Balanced variant - a change of the default would fail here.
        Assert.Equal(balanced.SuggestedCurves.Count, byDefault.SuggestedCurves.Count);
        foreach ((CurveConfig d, CurveConfig b) in byDefault.SuggestedCurves.Zip(balanced.SuggestedCurves))
        {
            Assert.Equal(b.Id, d.Id);
            Assert.Equal(b.Points.Select(p => (p.TemperatureC, p.Percent)),
                         d.Points.Select(p => (p.TemperatureC, p.Percent)));
        }
    }

    [Fact]
    public void BuildProfiles_ReturnsSilentBalancedPerformanceInOrder_WithStableIds()
    {
        IReadOnlyList<Profile> profiles = AirflowTuneService.BuildProfiles(AllRolesConfig(), AllRolesFanIds());

        Assert.Equal(new[] { "silent", "balanced", "performance" }, profiles.Select(p => p.Id));
        foreach (Profile profile in profiles)
        {
            // Stable airflow-* curve ids in every profile → assignments survive profile switches.
            Assert.Equal(new[] { "airflow-cpu", "airflow-gpu", "airflow-intake", "airflow-exhaust", "airflow-default" }
                    .OrderBy(id => id),
                profile.Curves.Select(c => c.Id).OrderBy(id => id));
            Assert.Equal("airflow-cpu", profile.Assignments.Single(a => a.FanId == "cpu").CurveId);
            Assert.Equal("airflow-exhaust", profile.Assignments.Single(a => a.FanId == "rear").CurveId);
        }
    }

    [Fact]
    public void BuildProfiles_BalancedProfileCurves_MatchAnalyzeSuggestedCurves()
    {
        AppConfig config = AllRolesConfig();

        Profile balanced = AirflowTuneService.BuildProfiles(config, AllRolesFanIds()).Single(p => p.Id == "balanced");
        AirflowTuneResult analyzed = AirflowTuneService.Analyze(config);

        foreach (CurveConfig expected in analyzed.SuggestedCurves)
        {
            CurveConfig actual = balanced.Curves.Single(c => c.Id == expected.Id);
            Assert.Equal(expected.Points.Select(p => (p.TemperatureC, p.Percent)),
                         actual.Points.Select(p => (p.TemperatureC, p.Percent)));
        }
    }

    [Fact]
    public void BuildProfiles_EveryVariant_MonotonicAndFullSpeedBeforeFailSafe()
    {
        IReadOnlyList<Profile> profiles = AirflowTuneService.BuildProfiles(AllRolesConfig(), AllRolesFanIds());

        Assert.All(profiles, profile => Assert.All(profile.Curves, curve =>
        {
            Assert.NotEmpty(curve.Points);
            Assert.Equal(100, curve.Points[^1].Percent);
            // Full speed clearly below the watchdog default (FailSafeTempC 90 °C) in EVERY variant:
            // Max curves by 86 °C; Avg component curves earlier (82 °C) because the average lags the
            // hottest sensor under offset spread (Tctl/hot spot).
            double headroom = curve.Aggregation == SensorAggregation.Avg ? 8 : 4;
            Assert.True(curve.Points[^1].TemperatureC <= AppConfig.Empty.FailSafeTempC - headroom,
                $"{profile.Id}/{curve.Id} reaches 100 % only at {curve.Points[^1].TemperatureC} °C");
            for (int i = 1; i < curve.Points.Count; i++)
            {
                Assert.True(curve.Points[i].TemperatureC > curve.Points[i - 1].TemperatureC);
                Assert.True(curve.Points[i].Percent >= curve.Points[i - 1].Percent);
            }
        }));
    }

    [Fact]
    public void BuildProfiles_VariantOrdering_SilentNeverAboveBalanced_BalancedNeverAbovePerformance()
    {
        IReadOnlyList<Profile> profiles = AirflowTuneService.BuildProfiles(AllRolesConfig(), AllRolesFanIds());
        Profile silent = profiles.Single(p => p.Id == "silent");
        Profile balanced = profiles.Single(p => p.Id == "balanced");
        Profile performance = profiles.Single(p => p.Id == "performance");

        static double Eval(Profile profile, string curveId, double temp)
        {
            CurveConfig c = profile.Curves.Single(x => x.Id == curveId);
            return CurveEngine.Evaluate(new Curve(c.Name, c.Points, c.InterpolationMode), temp);
        }

        foreach (string curveId in balanced.Curves.Select(c => c.Id))
        {
            // Union of all breakpoints of the three variants plus points beyond both ends: with
            // linear interpolation the pairwise difference is piecewise linear, so ordering at every
            // breakpoint proves it at every temperature.
            double[] temps = new[] { silent, balanced, performance }
                .Select(p => p.Curves.Single(x => x.Id == curveId))
                .SelectMany(c => c.Points.Select(pt => pt.TemperatureC))
                .Concat([20.0, 95.0])
                .Distinct()
                .OrderBy(t => t)
                .ToArray();

            foreach (double temp in temps)
            {
                double s = Eval(silent, curveId, temp);
                double b = Eval(balanced, curveId, temp);
                double p = Eval(performance, curveId, temp);
                Assert.True(s <= b, $"{curveId}@{temp} °C: silent {s} > balanced {b}");
                Assert.True(b <= p, $"{curveId}@{temp} °C: balanced {b} > performance {p}");
            }
        }
    }

    [Fact]
    public void BuildProfiles_RestrictsAssignmentsAndCurvesToGivenFanIds()
    {
        // "gpu" is not assignable (e.g. read-only channel): no assignment, and the now-unreferenced
        // GPU curve drops out; the pressure balance still counted every visible fan beforehand.
        IReadOnlyList<Profile> profiles = AirflowTuneService.BuildProfiles(
            AllRolesConfig(), new[] { "cpu", "front", "rear" });

        Assert.All(profiles, profile =>
        {
            Assert.Equal(new[] { "cpu", "front", "rear" }, profile.Assignments.Select(a => a.FanId).OrderBy(id => id));
            Assert.DoesNotContain(profile.Curves, c => c.Id == "airflow-gpu");
            Assert.DoesNotContain(profile.Curves, c => c.Id == "airflow-default");
        });
    }

    [Fact]
    public void BuildProfiles_PsuFan_GetsNullAssignment()
    {
        IReadOnlyList<Profile> profiles = AirflowTuneService.BuildProfiles(AllRolesConfig(), AllRolesFanIds());

        Assert.All(profiles, profile =>
            Assert.Null(profile.Assignments.Single(a => a.FanId == "psu").CurveId));
    }

    [Fact]
    public void BuildProfiles_LocalizedProfileAndCurveNames_ReachOutput()
    {
        IReadOnlyList<Profile> profiles = AirflowTuneService.BuildProfiles(
            AllRolesConfig(), AllRolesFanIds(),
            curveNames: new Dictionary<string, string> { ["airflow-cpu"] = "Luftstrom · CPU" },
            silentName: "Leise", balancedName: "Ausgewogen", performanceName: "Leistung");

        Assert.Equal(new[] { "Leise", "Ausgewogen", "Leistung" }, profiles.Select(p => p.Name));
        Assert.All(profiles, profile =>
            Assert.Equal("Luftstrom · CPU", profile.Curves.Single(c => c.Id == "airflow-cpu").Name));
    }

    [Fact]
    public void BuildProfiles_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.BuildProfiles(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.BuildProfiles(AppConfig.Empty, null!));
    }

    [Theory]
    [InlineData(FanLocation.CpuCooler, true)]
    [InlineData(FanLocation.Radiator, true)]
    [InlineData(FanLocation.GpuCooler, true)]
    [InlineData(FanLocation.CaseFrontIntake, true)]
    [InlineData(FanLocation.CaseBottomIntake, true)]
    [InlineData(FanLocation.CaseSideIntake, true)]
    [InlineData(FanLocation.CaseTopIntake, true)]
    [InlineData(FanLocation.CaseRearIntake, true)]
    [InlineData(FanLocation.CaseRearExhaust, true)]
    [InlineData(FanLocation.CaseTopExhaust, true)]
    [InlineData(FanLocation.CaseFrontExhaust, true)]
    [InlineData(FanLocation.CaseBottomExhaust, true)]
    [InlineData(FanLocation.CaseSideExhaust, true)]
    [InlineData(FanLocation.Psu, false)]
    [InlineData(FanLocation.Unspecified, false)]
    [InlineData(FanLocation.Other, false)]
    public void HasRoleSpecificCurve_Matrix(FanLocation location, bool expected)
    {
        Assert.Equal(expected, AirflowTuneService.HasRoleSpecificCurve(location));
    }
}
