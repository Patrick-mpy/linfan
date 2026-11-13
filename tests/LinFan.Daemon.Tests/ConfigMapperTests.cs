// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.Daemon.Tests;

public class ConfigMapperTests
{
    private static FanCalibration Cal(byte startPwm, int minRpm, int maxRpm) => new()
    {
        StartPwm = startPwm,
        MinRpm = minRpm,
        MaxRpm = maxRpm,
        Samples = new[] { new CalibrationSample(startPwm, minRpm) },
    };

    private static IpcConfig IpcWith(params IpcFanAssignment[] fans) => IpcConfig.Empty with
    {
        Fans = fans,
    };

    // --- Merge: Kalibrierung & nicht editierte Felder bleiben erhalten ---------------------------

    [Fact]
    public void Merge_PreservesCalibration_OnFanUpdate()
    {
        var cal = Cal(96, 400, 1800);
        var current = new AppConfig
        {
            Fans = new[]
            {
                new FanConfig { FanId = "f1", Name = "Alt", MinPwm = 96, MaxPwm = 200, Calibration = cal },
            },
        };
        var incoming = IpcWith(new IpcFanAssignment("f1", "Neu", 10, 250, "curve-1"));

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        FanConfig fan = Assert.Single(merged.Fans);
        Assert.Equal("Neu", fan.Name);                 // editiertes Feld übernommen
        Assert.Equal("curve-1", fan.AssignedCurveId);
        Assert.Same(cal, fan.Calibration);             // Kalibrierung unangetastet
    }

    // --- Merge: MinPwm three ways against the sender's base -----------------------------------------

    private static AppConfig CalibratedTo(byte minPwm) => new()
    {
        Fans = new[] { new FanConfig { FanId = "f1", Name = "F", MinPwm = minPwm, Calibration = Cal(minPwm, 400, 1800) } },
    };

    [Fact]
    public void Merge_MinPwmTheSenderLeftAlone_KeepsTheCalibratedStartPoint()
    {
        // The GUI built its save from MinPwm 40; the calibration set 96 before the save arrived.
        var incoming = IpcWith(new IpcFanAssignment("f1", "F", 40, 255, null, BaseMinPwm: 40));

        FanConfig fan = Assert.Single(ConfigMapper.Merge(CalibratedTo(96), incoming).Fans);

        Assert.Equal((byte)96, fan.MinPwm);
    }

    [Fact]
    public void Merge_MinPwmTheSenderChanged_Wins()
    {
        var incoming = IpcWith(new IpcFanAssignment("f1", "F", 60, 255, null, BaseMinPwm: 40));

        FanConfig fan = Assert.Single(ConfigMapper.Merge(CalibratedTo(96), incoming).Fans);

        Assert.Equal((byte)60, fan.MinPwm);
    }

    [Fact]
    public void Merge_WithoutABase_TheSentMinPwmWins()
    {
        // An older GUI sends no base - as before, its value is taken.
        var incoming = IpcWith(new IpcFanAssignment("f1", "F", 40, 255, null));

        FanConfig fan = Assert.Single(ConfigMapper.Merge(CalibratedTo(96), incoming).Fans);

        Assert.Equal((byte)40, fan.MinPwm);
    }

    [Fact]
    public void Merge_ANewFanWithABase_TakesTheSentMinPwm()
    {
        var incoming = IpcWith(new IpcFanAssignment("new", "F", 40, 255, null, BaseMinPwm: 40));

        FanConfig fan = Assert.Single(ConfigMapper.Merge(AppConfig.Empty, incoming).Fans);

        Assert.Equal((byte)40, fan.MinPwm); // the daemon has no value of its own to keep
    }

    [Fact]
    public void Merge_ComparesTheBaseAfterClamping()
    {
        // Out-of-range values are clamped before the comparison: -5 and 0 are both "0, unchanged".
        var incoming = IpcWith(new IpcFanAssignment("f1", "F", -5, 255, null, BaseMinPwm: 0));

        FanConfig fan = Assert.Single(ConfigMapper.Merge(CalibratedTo(96), incoming).Fans);

        Assert.Equal((byte)96, fan.MinPwm);
    }

    [Fact]
    public void Merge_ClampsMinAndMaxPwm_To_0_255()
    {
        var current = AppConfig.Empty;
        var incoming = IpcWith(new IpcFanAssignment("f1", "F", MinPwm: -50, MaxPwm: 999, null));

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        FanConfig fan = Assert.Single(merged.Fans);
        Assert.Equal((byte)0, fan.MinPwm);
        Assert.Equal((byte)255, fan.MaxPwm);
    }

