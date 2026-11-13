// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class ConfigSanitizerTests
{
    [Theory]
    [InlineData(200.0)]  // viel zu hoch → Watchdog faktisch aus
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void Sanitize_OutOfRangeFailSafe_FallsBackToDefault_WithWarning(double bad)
    {
        var config = new AppConfig { FailSafeTempC = bad };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(ConfigSanitizer.DefaultFailSafeC, clean.FailSafeTempC);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_PlausibleValues_PassThroughUnchanged()
    {
        var config = new AppConfig { FailSafeTempC = 85, PollIntervalMs = 1000 };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Same(config, clean); // unverändert → exakt dieselbe Instanz
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_TooFastPoll_IsClamped()
    {
        var config = new AppConfig { FailSafeTempC = 85, PollIntervalMs = 10 };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(ConfigSanitizer.MinPollIntervalMs, clean.PollIntervalMs);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_TooSlowPoll_IsClamped()
    {
        // The watchdog checks once per tick: an hour-long interval would leave over-temperature unchecked.
        var config = new AppConfig { FailSafeTempC = 85, PollIntervalMs = 3_600_000 };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(ConfigSanitizer.MaxPollIntervalMs, clean.PollIntervalMs);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_FanMaxBelowMin_RaisesMaxToMin()
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Fans = new[] { new FanConfig { FanId = "f", MinPwm = 200, MaxPwm = 50 } },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        FanConfig f = Assert.Single(clean.Fans);
        Assert.Equal((byte)200, f.MaxPwm); // MaxPwm auf MinPwm angehoben
        Assert.NotEmpty(warnings);
    }

    [Theory]
    [InlineData(0, 80)]   // a cap of 0 holds the fan still despite its curve
    [InlineData(79, 80)]  // just below the start point: every curve value lies where the fan does not start
    [InlineData(80, 80)]  // at the start point - kept
    [InlineData(200, 200)]
    public void Sanitize_FanMaxBelowTheCalibratedStartPoint_IsRaisedToIt(int maxPwm, int expected)
    {
        var config = new AppConfig
        {
            Fans = new[]
            {
                new FanConfig { FanId = "f", MaxPwm = (byte)maxPwm, Calibration = new FanCalibration { StartPwm = 80, MinRpm = 400 } },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal((byte)expected, Assert.Single(clean.Fans).MaxPwm);
        Assert.Equal(maxPwm < expected, warnings.Any(x => x.Contains("Anlaufpunkt", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null)] // not calibrated
    [InlineData(255)]  // calibrated, but the fan never started - no start point to raise to
    [InlineData(0)]    // „spinning" at 0: most likely still coasting when the ramp began - no start point either
    public void Sanitize_FanMaxZeroWithoutAStartPoint_ReadsAsNoCap(int? startPwm)
    {
        var config = new AppConfig
        {
            Fans = new[]
            {
                new FanConfig
                {
                    FanId = "f",
                    MaxPwm = 0,
                    Calibration = startPwm is { } s ? new FanCalibration { StartPwm = (byte)s } : null,
                },
                new FanConfig { FanId = "g", MaxPwm = 1 }, // a cap above 0 is the user's, start point or not
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal((byte)255, clean.Fans.Single(f => f.FanId == "f").MaxPwm);
        Assert.Equal((byte)1, clean.Fans.Single(f => f.FanId == "g").MaxPwm);
        Assert.Single(warnings);
    }

    [Fact]
    public void Sanitize_AnUndefinedFanLocation_BecomesUnspecified()
    {
        // A hand-edited file with "location": 999 - IPC only takes defined names, the file path came through unchecked.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Fans = new[]
            {
                new FanConfig { FanId = "f", Location = (FanLocation)999 },
                new FanConfig { FanId = "g", Location = FanLocation.CpuCooler },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(FanLocation.Unspecified, clean.Fans[0].Location);
        Assert.Equal(FanLocation.CpuCooler, clean.Fans[1].Location);
        Assert.Single(warnings);
    }

    [Fact]
    public void Sanitize_AnUndefinedCaseType_BecomesTower()
    {
        var config = new AppConfig { FailSafeTempC = 85, CaseType = (CaseType)7 };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(CaseType.Tower, clean.CaseType);
        Assert.Single(warnings);
    }

    [Fact]
    public void Sanitize_KeepsADefinedCaseType()
    {
        var config = new AppConfig { FailSafeTempC = 85, CaseType = CaseType.Server };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Same(config, clean);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_MigratesLegacySourceSensorId_ToSourceSensorIds()
    {
        // Schema-1-Altbestand: nur das alte Einzelfeld gesetzt, SourceSensorIds leer.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[]
            {
                new CurveConfig { Id = "c", Name = "c", SourceSensorId = "hwmon6/temp1" },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        CurveConfig migrated = Assert.Single(clean.Curves);
        Assert.Equal(new[] { "hwmon6/temp1" }, migrated.SourceSensorIds); // ohne Migration ginge die Quelle verloren
        Assert.Null(migrated.SourceSensorId);                              // altes Feld geleert
        Assert.Empty(warnings);                                           // stille Normalisierung, keine Warnung
    }

    [Fact]
    public void Sanitize_DoesNotOverwriteExistingSourceSensorIds()
    {
        // Schema 2: SourceSensorIds bereits gesetzt → das alte Einzelfeld wird ignoriert (kein Overwrite).
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c",
                    SourceSensorId = "legacy", SourceSensorIds = new[] { "a", "b" },
                },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out _);

        Assert.Equal(new[] { "a", "b" }, Assert.Single(clean.Curves).SourceSensorIds);
    }

    [Fact]
    public void Sanitize_EmptySources_NoMigration_PassThrough()
    {
        // Weder altes noch neues Feld gesetzt → keine Migration, identische Instanz zurück.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[] { new CurveConfig { Id = "c", Name = "c" } },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Same(config, clean);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_DuplicateFanIds_KeepsFirst_WithWarning()
    {
        // Kann durch eine Hand-Edit-Datei oder eine IPC-Gegenstelle entstehen -
        // ohne Bereinigung würde der Snapshot-Bau/die GUI am ToDictionary werfen und abstürzen.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Fans = new[]
            {
                new FanConfig { FanId = "thinkpad/pwm1", Name = "erster" },
                new FanConfig { FanId = "thinkpad/pwm1", Name = "zweiter" },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        FanConfig f = Assert.Single(clean.Fans);
        Assert.Equal("erster", f.Name); // erster gewinnt
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_DuplicateSensorIds_KeepsFirst_WithWarning()
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Sensors = new[]
            {
                new SensorConfig { SensorId = "k10temp/temp1", Name = "erster" },
                new SensorConfig { SensorId = "k10temp/temp1", Name = "zweiter" },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        SensorConfig s = Assert.Single(clean.Sensors);
        Assert.Equal("erster", s.Name);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_NoDuplicates_PassesListsThroughUnchanged()
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Fans = new[] { new FanConfig { FanId = "a" }, new FanConfig { FanId = "b" } },
            Sensors = new[] { new SensorConfig { SensorId = "x" }, new SensorConfig { SensorId = "y" } },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Same(config, clean); // dup-frei → dieselbe Instanz, keine Allokation
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_EntriesWithoutId_AreDropped()
    {
        // JSON null still arrives in ids declared non-null (IPC client, hand-edited file); downstream they are
        // dictionary keys, and a null key there used to stop the daemon from starting.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Sensors = new[] { new SensorConfig { SensorId = null! }, new SensorConfig { SensorId = "s" } },
            Fans = new[] { new FanConfig { FanId = " " }, new FanConfig { FanId = "f" } },
            Curves = new[] { new CurveConfig { Id = "c", Name = "c", SourceSensorIds = new[] { "s", null!, "" } } },
            Profiles = new[]
            {
                new Profile { Id = null!, Name = "ohne Id" },
                new Profile
                {
                    Id = "p", Name = "P",
                    Assignments = new[] { new ProfileAssignment(null!, "c"), new ProfileAssignment("f", "c") },
                },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal("s", Assert.Single(clean.Sensors).SensorId);
        Assert.Equal("f", Assert.Single(clean.Fans).FanId);
        Assert.Equal(new[] { "s" }, Assert.Single(clean.Curves).SourceSensorIds);
        Profile p = Assert.Single(clean.Profiles);
        Assert.Equal("f", Assert.Single(p.Assignments).FanId);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_CurvesWithoutIdOrDuplicated_AreDropped_AndSourcesDeduplicated()
    {
        // A null curve id reached every GUI, whose lookup by curve id throws on the UI thread.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[]
            {
                new CurveConfig { Id = null!, Name = "ohne Id" },
                new CurveConfig { Id = "c", Name = "erste", SourceSensorIds = new[] { "s", "s", "t" } },
                new CurveConfig { Id = "c", Name = "zweite" },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        CurveConfig c = Assert.Single(clean.Curves);
        Assert.Equal("erste", c.Name);
        Assert.Equal(new[] { "s", "t" }, c.SourceSensorIds);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_DuplicateProfilesAndAssignments_KeepFirst()
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p", Name = "erster",
                    Assignments = new[] { new ProfileAssignment("f", "a"), new ProfileAssignment("f", "b") },
                },
                new Profile { Id = "p", Name = "zweiter" },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Profile p = Assert.Single(clean.Profiles);
        Assert.Equal("erster", p.Name);
        Assert.Equal("a", Assert.Single(p.Assignments).CurveId);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_ProfileCurves_AreCheckedLikeTheTopLevelCurves()
    {
        // EnsureProfiles copies the active profile's curves into the live config after Sanitize ran.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p", Name = "P",
                    Curves = new[]
                    {
                        new CurveConfig
                        {
                            Id = "c", Name = "c", SmoothingSeconds = 1e30,
                            Points = new[] { new CurvePoint(40, 30), new CurvePoint(double.NaN, 50) },
                        },
                    },
                },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        CurveConfig c = Assert.Single(Assert.Single(clean.Profiles).Curves);
        Assert.Single(c.Points);
        Assert.Equal(ConfigSanitizer.MaxSmoothingSeconds, c.SmoothingSeconds);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_CleanProfiles_PassThroughUnchanged()
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p", Name = "P",
                    Curves = new[] { new CurveConfig { Id = "c", Name = "c", SourceSensorIds = new[] { "s" } } },
                    Assignments = new[] { new ProfileAssignment("f", "c") },
                },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Same(config, clean);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_DropsNonFiniteCurvePoints()
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c",
                    Points = new[]
                    {
                        new CurvePoint(30, 20),
                        new CurvePoint(double.NaN, 50),
                        new CurvePoint(80, double.PositiveInfinity),
                    },
                },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        CurvePoint p = Assert.Single(Assert.Single(clean.Curves).Points);
        Assert.Equal(30, p.TemperatureC);
        Assert.NotEmpty(warnings);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    public void Sanitize_InvalidSmoothing_FallsBackToDefault_WithWarning(double bad)
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[] { new CurveConfig { Id = "c", Name = "c", SmoothingSeconds = bad } },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(CurveConfig.DefaultSmoothingSeconds, Assert.Single(clean.Curves).SmoothingSeconds);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_OverlongSmoothing_IsClamped()
    {
        // Ein Fenster weit jenseits der Zeitkonstante des Kühlkörpers ließe die Kurve kaputt wirken.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[] { new CurveConfig { Id = "c", Name = "c", SmoothingSeconds = 600 } },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(ConfigSanitizer.MaxSmoothingSeconds, Assert.Single(clean.Curves).SmoothingSeconds);
        Assert.NotEmpty(warnings);
    }

    [Theory]
    [InlineData(double.NaN, CurveConfig.DefaultHysteresisC)]
    [InlineData(-1.0, CurveConfig.DefaultHysteresisC)]
    [InlineData(1e30, ConfigSanitizer.MaxHysteresisC)]
    public void Sanitize_HysteresisOutOfRange_IsCorrected(double bad, double expected)
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[] { new CurveConfig { Id = "c", Name = "c", HysteresisC = bad } },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(expected, Assert.Single(clean.Curves).HysteresisC);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_PointsOutOfRange_AreClampedToTheEditorRange()
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c",
                    Points = new[] { new CurvePoint(-5, -10), new CurvePoint(40, 150), new CurvePoint(1e30, 50) },
                },
            },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(
            new[] { new CurvePoint(0, 0), new CurvePoint(40, 100), new CurvePoint(ConfigSanitizer.MaxPointTempC, 50) },
            Assert.Single(clean.Curves).Points);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Sanitize_SmoothingOff_IsKept()
    {
        // 0 ist eine gültige Nutzer-Entscheidung (Glättung aus), keine zu korrigierende Eingabe.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Curves = new[] { new CurveConfig { Id = "c", Name = "c", SmoothingSeconds = 0 } },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Same(config, clean);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_SensorGroups_AreTrimmed_BlanksAndCaseInsensitiveRepeatsDropped()
    {
        // JSON null still arrives in a list declared non-null.
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            SensorGroups = new[] { " CPU ", "GPU", "", null!, "cpu", "  ", "Gpu", "Leer" },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(new[] { "CPU", "GPU", "Leer" }, clean.SensorGroups); // the first spelling wins, order kept
        Assert.Equal(2, warnings.Count); // one note on the blanks, one on the repeats
        Assert.DoesNotContain(warnings, x => x.Contains("cpu") || x.Contains("Gpu")); // client text stays out of the log
    }

    [Fact]
    public void Sanitize_SensorGroupsNull_ReadAsEmpty()
    {
        // A hand-edited "sensorGroups": null - the limits check must pass it too, or the file is set aside.
        var config = new AppConfig { FailSafeTempC = 85, SensorGroups = null! };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Null(ConfigLimits.Check(config));
        Assert.NotNull(clean.SensorGroups);
        Assert.Empty(clean.SensorGroups);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_SensorGroupsNoSensorIsIn_AreKept()
    {
        var config = new AppConfig
        {
            FailSafeTempC = 85,
            Sensors = new[] { new SensorConfig { SensorId = "k10temp/temp1", Group = "CPU" } },
            SensorGroups = new[] { "Leer", "CPU" },
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Same(config, clean); // clean list → same instance
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_OnlyTrimmedSensorGroups_AreANewConfig_WithoutWarning()
    {
        var config = new AppConfig { FailSafeTempC = 85, SensorGroups = new[] { "CPU " } };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(new[] { "CPU" }, clean.SensorGroups);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_ControlCharactersInLabels_AreStripped_AndTheWarningQuotesNoText()
    {
        const string evil = "Front\r\n2026-01-01 [ERR] forged\u001b]0;pwned\u0007";
        var curve = new CurveConfig { Id = "c1", Name = evil };
        var config = new AppConfig
        {
            Sensors = [new SensorConfig { SensorId = "k10temp/temp1", Name = evil, Group = "CPU\u202e" }],
            SensorGroups = ["CPU\u202e"],
            Fans = [new FanConfig { FanId = "thinkpad/pwm1", Name = evil, AssignedCurveId = "c1" }],
            Curves = [curve],
            Profiles = [new Profile { Id = "p1", Name = evil, Curves = [curve] }],
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        const string stripped = "Front2026-01-01 [ERR] forged]0;pwned";
        Assert.Equal(stripped, clean.Sensors[0].Name);
        Assert.Equal("CPU", clean.Sensors[0].Group);
        Assert.Equal(["CPU"], clean.SensorGroups);
        Assert.Equal(stripped, clean.Fans[0].Name);
        Assert.Equal("c1", clean.Fans[0].AssignedCurveId);
        Assert.Equal(stripped, clean.Curves[0].Name);
        Assert.Equal(stripped, clean.Profiles[0].Name);
        Assert.Equal(stripped, clean.Profiles[0].Curves[0].Name);
        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.True(UntrustedText.IsClean(w)));
        Assert.All(warnings, w => Assert.DoesNotContain("forged", w));
    }

    /// <summary>A recorded device is clean by construction (<see cref="HardwareDevice"/>): the sanitizer leaves it be.</summary>
    [Fact]
    public void Sanitize_ARecordedDevice_IsKept_AsTheSameConfig()
    {
        var config = new AppConfig
        {
            Fans = [new FanConfig { FanId = "nvme/pwm1", Device = new HardwareDevice("nvme", null) }],
        };

        Assert.Same(config, ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_EntriesWithControlCharactersInTheirId_AreDropped()
    {
        var config = new AppConfig
        {
            Sensors = [new SensorConfig { SensorId = "bad\nid" }, new SensorConfig { SensorId = "k10temp/temp1" }],
            Fans = [new FanConfig { FanId = "bad\u0000fan" }, new FanConfig { FanId = "thinkpad/pwm1" }],
            Curves = [new CurveConfig { Id = "c\u001b1" }, new CurveConfig { Id = "c2" }],
            Profiles =
            [
                new Profile { Id = "p\r1" },
                new Profile { Id = "p2", Assignments = [new ProfileAssignment("x\ny", null), new ProfileAssignment("thinkpad/pwm1", "c2")] },
            ],
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(["k10temp/temp1"], clean.Sensors.Select(s => s.SensorId));
        Assert.Equal(["thinkpad/pwm1"], clean.Fans.Select(f => f.FanId));
        Assert.Equal(["c2"], clean.Curves.Select(c => c.Id));
        Assert.Equal(["p2"], clean.Profiles.Select(p => p.Id));
        Assert.Equal(["thinkpad/pwm1"], clean.Profiles[0].Assignments.Select(a => a.FanId));
        Assert.All(warnings, w => Assert.True(UntrustedText.IsClean(w)));
    }

    [Fact]
    public void Sanitize_ReferencesWithControlCharacters_AreCleared()
    {
        var config = new AppConfig
        {
            ActiveProfileId = "p1\n",
            Fans = [new FanConfig { FanId = "thinkpad/pwm1", AssignedCurveId = "c1\r", RpmSource = "thinkpad/fan1\u0007" }],
            Curves = [new CurveConfig { Id = "c1", SourceSensorIds = ["k10temp/temp1", "evil\u001b[2J"] }],
            Profiles = [new Profile { Id = "p1", Assignments = [new ProfileAssignment("thinkpad/pwm1", "c1\0")] }],
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Null(clean.ActiveProfileId);
        Assert.Null(clean.Fans[0].AssignedCurveId);   // like no curve: the loop puts the fan on firmware auto
        Assert.Null(clean.Fans[0].RpmSource);
        Assert.Equal(["k10temp/temp1"], clean.Curves[0].SourceSensorIds);
        Assert.Null(clean.Profiles[0].Assignments[0].CurveId);
        Assert.Contains(warnings, w => w.Contains("Verweis", StringComparison.Ordinal));
    }

    [Fact]
    public void Sanitize_ACalibrationSensorWithControlCharacters_IsCleared_TheCalibrationStays()
    {
        var cal = new FanCalibration { StartPwm = 64, MinRpm = 800, MaxRpm = 1800, Tachometer = "thinkpad/fan1\u001b[2J", Samples = [new(64, 800)] };
        var config = new AppConfig { Fans = [new FanConfig { FanId = "thinkpad/pwm1", MinPwm = 64, Calibration = cal }] };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Null(clean.Fans[0].Calibration!.Tachometer);       // compared like a calibration that recorded none
        Assert.Equal((64, 800, 1800), (clean.Fans[0].Calibration!.StartPwm, clean.Fans[0].Calibration!.MinRpm, clean.Fans[0].Calibration!.MaxRpm));
        Assert.Same(cal.Samples, clean.Fans[0].Calibration!.Samples);
        Assert.Contains(warnings, w => w.Contains("Verweis", StringComparison.Ordinal));
    }

    [Fact]
    public void Sanitize_CalibrationSamplesWithANegativeSpeed_AreDropped()
    {
        var config = new AppConfig
        {
            Fans =
            [
                new FanConfig
                {
                    FanId = "nct/pwm1",
                    Calibration = new FanCalibration { Samples = [new(0, 0), new(64, -5), new(96, 1000), new(128, int.MinValue)] },
                },
            ],
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal([new(0, 0), new(96, 1000)], clean.Fans[0].Calibration!.Samples);
        Assert.Contains(warnings, w => w.Contains("nct/pwm1", StringComparison.Ordinal) && w.Contains('2'));
    }

    [Fact]
    public void Sanitize_AMissingCalibrationSeries_ReadsAsEmpty()
    {
        var config = new AppConfig
        {
            Fans = [new FanConfig { FanId = "nct/pwm1", Calibration = new FanCalibration { StartPwm = 64, Samples = null! } }],
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Empty(clean.Fans[0].Calibration!.Samples);
        Assert.Equal(64, clean.Fans[0].Calibration!.StartPwm);
        Assert.Contains(warnings, w => w.Contains("nct/pwm1", StringComparison.Ordinal));
    }

    [Fact]
    public void Sanitize_ACleanCalibration_StaysTheSameInstance()
    {
        var config = new AppConfig
        {
            Fans =
            [
                new FanConfig
                {
                    FanId = "nct/pwm1",
                    MinPwm = 64,
                    Calibration = new FanCalibration { StartPwm = 64, Tachometer = "nct/fan1", Samples = [new(0, 0), new(64, 800)] },
                },
            ],
        };

        Assert.Same(config, ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Sanitize_ASensorCoupledToTwoFans_StaysWithTheFirst()
    {
        var config = new AppConfig
        {
            Fans =
            [
                new FanConfig { FanId = "nct/pwm1", RpmSource = "nct/fan1" },
                new FanConfig { FanId = "nct/pwm2", RpmSource = "nct/fan1" },
                new FanConfig { FanId = "nct/pwm3", RpmSource = "nct/fan3" },
            ],
        };

        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);

        Assert.Equal(["nct/fan1", null, "nct/fan3"], clean.Fans.Select(f => f.RpmSource));
        Assert.Contains(warnings, w => w.Contains("nct/pwm2", StringComparison.Ordinal));
    }

    [Fact]
    public void Sanitize_DistinctCouplings_StayTheSameInstance()
    {
        var config = new AppConfig
        {
            Fans = [new FanConfig { FanId = "nct/pwm1", RpmSource = "nct/fan1" }, new FanConfig { FanId = "nct/pwm2" }],
        };

        Assert.Same(config, ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings));
        Assert.Empty(warnings);
    }
}
