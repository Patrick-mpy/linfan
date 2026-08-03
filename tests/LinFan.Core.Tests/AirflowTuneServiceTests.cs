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

        Assert.Equal(PressureBalance.Positive, result.Pressure);
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

        Assert.Equal(PressureBalance.Negative, result.Pressure);
        Assert.Contains(AirflowHint.NegativePressure, result.Hints);
    }

    [Fact]
    public void Analyze_EqualIntakeAndExhaust_IsBalanced()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CaseFrontIntake),
            Fan("f2", FanLocation.CaseRearExhaust));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureBalance.Balanced, result.Pressure);
    }

    [Fact]
    public void Analyze_NoCaseFans_PressureUnknown()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CpuCooler),
            Fan("f2", FanLocation.GpuCooler));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureBalance.Unknown, result.Pressure);
        Assert.Contains(AirflowHint.NoCaseFans, result.Hints);
    }

    [Fact]
    public void Analyze_OnlyExhaust_WarnsNoIntake()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CaseRearExhaust));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureBalance.Negative, result.Pressure);
        Assert.Contains(AirflowHint.NoIntakeFan, result.Hints);
    }

    [Fact]
    public void Analyze_OnlyIntake_WarnsNoExhaust()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CaseFrontIntake));

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        Assert.Equal(PressureBalance.Positive, result.Pressure);
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

        Assert.Equal(PressureBalance.Positive, result.Pressure);
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
        Assert.Equal(PressureBalance.Balanced, result.Pressure);
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

    // ── D · Sensor-Zuordnung (Namens-Heuristik) ─────────────────────────────────

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

    // ── E · Apply ───────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_SetsAssignedCurveAndInsertsCurves()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CpuCooler));
        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        AppConfig applied = AirflowTuneService.Apply(config, result);

        Assert.Equal("airflow-cpu", applied.Fans.Single().AssignedCurveId);
        Assert.Contains(applied.Curves, c => c.Id == "airflow-cpu");
    }

    [Fact]
    public void Apply_PreservesCalibrationLimitsAndPlacement()
    {
        var fan = new FanConfig
        {
            FanId = "f1",
            Name = "Test",
            Location = FanLocation.CpuCooler,
            MinPwm = 50,
            MaxPwm = 200,
            Hidden = true,
            Group = "Oben",
            Calibration = Cal(2500),
        };
        AppConfig config = ConfigWith(fan);
        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        FanConfig applied = AirflowTuneService.Apply(config, result).Fans.Single();

        Assert.Equal((byte)50, applied.MinPwm);
        Assert.Equal((byte)200, applied.MaxPwm);
        Assert.True(applied.Hidden);
        Assert.Equal("Oben", applied.Group);
        Assert.Equal(FanLocation.CpuCooler, applied.Location);
        Assert.Equal(2500, applied.Calibration!.MaxRpm);
    }

    [Fact]
    public void Apply_IsIdempotent_NoDuplicateCurves()
    {
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CpuCooler));
        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        AppConfig once = AirflowTuneService.Apply(config, result);
        AppConfig twice = AirflowTuneService.Apply(once, AirflowTuneService.Analyze(once));

        Assert.Equal(once.Curves.Count, twice.Curves.Count);
        Assert.Single(twice.Curves, c => c.Id == "airflow-cpu");
    }

    [Fact]
    public void Apply_SyncsActiveProfile()
    {
        var fan = Fan("f1", FanLocation.CpuCooler);
        var profile = new Profile
        {
            Id = "p1",
            Name = "Profil",
            Curves = Array.Empty<CurveConfig>(),
            Assignments = new[] { new ProfileAssignment("f1", null) },
        };
        AppConfig config = AppConfig.Empty with
        {
            Fans = new[] { fan },
            Profiles = new[] { profile },
            ActiveProfileId = "p1",
        };
        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        AppConfig applied = AirflowTuneService.Apply(config, result);

        Profile activeProfile = applied.Profiles.Single(p => p.Id == "p1");
        Assert.Contains(activeProfile.Curves, c => c.Id == "airflow-cpu");
        Assert.Equal("airflow-cpu", activeProfile.Assignments.Single(a => a.FanId == "f1").CurveId);
    }

    [Fact]
    public void Apply_ReplacesExistingCurveWithSameId()
    {
        var stale = new CurveConfig { Id = "airflow-cpu", Name = "Alt", Points = new[] { new CurvePoint(0, 0) } };
        AppConfig config = ConfigWith(Fan("f1", FanLocation.CpuCooler)) with { Curves = new[] { stale } };
        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        AppConfig applied = AirflowTuneService.Apply(config, result);

        CurveConfig curve = applied.Curves.Single(c => c.Id == "airflow-cpu");
        Assert.NotEqual("Alt", curve.Name);
        Assert.True(curve.Points.Count > 1);
    }

    // ── Grenzfälle & Guards ─────────────────────────────────────────────────────

    [Fact]
    public void Analyze_EmptyConfig_NoThrowAndUnknownPressure()
    {
        AirflowTuneResult result = AirflowTuneService.Analyze(AppConfig.Empty);

        Assert.Equal(PressureBalance.Unknown, result.Pressure);
        Assert.Empty(result.Fans);
        Assert.Empty(result.SuggestedCurves);
    }

    [Fact]
    public void Analyze_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.Analyze(null!));
    }

    [Fact]
    public void Apply_NullArguments_Throw()
    {
        AirflowTuneResult result = AirflowTuneService.Analyze(AppConfig.Empty);

        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.Apply(null!, result));
        Assert.Throws<ArgumentNullException>(() => AirflowTuneService.Apply(AppConfig.Empty, null!));
    }

    [Fact]
    public void Analyze_IsDeterministic()
    {
        AppConfig config = ConfigWith(
            Fan("f1", FanLocation.CpuCooler),
            Fan("f2", FanLocation.CaseRearExhaust));

        AirflowTuneResult a = AirflowTuneService.Analyze(config);
        AirflowTuneResult b = AirflowTuneService.Analyze(config);

        Assert.Equal(a.Pressure, b.Pressure);
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
}