    [Theory]
    [InlineData("CaseRearExhaust", FanLocation.CaseRearExhaust)]
    [InlineData("CpuCooler", FanLocation.CpuCooler)]
    [InlineData("Unspecified", FanLocation.Unspecified)]
    public void Merge_ParsesValidFanLocation(string raw, FanLocation expected)
    {
        var incoming = IpcWith(new IpcFanAssignment("f1", "F", 0, 255, null, Location: raw));

        AppConfig merged = ConfigMapper.Merge(AppConfig.Empty, incoming);

        Assert.Equal(expected, Assert.Single(merged.Fans).Location);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Garbage")]
    [InlineData("not-a-location")]
    [InlineData("999")] // Enum.TryParse alone would persist an undefined numeric value
    [InlineData("-1")]
    public void Merge_And_Replace_InvalidFanLocation_FallsBackToUnspecified(string raw)
    {
        var incoming = IpcWith(new IpcFanAssignment("f1", "F", 0, 255, null, Location: raw));

        Assert.Equal(FanLocation.Unspecified, Assert.Single(ConfigMapper.Merge(AppConfig.Empty, incoming).Fans).Location);
        Assert.Equal(FanLocation.Unspecified, Assert.Single(ConfigMapper.Replace(AppConfig.Empty, incoming).Fans).Location);
    }

    [Fact]
    public void Merge_KeepsUnknownFansFromCurrent()
    {
        var current = new AppConfig
        {
            Fans = new[]
            {
                new FanConfig { FanId = "kept", Name = "Bleibt", MinPwm = 30 },
                new FanConfig { FanId = "f1", Name = "Alt" },
            },
        };
        var incoming = IpcWith(new IpcFanAssignment("f1", "Neu", 0, 255, null));

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        Assert.Equal(2, merged.Fans.Count);
        FanConfig kept = merged.Fans.Single(f => f.FanId == "kept");
        Assert.Equal("Bleibt", kept.Name);             // unverändert
        Assert.Equal((byte)30, kept.MinPwm);
        Assert.Equal("Neu", merged.Fans.Single(f => f.FanId == "f1").Name);
    }

    [Fact]
    public void Merge_EmptyIncomingSensors_KeepsCurrentSensors()
    {
        var current = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "s1", Name = "CPU", Group = "Zone" } },
        };
        var incoming = IpcConfig.Empty; // keine Sensoren

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        SensorConfig s = Assert.Single(merged.Sensors);
        Assert.Equal("CPU", s.Name);
        Assert.Equal("Zone", s.Group);
    }

    [Fact]
    public void Merge_TakesTheSentSensors_AndKeepsTheOnesNotSent()
    {
        var current = new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "s1", Name = "Vorher" },
                new SensorConfig { SensorId = "old", Name = "Alt", Group = "GPU" }, // a device that is off right now
            },
        };
        var incoming = IpcConfig.Empty with
        {
            Sensors = new[] { new IpcSensorName("s1", " CPU ", " Zone ", Hidden: true) },
        };

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        Assert.Equal(new[] { "s1", "old" }, merged.Sensors.Select(s => s.SensorId));
        SensorConfig s = merged.Sensors[0];
        Assert.Equal("CPU", s.Name);                   // getrimmt
        Assert.Equal("Zone", s.Group);                 // getrimmt
        Assert.True(s.Hidden);
        Assert.Equal(("Alt", "GPU"), (merged.Sensors[1].Name, merged.Sensors[1].Group)); // name and group come back with it
    }

    [Fact]
    public void Merge_KeepsTheRecordedDeviceOfASentSensor()
    {
        // The device is daemon-owned: a save from the GUI, which knows nothing of it, must not wipe it.
        var gpu = new HardwareDevice("amdgpu", "pci:1002:73bf:1849:5201");
        var current = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "s1", Name = "Vorher", Device = gpu } },
            Fans = new[] { new FanConfig { FanId = "f1", Device = gpu } },
        };
        var incoming = IpcWith(new IpcFanAssignment("f1", "GPU", 0)) with
        {
            Sensors = new[] { new IpcSensorName("s1", "Hotspot", "GPU", Hidden: true), new IpcSensorName("new", "Neu") },
        };

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        Assert.Equal(new SensorConfig { SensorId = "s1", Name = "Hotspot", Group = "GPU", Hidden = true, Device = gpu },
            merged.Sensors[0]);
        Assert.Null(merged.Sensors.Single(s => s.SensorId == "new").Device); // the daemon records it when it normalizes
        Assert.Equal(gpu, Assert.Single(merged.Fans).Device);
    }

    [Fact]
    public void Merge_TakesCurvesProfilesAndActiveProfile()
    {
        var current = AppConfig.Empty with { ActiveProfileId = "old" };
        var incoming = IpcConfig.Empty with
        {
            Curves = new[]
            {
                new IpcCurve("c1", "Silent", "s1", 3.0, new[] { new IpcCurvePoint(40, 20), new IpcCurvePoint(70, 100) }),
            },
            Profiles = new[]
            {
                new IpcProfile("p1", "Performance",
                    new[] { new IpcProfileAssignment("f1", "c1") },
                    new[] { new IpcCurve("pc1", "Aggr", "s1", 1.0, new[] { new IpcCurvePoint(30, 50) }) }),
            },
            ActiveProfileId = "p1",
        };

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        CurveConfig curve = Assert.Single(merged.Curves);
        Assert.Equal("c1", curve.Id);
        Assert.Equal("Silent", curve.Name);
        Assert.Equal(3.0, curve.HysteresisC);
        // Ältere GUI kennt das Feld nicht (null) → Core-Default statt still 0 (Glättung aus).
        Assert.Equal(CurveConfig.DefaultSmoothingSeconds, curve.SmoothingSeconds);
        Assert.Equal(2, curve.Points.Count);

        Profile profile = Assert.Single(merged.Profiles);
        Assert.Equal("p1", profile.Id);
        Assert.Equal("Performance", profile.Name);
        Assert.Equal("c1", Assert.Single(profile.Assignments).CurveId);
        Assert.Equal("pc1", Assert.Single(profile.Curves).Id);

        Assert.Equal("p1", merged.ActiveProfileId);
    }

    // --- ToIpc / Roundtrip ----------------------------------------------------------------------

    [Fact]
    public void ToIpc_Then_Merge_RoundTrips_EditableFields()
    {
        var original = new AppConfig
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Silent", SourceSensorId = "s1", HysteresisC = 2.5, SmoothingSeconds = 8,
                    Points = new[] { new CurvePoint(40, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[]
            {
                new FanConfig
                {
                    FanId = "f1", Name = "CPU-Lüfter", MinPwm = 40, MaxPwm = 230,
                    AssignedCurveId = "c1", Location = FanLocation.CpuCooler, Hidden = true,
                },
            },
            Sensors = new[] { new SensorConfig { SensorId = "s1", Name = "CPU", Group = "Zone", Hidden = false } },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p1", Name = "Default",
                    Assignments = new[] { new ProfileAssignment("f1", "c1") },
                    Curves = new[]
                    {
                        new CurveConfig { Id = "pc1", Name = "P", SourceSensorId = "s1", HysteresisC = 1, Points = new[] { new CurvePoint(30, 40) } },
                    },
                },
            },
            ActiveProfileId = "p1",
        };

        IpcConfig ipc = ConfigMapper.ToIpc(original);
        AppConfig back = ConfigMapper.Merge(AppConfig.Empty, ipc);

        FanConfig fan = Assert.Single(back.Fans);
        Assert.Equal("f1", fan.FanId);
        Assert.Equal("CPU-Lüfter", fan.Name);
        Assert.Equal((byte)40, fan.MinPwm);
        Assert.Equal((byte)230, fan.MaxPwm);
        Assert.Equal("c1", fan.AssignedCurveId);
        Assert.Equal(FanLocation.CpuCooler, fan.Location);
        Assert.True(fan.Hidden);

        CurveConfig curve = Assert.Single(back.Curves);
        Assert.Equal("Silent", curve.Name);
        Assert.Equal(new[] { "s1" }, curve.SourceSensorIds); // altes Einzelfeld → Mehrfach-Quelle migriert
        Assert.Equal(SensorAggregation.Max, curve.Aggregation); // Default-Aggregation
        Assert.Equal(2.5, curve.HysteresisC);
        Assert.Equal(8, curve.SmoothingSeconds);
        Assert.Equal(2, curve.Points.Count);

        SensorConfig sensor = Assert.Single(back.Sensors);
        Assert.Equal("CPU", sensor.Name);
        Assert.Equal("Zone", sensor.Group);

        Profile profile = Assert.Single(back.Profiles);
        Assert.Equal("Default", profile.Name);
        Assert.Equal("p1", back.ActiveProfileId);
    }

    [Fact]
    public void ToIpc_MapsCalibration_SoBadgeSurvivesRestart()
    {
        var config = new AppConfig
        {
            Fans = new[]
            {
                new FanConfig { FanId = "f1", Name = "CPU", MinPwm = 96, Calibration = Cal(96, 400, 1800) },
                new FanConfig { FanId = "f2", Name = "Gehäuse" }, // nie kalibriert
            },
        };

        IpcConfig ipc = ConfigMapper.ToIpc(config);

        IpcFanCalibration? cal = ipc.Fans.Single(f => f.FanId == "f1").Calibration;
        Assert.NotNull(cal);
        Assert.Equal(96, cal!.StartPwm);     // Anlaufpunkt für das Badge
        Assert.Equal(400, cal.MinRpm);
        Assert.Equal(1800, cal.MaxRpm);

        Assert.Null(ipc.Fans.Single(f => f.FanId == "f2").Calibration); // nicht kalibriert → kein DTO
    }

    // --- Mehr-Sensor-Mix für Kurven (Schema 2) --------------------------------------------------

    [Fact]
    public void Curve_MultiSource_And_Aggregation_RoundTrip()
    {
        var original = AppConfig.Empty with
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Mix",
                    SourceSensorIds = new[] { "s1", "s2", "s3" },
                    Aggregation = SensorAggregation.Avg,
                    HysteresisC = 2.0,
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
        };

        IpcConfig ipc = ConfigMapper.ToIpc(original);
        // Auf dem Draht müssen die neuen Felder ankommen (sonst divergiert GUI vs. Daemon).
        IpcCurve wire = Assert.Single(ipc.Curves);
        Assert.Equal(new[] { "s1", "s2", "s3" }, wire.SourceSensorIds);
        Assert.Equal("Avg", wire.Aggregation);
        Assert.Equal("s1", wire.SourceSensorId); // erste Quelle ins alte Feld gespiegelt (Abwärtskompat.)

        AppConfig back = ConfigMapper.Merge(AppConfig.Empty, ipc);
        CurveConfig curve = Assert.Single(back.Curves);
        Assert.Equal(new[] { "s1", "s2", "s3" }, curve.SourceSensorIds);
        Assert.Equal(SensorAggregation.Avg, curve.Aggregation);
    }

    [Fact]
    public void ToCoreCurve_OldClientSendsOnlySourceSensorId_MigratesToSourceSensorIds()
    {
        // Ein älterer GUI-Client kennt SourceSensorIds/Aggregation noch nicht → nur das alte Feld.
        var incoming = IpcConfig.Empty with
        {
            Curves = new[]
            {
                new IpcCurve("c1", "Alt", "s7", 2.0, new[] { new IpcCurvePoint(40, 30) }),
            },
        };

        AppConfig merged = ConfigMapper.Merge(AppConfig.Empty, incoming);

        CurveConfig curve = Assert.Single(merged.Curves);
        Assert.Equal(new[] { "s7" }, curve.SourceSensorIds);   // aus dem alten Feld migriert
        Assert.Equal(SensorAggregation.Max, curve.Aggregation); // unbekannte Aggregation → sicherer Default
    }

    [Fact]
    public void ToCoreCurve_UnknownAggregationString_FallsBackToMax()
    {
        var incoming = IpcConfig.Empty with
        {
            Curves = new[]
            {
                new IpcCurve("c1", "X", "s1", 2.0, new[] { new IpcCurvePoint(40, 30) },
                    SourceSensorIds: new[] { "s1" }, Aggregation: "Garbage"),
            },
        };

        AppConfig merged = ConfigMapper.Merge(AppConfig.Empty, incoming);

        Assert.Equal(SensorAggregation.Max, Assert.Single(merged.Curves).Aggregation);
    }

    // --- InterpolationMode-Roundtrip ------------------------------------------------------------

    [Theory]
    [InlineData(InterpolationMode.Linear)]
    [InlineData(InterpolationMode.Spline)]
    public void InterpolationMode_SurvivesCore_Ipc_Core_Roundtrip(InterpolationMode mode)
    {
        var original = AppConfig.Empty with
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Test",
                    SourceSensorIds = new[] { "s1" },
                    InterpolationMode = mode,
                    HysteresisC = 2.0,
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
        };

        IpcConfig ipc = ConfigMapper.ToIpc(original);
        // Modus muss auf dem Draht ankommen.
        IpcCurve wire = Assert.Single(ipc.Curves);
        Assert.Equal(mode.ToString(), wire.InterpolationMode);

        AppConfig back = ConfigMapper.Merge(AppConfig.Empty, ipc);
        Assert.Equal(mode, Assert.Single(back.Curves).InterpolationMode);
    }

    [Fact]
    public void InterpolationMode_Null_DefaultsToLinear()
    {
        // Älterer Daemon/GUI schickt das Feld nicht → null → sicherer Default.
        var incoming = IpcConfig.Empty with
        {
            Curves = new[]
            {
                new IpcCurve("c1", "Alt", "s1", 2.0, new[] { new IpcCurvePoint(40, 30) },
                    InterpolationMode: null),
            },
        };

        AppConfig merged = ConfigMapper.Merge(AppConfig.Empty, incoming);

        Assert.Equal(InterpolationMode.Linear, Assert.Single(merged.Curves).InterpolationMode);
    }

    [Fact]
    public void InterpolationMode_InvalidString_DefaultsToLinear()
    {
        var incoming = IpcConfig.Empty with
        {
            Curves = new[]
            {
                new IpcCurve("c1", "Bad", "s1", 2.0, new[] { new IpcCurvePoint(40, 30) },
                    InterpolationMode: "Garbage"),
            },
        };

        AppConfig merged = ConfigMapper.Merge(AppConfig.Empty, incoming);

        Assert.Equal(InterpolationMode.Linear, Assert.Single(merged.Curves).InterpolationMode);
    }

    // --- OnboardingCompleted-Flag ---------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnboardingCompleted_SurvivesCore_Ipc_Core_Roundtrip(bool completed)
    {
        var original = AppConfig.Empty with { OnboardingCompleted = completed };

        IpcConfig ipc = ConfigMapper.ToIpc(original);
        Assert.Equal(completed, ipc.OnboardingCompleted);

        AppConfig back = ConfigMapper.Merge(AppConfig.Empty with { OnboardingCompleted = false }, ipc);
        Assert.Equal(completed, back.OnboardingCompleted);
    }

    [Fact]
    public void Merge_OnboardingCompletedNull_KeepsCurrentDaemonState()
    {
        // Eine ältere GUI kennt das Feld nicht (null) → darf den autoritativen Daemon-Stand nicht zurücksetzen.
        var current = AppConfig.Empty with { OnboardingCompleted = true };
        IpcConfig incoming = IpcConfig.Empty with { OnboardingCompleted = null };

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        Assert.True(merged.OnboardingCompleted);
    }

    // --- Sensor group list ----------------------------------------------------------------------

    [Fact]
    public void ToIpc_Then_Merge_CarriesTheSensorGroupsInOrder()
    {
        var desired = AppConfig.Empty with { SensorGroups = new[] { "GPU", "Leer", "CPU" } };

        IpcConfig ipc = ConfigMapper.ToIpc(desired);
        AppConfig merged = ConfigMapper.Merge(AppConfig.Empty with { SensorGroups = new[] { "Alt" } }, ipc);

        Assert.Equal(new[] { "GPU", "Leer", "CPU" }, ipc.SensorGroups);
        Assert.Equal(new[] { "GPU", "Leer", "CPU" }, merged.SensorGroups);
    }

    [Fact]
    public void Merge_SensorGroupsNull_KeepsTheDaemonsList()
    {
        // A GUI that predates the field sends nothing and must not wipe the groups another client set up.
        var current = AppConfig.Empty with { SensorGroups = new[] { "CPU", "Leer" } };

        AppConfig merged = ConfigMapper.Merge(current, IpcConfig.Empty with { SensorGroups = null });

        Assert.Equal(new[] { "CPU", "Leer" }, merged.SensorGroups);
    }

    [Fact]
    public void Merge_EmptySensorGroups_ClearsTheList()
    {
        // Unlike the sensors (empty = "sent nothing"), an empty list is a deliberate state: every group deleted.
        var current = AppConfig.Empty with { SensorGroups = new[] { "CPU" } };

        AppConfig merged = ConfigMapper.Merge(current, IpcConfig.Empty with { SensorGroups = Array.Empty<string>() });

        Assert.Empty(merged.SensorGroups);
    }

    [Fact]
    public void Replace_TakesTheIncomingSensorGroups_AndNullClearsThem()
    {
        var current = AppConfig.Empty with { SensorGroups = new[] { "Alt" } };

        Assert.Equal(new[] { "Neu" },
            ConfigMapper.Replace(current, IpcConfig.Empty with { SensorGroups = new[] { "Neu" } }).SensorGroups);
        // A backup from before the field restores to no groups, like its missing sensors.
        Assert.Empty(ConfigMapper.Replace(current, IpcConfig.Empty with { SensorGroups = null }).SensorGroups);
    }

    // --- Case type --------------------------------------------------------------------------------

    [Fact]
    public void ToIpc_Then_Merge_CarriesTheCaseType_AndAFanWall()
    {
        var desired = AppConfig.Empty with
        {
            CaseType = CaseType.Server,
            Fans = new[] { new FanConfig { FanId = "f1", Location = FanLocation.FanWall } },
        };

        IpcConfig ipc = ConfigMapper.ToIpc(desired);
        AppConfig merged = ConfigMapper.Merge(AppConfig.Empty, ipc);

        Assert.Equal("Server", ipc.CaseType);
        Assert.Equal("FanWall", Assert.Single(ipc.Fans).Location);
        Assert.Equal(CaseType.Server, merged.CaseType);
        Assert.Equal(FanLocation.FanWall, Assert.Single(merged.Fans).Location);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Submarine")]
    public void Merge_ACaseTypeItCannotRead_KeepsTheDaemonsType(string? sent)
    {
        // null: a GUI that predates the field; an unknown name: a newer one - neither may reset the type.
        var current = AppConfig.Empty with { CaseType = CaseType.Open };

        AppConfig merged = ConfigMapper.Merge(current, IpcConfig.Empty with { CaseType = sent });

        Assert.Equal(CaseType.Open, merged.CaseType);
    }

    [Fact]
    public void Replace_TakesTheIncomingCaseType_AndNullIsATower_AsIsAReset()
    {
        var current = AppConfig.Empty with { CaseType = CaseType.Open };

        Assert.Equal(CaseType.Server, ConfigMapper.Replace(current, IpcConfig.Empty with { CaseType = "Server" }).CaseType);
        // A backup from before the field restores the standard case.
        Assert.Equal(CaseType.Tower, ConfigMapper.Replace(current, IpcConfig.Empty with { CaseType = null }).CaseType);
        Assert.Equal(CaseType.Tower, ConfigMapper.Reset(current).CaseType);
    }

    // --- Replace: Voll-Ersetzen (Import/Restore) ------------------------------------------------

    [Fact]
    public void Replace_DropsUnknownFansFromCurrent()
    {
        // Anders als Merge: im current vorhandene, aber nicht mitgelieferte Lüfter entfallen.
        var current = new AppConfig
        {
            Fans = new[]
            {
                new FanConfig { FanId = "gone", Name = "Weg", Calibration = Cal(80, 300, 1500) },
                new FanConfig { FanId = "f1", Name = "Alt" },
            },
        };
        var incoming = IpcWith(new IpcFanAssignment("f1", "Neu", 0, 255, null));

        AppConfig replaced = ConfigMapper.Replace(current, incoming);

        FanConfig fan = Assert.Single(replaced.Fans);
        Assert.Equal("f1", fan.FanId);
        Assert.Equal("Neu", fan.Name);
    }

    [Fact]
    public void Replace_TakesIncomingCalibration()
    {
        // Anders als Merge: die eingehende Kalibrierung wird übernommen (Restore aus Backup).
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Name = "Alt", Calibration = Cal(200, 100, 500) } },
        };
        var incoming = IpcWith(new IpcFanAssignment("f1", "Neu", 40, 220, "c1",
            Calibration: new IpcFanCalibration(96, 400, 1800)));

        AppConfig replaced = ConfigMapper.Replace(current, incoming);

        FanConfig fan = Assert.Single(replaced.Fans);
        Assert.NotNull(fan.Calibration);
        Assert.Equal((byte)96, fan.Calibration!.StartPwm); // eingehende, nicht die alte Kalibrierung
        Assert.Equal(400, fan.Calibration.MinRpm);
        Assert.Equal(1800, fan.Calibration.MaxRpm);
    }

    [Fact]
    public void ToIpc_Then_Replace_KeepsTheCalibrationsSensorAndSeries()
    {
        // An export and its import: the plausibility check compares with exactly this.
        var cal = new FanCalibration
        {
            StartPwm = 64,
            MinRpm = 800,
            MaxRpm = 1800,
            Tachometer = "nct/fan1",
            Samples = [new(0, 1500), new(32, 0), new(64, 800), new(224, 1800)],
        };
        var config = new AppConfig { Fans = [new FanConfig { FanId = "nct/pwm1", MinPwm = 64, Calibration = cal }] };

        IpcFanCalibration sent = ConfigMapper.ToIpc(config).Fans.Single().Calibration!;
        FanCalibration restored = ConfigMapper.Replace(AppConfig.Empty, ConfigMapper.ToIpc(config)).Fans.Single().Calibration!;

        Assert.Equal("nct/fan1", sent.Tachometer);
        Assert.Equal([new(0, 1500), new(32, 0), new(64, 800), new(224, 1800)], sent.Samples!);
        Assert.Equal(cal with { Samples = restored.Samples }, restored);
        Assert.Equal(cal.Samples, restored.Samples);
    }

    [Fact]
    public void Replace_FromAPeerWithoutTheSensorAndSeries_StoresNoneOfThem()
    {
        AppConfig replaced = ConfigMapper.Replace(AppConfig.Empty,
            IpcWith(new IpcFanAssignment("f1", "F", 96, Calibration: new IpcFanCalibration(96, 400, 1800))));

        FanCalibration cal = replaced.Fans.Single().Calibration!;
        Assert.Null(cal.Tachometer);
        Assert.Empty(cal.Samples);
    }

    [Fact]
    public void Replace_DropsSamplesWhosePwmIsNoRawValue_AndTrimsTheSensor()
    {
        AppConfig replaced = ConfigMapper.Replace(AppConfig.Empty, IpcWith(new IpcFanAssignment("f1", "F", 96,
            Calibration: new IpcFanCalibration(96, 400, 1800, " nct/fan1 ", [new(-1, 0), new(0, 0), new(96, 400), new(256, 1800)]))));
        AppConfig blank = ConfigMapper.Replace(AppConfig.Empty, IpcWith(new IpcFanAssignment("f1", "F", 96,
            Calibration: new IpcFanCalibration(96, 400, 1800, "  "))));

        FanCalibration cal = replaced.Fans.Single().Calibration!;
        Assert.Equal([new(0, 0), new(96, 400)], cal.Samples); // not clamped onto 0 and 255, where they were not measured
        Assert.Equal("nct/fan1", cal.Tachometer);
        Assert.Null(blank.Fans.Single().Calibration!.Tachometer);
    }

    [Fact]
    public void Replace_SkipsANullSample_InsteadOfDroppingTheImport()
    {
        AppConfig replaced = ConfigMapper.Replace(AppConfig.Empty, IpcWith(new IpcFanAssignment("f1", "F", 96,
            Calibration: new IpcFanCalibration(96, 400, 1800, "nct/fan1", [null!, new(96, 400)]))));

        Assert.Equal([new(96, 400)], replaced.Fans.Single().Calibration!.Samples);
    }

    [Fact]
    public void Replace_KeepsTheRecordedDevices_OfTheIdsItAlreadyHas()
    {
        // A backup carries no device, and an import settles no replacement: what the daemon recorded stays.
        var gpu = new HardwareDevice("amdgpu", "pci:1002:73bf:1849:5201");
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Device = gpu }, new FanConfig { FanId = "gone", Device = gpu } },
            Sensors = new[] { new SensorConfig { SensorId = "s1", Device = gpu } },
        };
        var incoming = IpcWith(new IpcFanAssignment("f1", "GPU", 0), new IpcFanAssignment("f2", "Neu", 0)) with
        {
            Sensors = new[] { new IpcSensorName("s1", "Hotspot"), new IpcSensorName("s2", "Neu") },
        };

        AppConfig replaced = ConfigMapper.Replace(current, incoming);

        Assert.Equal(new HardwareDevice?[] { gpu, null }, replaced.Fans.Select(f => f.Device));
        Assert.Equal(new HardwareDevice?[] { gpu, null }, replaced.Sensors.Select(s => s.Device));
    }

    [Fact]
    public void Replace_EmptyIncomingSensors_ClearsSensors()
    {
        // Anders als Merge (das leere Sensoren als „nichts geschickt" behandelt): Replace leert wirklich.
        var current = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "s1", Name = "CPU" } },
        };

        AppConfig replaced = ConfigMapper.Replace(current, IpcConfig.Empty);

        Assert.Empty(replaced.Sensors);
    }

    [Fact]
    public void Replace_KeepsDaemonOnlyFields()
    {
        // FailSafeTempC/PollIntervalMs/SchemaVersion stehen nicht im IPC-Vertrag → aus current behalten.
        var current = AppConfig.Empty with { FailSafeTempC = 77.0, PollIntervalMs = 2500, SchemaVersion = 3 };

        AppConfig replaced = ConfigMapper.Replace(current, IpcConfig.Empty);

        Assert.Equal(77.0, replaced.FailSafeTempC);
        Assert.Equal(2500, replaced.PollIntervalMs);
        Assert.Equal(3, replaced.SchemaVersion);
    }

    [Fact]
    public void Reset_ClearsWhatTheGuiEdits_ButKeepsDaemonOnlyFields()
    {
        // An operator's stricter over-temperature limit must survive a reset that any client can send.
        var current = AppConfig.Empty with
        {
            FailSafeTempC = 72.0,
            PollIntervalMs = 2500,
            SchemaVersion = 3,
            Fans = new[] { new FanConfig { FanId = "f1", Calibration = Cal(96, 400, 1800) } },
            Curves = new[] { new CurveConfig { Id = "c1" } },
            Sensors = new[] { new SensorConfig { SensorId = "s1" } },
            SensorGroups = new[] { "CPU" },
            Profiles = new[] { new Profile { Id = "p1" } },
            ActiveProfileId = "p1",
            OnboardingCompleted = false,
        };

        AppConfig reset = ConfigMapper.Reset(current);

        Assert.Equal(72.0, reset.FailSafeTempC);
        Assert.Equal(2500, reset.PollIntervalMs);
        Assert.Equal(3, reset.SchemaVersion);
        Assert.Empty(reset.Fans);
        Assert.Empty(reset.Curves);
        Assert.Empty(reset.Sensors);
        Assert.Empty(reset.SensorGroups);
        Assert.Empty(reset.Profiles);
        Assert.Null(reset.ActiveProfileId);
        Assert.True(reset.OnboardingCompleted);
    }

    [Fact]
    public void Replace_TakesCurvesProfilesSensorsAndActiveProfile()
    {
        var current = AppConfig.Empty with { ActiveProfileId = "old" };
        var incoming = IpcConfig.Empty with
        {
            Curves = new[] { new IpcCurve("c1", "Silent", "s1", 3.0, new[] { new IpcCurvePoint(40, 20) }) },
            Sensors = new[] { new IpcSensorName("s1", "CPU", "Zone") },
            Profiles = new[]
            {
                new IpcProfile("p1", "Perf", new[] { new IpcProfileAssignment("f1", "c1") }, Array.Empty<IpcCurve>()),
            },
            ActiveProfileId = "p1",
        };

        AppConfig replaced = ConfigMapper.Replace(current, incoming);

        Assert.Equal("Silent", Assert.Single(replaced.Curves).Name);
        Assert.Equal("CPU", Assert.Single(replaced.Sensors).Name);
        Assert.Equal("p1", Assert.Single(replaced.Profiles).Id);
        Assert.Equal("p1", replaced.ActiveProfileId);
    }

    // --- ApplyCalibration (Fail-Safe-Pfad) ------------------------------------------------------

    [Fact]
    public void ApplyCalibration_MinRpmPositive_SetsMinPwmToStartPwm()
    {
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Name = "F", MinPwm = 10 } },
        };
        var cal = Cal(96, 400, 1800);

        AppConfig result = ConfigMapper.ApplyCalibration(current, "f1", cal, device: null);

        FanConfig fan = Assert.Single(result.Fans);
        Assert.Equal((byte)96, fan.MinPwm);            // Anlaufpunkt übernommen
        Assert.Same(cal, fan.Calibration);
    }

    [Fact]
    public void ApplyCalibration_MinRpmZero_KeepsMinPwm_OnlySetsCalibration()
    {
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Name = "F", MinPwm = 10 } },
        };
        var cal = Cal(255, 0, 0);                      // nicht angelaufen → StartPwm=255

        AppConfig result = ConfigMapper.ApplyCalibration(current, "f1", cal, device: null);

        FanConfig fan = Assert.Single(result.Fans);
        Assert.Equal((byte)10, fan.MinPwm);            // unverändert (Fail-Safe: kein Zwang auf Volllast)
        Assert.Same(cal, fan.Calibration);
    }

    [Fact]
    public void ApplyCalibration_UnknownFan_IsAdded()
    {
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "other", Name = "Andere" } },
        };
        var cal = Cal(64, 300, 1500);

        AppConfig result = ConfigMapper.ApplyCalibration(current, "new", cal, device: null);

        Assert.Equal(2, result.Fans.Count);
        FanConfig added = result.Fans.Single(f => f.FanId == "new");
        Assert.Equal("", added.Name);                 // no pseudo name ⇒ the hardware label applies
        Assert.Equal((byte)64, added.MinPwm);
        Assert.Same(cal, added.Calibration);
        Assert.NotNull(result.Fans.Single(f => f.FanId == "other"));
    }

    [Fact]
    public void ApplyCalibration_RecordsTheDeviceItMeasured_AlsoOverAnotherOne()
    {
        // The result belongs to the device behind the id now - the fan leaves a pending replacement with it.
        var old = new HardwareDevice("amdgpu", "pci:1002:73bf:1849:5201");
        var measured = new HardwareDevice("amdgpu", "pci:1002:744c:1da2:e471");
        var current = new AppConfig { Fans = new[] { new FanConfig { FanId = "f1", Device = old } } };

        AppConfig result = ConfigMapper.ApplyCalibration(current, "f1", Cal(96, 400, 1800), measured);
        AppConfig added = ConfigMapper.ApplyCalibration(AppConfig.Empty, "f1", Cal(96, 400, 1800), measured);

        Assert.Equal(measured, Assert.Single(result.Fans).Device);
        Assert.Equal(measured, Assert.Single(added.Fans).Device);
    }

    [Fact]
    public void ApplyCalibration_WithoutAStartPoint_KeepsTheRecordedDevice()
    {
        // MinPwm stays the old device's, so the replacement must stay reported.
        var old = new HardwareDevice("amdgpu", "pci:1002:73bf:1849:5201");
        var measured = new HardwareDevice("amdgpu", "pci:1002:744c:1da2:e471");
        var current = new AppConfig { Fans = new[] { new FanConfig { FanId = "f1", MinPwm = 70, Device = old } } };

        AppConfig result = ConfigMapper.ApplyCalibration(current, "f1", Cal(255, 0, 0), measured);

        FanConfig fan = Assert.Single(result.Fans);
        Assert.Equal(((byte)70, old), (fan.MinPwm, fan.Device));
        Assert.NotNull(fan.Calibration);
    }

    [Fact]
    public void ApplyCalibration_WithoutADevice_KeepsTheRecordedOne()
    {
        var old = new HardwareDevice("thinkpad", null);
        var current = new AppConfig { Fans = new[] { new FanConfig { FanId = "f1", Device = old } } };

        AppConfig result = ConfigMapper.ApplyCalibration(current, "f1", Cal(96, 400, 1800), device: null);

        Assert.Equal(old, Assert.Single(result.Fans).Device);
    }

    // --- RpmSource-Override (Tacho-Zuordnung, daemon-verwaltet) ----------------------------------

    [Fact]
    public void Merge_PreservesRpmSource_OnFanUpdate()
    {
        // RpmSource ist daemon-verwaltet (wie Kalibrierung): ein GUI-Save darf es nicht wegräumen.
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Name = "Alt", RpmSource = "io/fan/3" } },
        };
        var incoming = IpcWith(new IpcFanAssignment("f1", "Neu", 10, 250, "curve-1"));

        AppConfig merged = ConfigMapper.Merge(current, incoming);

        FanConfig fan = Assert.Single(merged.Fans);
        Assert.Equal("Neu", fan.Name);
        Assert.Equal("io/fan/3", fan.RpmSource);        // Zuordnung unangetastet
    }

    [Fact]
    public void ToIpc_MirrorsRpmSource()
    {
        var config = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Name = "F", RpmSource = "io/fan/2" } },
        };

        IpcConfig ipc = ConfigMapper.ToIpc(config);

        Assert.Equal("io/fan/2", Assert.Single(ipc.Fans).RpmSource);
    }

    [Fact]
    public void Replace_TakesIncomingRpmSource()
    {
        // Restore aus Backup trägt die Zuordnung originalgetreu zurück (wie die Kalibrierung).
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Name = "Alt", RpmSource = "old/fan" } },
        };
        var incoming = IpcWith(new IpcFanAssignment("f1", "Neu", 40, 220, "c1", RpmSource: "io/fan/5"));

        AppConfig replaced = ConfigMapper.Replace(current, incoming);

        Assert.Equal("io/fan/5", Assert.Single(replaced.Fans).RpmSource);
    }

    [Fact]
    public void ApplyTachometer_SetsRpmSource()
    {
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Name = "F" } },
        };

        AppConfig result = ConfigMapper.ApplyTachometer(current, "f1", "io/fan/1", out _);

        Assert.Equal("io/fan/1", Assert.Single(result.Fans).RpmSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ApplyTachometer_NullOrBlank_ClearsRpmSource(string? blank)
    {
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "f1", Name = "F", RpmSource = "io/fan/1" } },
        };

        AppConfig result = ConfigMapper.ApplyTachometer(current, "f1", blank, out string? previous);
        Assert.Null(previous);

        Assert.Null(Assert.Single(result.Fans).RpmSource);
    }

    [Fact]
    public void ApplyTachometer_UnknownFan_IsAdded()
    {
        var current = new AppConfig
        {
            Fans = new[] { new FanConfig { FanId = "other", Name = "Andere" } },
        };

        AppConfig result = ConfigMapper.ApplyTachometer(current, "new", "io/fan/9", out _);

        FanConfig added = result.Fans.Single(f => f.FanId == "new");
        // No name: Name is the user's OWN name, empty ⇒ the hardware label applies. Putting the FanId here
        // would leave the raw path stuck as the display name.
        Assert.Equal("", added.Name);
        Assert.Equal("io/fan/9", added.RpmSource);
        Assert.Equal(2, result.Fans.Count);
    }

    [Fact]
    public void ApplyTachometer_TakesTheSensorFromTheFanThatHadIt()
    {
        var current = new AppConfig
        {
            Fans = new[]
            {
                new FanConfig { FanId = "f1", RpmSource = "io/fan/1" },
                new FanConfig { FanId = "f2", RpmSource = "io/fan/2" },
            },
        };

        AppConfig result = ConfigMapper.ApplyTachometer(current, "f2", "io/fan/1", out string? previous);

        Assert.Equal("f1", previous);
        Assert.Null(result.Fans.Single(f => f.FanId == "f1").RpmSource);       // one sensor, one fan
        Assert.Equal("io/fan/1", result.Fans.Single(f => f.FanId == "f2").RpmSource);
    }
}
