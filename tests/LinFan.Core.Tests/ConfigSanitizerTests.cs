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
        // Kann durch die hwmon-Id-Migration (Id-Kollaps) oder eine Hand-Edit-Datei entstehen -
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
}
