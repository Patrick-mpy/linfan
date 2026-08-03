// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.Core.Tests;

public class CurveSourceResolverTests
{
    // ── ResolveSources ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveSources_ModernNonEmpty_ReturnsModern()
    {
        var modern = new[] { "s1", "s2" };
        var result = CurveSourceResolver.ResolveSources("legacy", modern);
        Assert.Equal(modern, result);
    }

    [Fact]
    public void ResolveSources_ModernEmpty_LegacyPresent_ReturnsLegacySingleton()
    {
        var result = CurveSourceResolver.ResolveSources("legacy", []);
        Assert.Equal(["legacy"], result);
    }

    [Fact]
    public void ResolveSources_ModernNull_LegacyPresent_ReturnsLegacySingleton()
    {
        var result = CurveSourceResolver.ResolveSources("legacy", null);
        Assert.Equal(["legacy"], result);
    }

    [Fact]
    public void ResolveSources_BothEmpty_ReturnsEmpty()
    {
        var result = CurveSourceResolver.ResolveSources(null, null);
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveSources_ModernNullLegacyEmpty_ReturnsEmpty()
    {
        var result = CurveSourceResolver.ResolveSources("", null);
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveSources_ModernEmptyLegacyEmpty_ReturnsEmpty()
    {
        var result = CurveSourceResolver.ResolveSources("", []);
        Assert.Empty(result);
    }

    // ── ParseEnum ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseEnum_ValidName_Avg_ReturnsAvg()
    {
        var result = CurveSourceResolver.ParseEnum<SensorAggregation>("Avg", SensorAggregation.Max);
        Assert.Equal(SensorAggregation.Avg, result);
    }

    [Fact]
    public void ParseEnum_ValidNameCaseInsensitive_ReturnsValue()
    {
        var result = CurveSourceResolver.ParseEnum<SensorAggregation>("avg", SensorAggregation.Max);
        Assert.Equal(SensorAggregation.Avg, result);
    }

    [Fact]
    public void ParseEnum_InvalidName_ReturnsFallback()
    {
        var result = CurveSourceResolver.ParseEnum<SensorAggregation>("Unknown", SensorAggregation.Max);
        Assert.Equal(SensorAggregation.Max, result);
    }

    [Fact]
    public void ParseEnum_Null_ReturnsFallback()
    {
        var result = CurveSourceResolver.ParseEnum<SensorAggregation>(null, SensorAggregation.Max);
        Assert.Equal(SensorAggregation.Max, result);
    }

    [Fact]
    public void ParseEnum_EmptyString_ReturnsFallback()
    {
        var result = CurveSourceResolver.ParseEnum<SensorAggregation>("", SensorAggregation.Max);
        Assert.Equal(SensorAggregation.Max, result);
    }

    [Fact]
    public void ParseEnum_NumericString999_UndefinedValue_ReturnsFallback()
    {
        // Enum.TryParse akzeptiert "999" ohne IsDefined-Guard — der Guard muss greifen.
        var result = CurveSourceResolver.ParseEnum<SensorAggregation>("999", SensorAggregation.Max);
        Assert.Equal(SensorAggregation.Max, result);
    }

    [Fact]
    public void ParseEnum_NumericString1_DefinedValue_ReturnsAvg()
    {
        // "1" entspricht SensorAggregation.Avg = 1, ist definiert → gültig.
        var result = CurveSourceResolver.ParseEnum<SensorAggregation>("1", SensorAggregation.Max);
        Assert.Equal(SensorAggregation.Avg, result);
    }

    // ── ParseAggregation (Bequemlichkeits-Wrapper) ────────────────────────────

    [Fact]
    public void ParseAggregation_ValidName_ReturnsExpected()
    {
        Assert.Equal(SensorAggregation.Avg, CurveSourceResolver.ParseAggregation("Avg"));
    }

    [Fact]
    public void ParseAggregation_Null_ReturnsMax()
    {
        Assert.Equal(SensorAggregation.Max, CurveSourceResolver.ParseAggregation(null));
    }

    [Fact]
    public void ParseAggregation_InvalidString_ReturnsMax()
    {
        Assert.Equal(SensorAggregation.Max, CurveSourceResolver.ParseAggregation("garbage"));
    }
}
