// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class ConfigLimitsTests
{
    private static CurveConfig Curve(string id, int points) => new()
    {
        Id = id,
        Name = id,
        SourceSensorIds = new[] { "k10temp/temp1" },
        Points = Enumerable.Range(0, points).Select(i => new CurvePoint(i * 0.5, 50)).ToArray(),
    };

    [Fact]
    public void Check_ARealisticConfig_IsWithinLimits()
    {
        var config = new AppConfig
        {
            Fans = Enumerable.Range(0, 8).Select(i => new FanConfig { FanId = $"nct6797/pwm{i}", Name = "Gehäuse" }).ToArray(),
            Sensors = Enumerable.Range(0, 60).Select(i => new SensorConfig { SensorId = $"coretemp/temp{i}" }).ToArray(),
            Curves = new[] { Curve("cpu", 6), Curve("gpu", 6) },
            Profiles = Enumerable.Range(0, 4).Select(i => new Profile
            {
                Id = $"p{i}",
                Name = $"P{i}",
                Curves = new[] { Curve("cpu", 6), Curve("gpu", 6) },
            }).ToArray(),
        };

        Assert.Null(ConfigLimits.Check(config));
    }

    [Fact]
    public void Check_TooManyFans_IsAViolation()
    {
        var config = new AppConfig
        {
            Fans = Enumerable.Range(0, ConfigLimits.MaxFans + 1).Select(i => new FanConfig { FanId = $"u{i}" }).ToArray(),
        };

        Assert.NotNull(ConfigLimits.Check(config));
    }

    [Fact]
    public void Check_ACurveWithTooManyPoints_IsAViolation()
    {
        var config = new AppConfig { Curves = new[] { Curve("c", ConfigLimits.MaxPointsPerCurve + 1) } };

        Assert.NotNull(ConfigLimits.Check(config));
    }

    [Fact]
    public void Check_OverlongText_IsAViolation()
    {
        // Counts alone are not enough: one curve with a huge name would be copied into every empty profile.
        var config = new AppConfig
        {
            Curves = new[] { Curve("c", 2) with { Name = new string('x', ConfigLimits.MaxTextLength + 1) } },
        };

        Assert.NotNull(ConfigLimits.Check(config));
    }

    /// <summary>
    /// A recorded device never costs a config its limits: whatever text it is built from, <see cref="HardwareDevice"/>
    /// keeps it within them - a backend's, a calibration's and a hand-edited file's alike.
    /// </summary>
    [Fact]
    public void Check_ARecordedDevice_IsWithinTheLimits_HoweverLongItsText()
    {
        string tooLong = new('x', ConfigLimits.MaxTextLength + 1);
        var device = new HardwareDevice(tooLong, tooLong);

        Assert.Null(ConfigLimits.Check(new AppConfig
        {
            Fans = [new FanConfig { FanId = "f", Device = device }],
            Sensors = [new SensorConfig { SensorId = "s", Device = device }],
        }));
        Assert.True(device.Name.Length <= ConfigLimits.MaxTextLength);
        Assert.True(device.Fingerprint!.Length <= ConfigLimits.MaxTextLength);
    }

    [Fact]
    public void Check_TooLongACalibrationSeries_IsAViolation()
    {
        static AppConfig With(int samples) => new()
        {
            Fans =
            [
                new FanConfig
                {
                    FanId = "f",
                    Calibration = new FanCalibration { Samples = Enumerable.Range(0, samples).Select(i => new CalibrationSample((byte)i, 900)).ToList() },
                },
            ],
        };

        Assert.Null(ConfigLimits.Check(With(ConfigLimits.MaxCalibrationSamples)));
        Assert.NotNull(ConfigLimits.Check(With(ConfigLimits.MaxCalibrationSamples + 1)));
    }

    [Fact]
    public void Check_AnOverlongCalibrationSensor_IsAViolation()
    {
        static AppConfig With(int length) => new()
        {
            Fans = [new FanConfig { FanId = "f", Calibration = new FanCalibration { Tachometer = new string('x', length) } }],
        };

        Assert.Null(ConfigLimits.Check(With(ConfigLimits.MaxTextLength)));
        Assert.NotNull(ConfigLimits.Check(With(ConfigLimits.MaxTextLength + 1)));
    }

    [Fact]
    public void Check_AMissingCalibrationSeries_IsWithinLimits()
    {
        // A hand-edited "samples": null reaches the check before the sanitizer reads it as empty.
        var config = new AppConfig { Fans = [new FanConfig { FanId = "f", Calibration = new FanCalibration { Samples = null! } }] };

        Assert.Null(ConfigLimits.Check(config));
    }

    [Fact]
    public void Check_TooManySensorGroups_IsAViolation()
    {
        var atLimit = new AppConfig { SensorGroups = Enumerable.Range(0, ConfigLimits.MaxSensorGroups).Select(i => $"G{i}").ToArray() };
        var overLimit = atLimit with { SensorGroups = [.. atLimit.SensorGroups, "one more"] };

        Assert.Null(ConfigLimits.Check(atLimit));
        Assert.NotNull(ConfigLimits.Check(overLimit));
    }

    [Fact]
    public void Check_AnOverlongSensorGroupName_IsAViolation()
    {
        var config = new AppConfig { SensorGroups = new[] { "CPU", new string('x', ConfigLimits.MaxTextLength + 1) } };

        Assert.NotNull(ConfigLimits.Check(config));
    }

    [Fact]
    public void Check_EmptyProfiles_CountWithTheCurvesTheyWillBeSeededWith()
    {
        // Each set is within its own bounds, but EnsureProfiles copies the top-level curves into every empty
        // profile: 33 sets of 2 x 128 points are 8448 points in total (audit run-2, #10).
        var config = new AppConfig
        {
            Curves = new[] { Curve("a", 128), Curve("b", 128) },
            Profiles = Enumerable.Range(0, ConfigLimits.MaxProfiles).Select(i => new Profile { Id = $"q{i}" }).ToArray(),
        };

        Assert.Contains("insgesamt", ConfigLimits.Check(config));
    }
}
