// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public sealed class CurvePresetsTests
{
    public static TheoryData<string> Presets => new() { "Silent", "Balanced", "Performance", "Constant40" };

    private static IReadOnlyList<CurvePoint> Get(string name) => name switch
    {
        "Silent" => CurvePresets.Silent,
        "Balanced" => CurvePresets.Balanced,
        "Performance" => CurvePresets.Performance,
        "Constant40" => CurvePresets.Constant40,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Presets))]
    public void Points_RiseMonotonically_WithinTheEditorRange(string name)
    {
        // Linear between monotonic points, flat outside them: the curve itself can only rise.
        IReadOnlyList<CurvePoint> points = Get(name);

        Assert.True(points.Count >= 2);
        for (int i = 0; i < points.Count; i++)
        {
            Assert.InRange(points[i].TemperatureC, ConfigSanitizer.MinPointTempC, ConfigSanitizer.MaxPointTempC);
            Assert.InRange(points[i].Percent, 0, 100);
            if (i == 0)
                continue;
            Assert.True(points[i].TemperatureC > points[i - 1].TemperatureC, $"{name}: temperatures must ascend");
            Assert.True(points[i].Percent >= points[i - 1].Percent, $"{name}: percent must not fall");
        }
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void FullSpeed_IsReachedBy86C_BelowTheFailSafeDefault(string name)
    {
        CurvePoint full = Get(name).First(p => p.Percent >= 100);

        Assert.True(full.TemperatureC <= 86, $"{name} reaches 100 % only at {full.TemperatureC} °C");
        Assert.True(full.TemperatureC < AppConfig.DefaultFailSafeTempC);
    }

    [Fact]
    public void Constant40_HoldsForty_ThenRampsBetween80And86()
    {
        var curve = new Curve("constant", CurvePresets.Constant40);

        Assert.Equal(40, CurveEngine.Evaluate(curve, 20));
        Assert.Equal(40, CurveEngine.Evaluate(curve, 80));
        Assert.Equal(70, CurveEngine.Evaluate(curve, 83), 6);
        Assert.Equal(100, CurveEngine.Evaluate(curve, 86));
    }
}
