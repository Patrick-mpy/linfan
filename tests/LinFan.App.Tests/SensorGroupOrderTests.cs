// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.App.Tests;

public sealed class SensorGroupOrderTests : IDisposable
{
    private static readonly LanguageChoice[] Languages = [LanguageChoice.German, LanguageChoice.English];

    // Some tests switch the language; the assembly is pinned to German (TestCulture).
    public void Dispose() => Localizer.Instance.SetLanguage(LanguageChoice.German);

    // ---------------------------------------------------------------------------
    // Merge
    // ---------------------------------------------------------------------------

    [Fact]
    public void Merge_KeepsConfiguredOrder_ThenUnlistedAlphabetically()
    {
        IReadOnlyList<string> names = SensorGroupOrder.Merge(
            configured: ["Zeta", "Alpha"],
            used: ["zubehör", "Alpha", "extern", "Beta"]);

        Assert.Equal(new[] { "Zeta", "Alpha", "Beta", "extern", "zubehör" }, names);
    }

    [Fact]
    public void Merge_DedupesCaseInsensitively_FirstSpellingWins()
    {
        IReadOnlyList<string> names = SensorGroupOrder.Merge(
            configured: ["CPU", "cpu", "Speicher"],
            used: ["speicher", "Extern", "EXTERN", "gpu", "GPU"]);

        Assert.Equal(new[] { "CPU", "Speicher", "Extern", "gpu" }, names);
    }

    [Fact]
    public void Merge_EmptyInputs_GiveEmptyList() =>
        Assert.Empty(SensorGroupOrder.Merge([], []));

    // ---------------------------------------------------------------------------
    // SuggestedName
    // ---------------------------------------------------------------------------

    public static TheoryData<SensorCategory, LanguageChoice> CategoriesTimesLanguages()
    {
        var data = new TheoryData<SensorCategory, LanguageChoice>();
        foreach (SensorCategory category in Enum.GetValues<SensorCategory>())
            foreach (LanguageChoice language in Languages)
                data.Add(category, language);
        return data;
    }

    /// <summary>
    /// The Localizer returns the key itself for a missing entry - a new <see cref="SensorCategory"/> value without
    /// resx entries would be saved as the group name "SensorCategory.Foo".
    /// </summary>
    [Theory]
    [MemberData(nameof(CategoriesTimesLanguages))]
    public void SuggestedName_ResolvesForEveryCategory_InEveryLanguage(SensorCategory category, LanguageChoice language)
    {
        Localizer.Instance.SetLanguage(language);

        string name = SensorGroupOrder.SuggestedName(category);

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.DoesNotContain("SensorCategory.", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LanguageChoice.German, "Speicher", "Sonstige")]
    [InlineData(LanguageChoice.English, "Storage", "Other")]
    public void SuggestedName_FollowsLanguage_ExceptCpuAndGpu(LanguageChoice language, string storage, string other)
    {
        Localizer.Instance.SetLanguage(language);

        Assert.Equal("CPU", SensorGroupOrder.SuggestedName(SensorCategory.Cpu));
        Assert.Equal("GPU", SensorGroupOrder.SuggestedName(SensorCategory.Gpu));
        Assert.Equal(storage, SensorGroupOrder.SuggestedName(SensorCategory.Storage));
        Assert.Equal("Mainboard", SensorGroupOrder.SuggestedName(SensorCategory.Mainboard));
        Assert.Equal(other, SensorGroupOrder.SuggestedName(SensorCategory.Other));
    }

    /// <summary>
    /// Parity with the airflow analysis, which finds CPU/GPU sources by keywords in a sensor's name or group: the
    /// suggested CPU and GPU group names must be such keywords in every language. Checked through the analysis
    /// itself (its keyword lists are internal to Core) with sensor names that match nothing on their own.
    /// </summary>
    [Theory]
    [InlineData(LanguageChoice.German)]
    [InlineData(LanguageChoice.English)]
    public void SuggestedCpuAndGpuNames_AreFoundByTheAirflowAnalysis(LanguageChoice language)
    {
        Localizer.Instance.SetLanguage(language);
        var config = new AppConfig
        {
            Sensors =
            [
                new SensorConfig { SensorId = "s1", Name = "Sensor A", Group = SensorGroupOrder.SuggestedName(SensorCategory.Cpu) },
                new SensorConfig { SensorId = "s2", Name = "Sensor B", Group = SensorGroupOrder.SuggestedName(SensorCategory.Gpu) },
            ],
            Fans =
            [
                new FanConfig { FanId = "f1", Name = "Fan 1", Location = FanLocation.CpuCooler },
                new FanConfig { FanId = "f2", Name = "Fan 2", Location = FanLocation.GpuCooler },
            ],
        };

        AirflowTuneResult result = AirflowTuneService.Analyze(config);

        // Without a match each curve would fall back to the primary sensor (the other component's) instead.
        Assert.Equal(new[] { "s1" }, result.SuggestedCurves.Single(c => c.Id == "airflow-cpu").SourceSensorIds);
        Assert.Equal(new[] { "s2" }, result.SuggestedCurves.Single(c => c.Id == "airflow-gpu").SourceSensorIds);
        Assert.DoesNotContain(AirflowHint.NoCpuSensorDetected, result.Hints);
    }

    // ---------------------------------------------------------------------------
    // Plan
    // ---------------------------------------------------------------------------

    [Fact]
    public void Plan_SuggestsByClassifier_InCategoryOrder()
    {
        SensorGroupPlan plan = SensorGroupOrder.Plan([],
        [
            ("iwlwifi_1/temp1", null),
            ("nvme@nvme0/temp1", null),
            ("k10temp/temp1", ""),
            ("nct6798/temp2", "  "),
            ("amdgpu/temp1", null),
        ]);

        Assert.Equal(new[] { "CPU", "GPU", "Speicher", "Mainboard", "Sonstige" }, plan.Names);
        Assert.Equal("CPU", plan.GroupById["k10temp/temp1"]);
        Assert.Equal("Mainboard", plan.GroupById["nct6798/temp2"]); // blank counts as no group
        Assert.Equal("Sonstige", plan.GroupById["iwlwifi_1/temp1"]);
        Assert.True(plan.HasSuggestions);
    }

    [Fact]
    public void Plan_ExistingGroupWins_AndKeepsItsPlace()
    {
        SensorGroupPlan plan = SensorGroupOrder.Plan(["Zeta"],
        [
            ("k10temp/temp1", " Prozessor "),
            ("k10temp/temp2", null),
            ("usb/temp1", "Extern"),
        ]);

        Assert.Equal(new[] { "Zeta", "Extern", "Prozessor", "CPU" }, plan.Names);
        Assert.Equal("Prozessor", plan.GroupById["k10temp/temp1"]); // trimmed, not reclassified
        Assert.Equal("CPU", plan.GroupById["k10temp/temp2"]);
    }

    [Fact]
    public void Plan_SuggestionJoinsExistingGroup_InItsSpelling()
    {
        SensorGroupPlan plan = SensorGroupOrder.Plan(["gpu"], [("k10temp/temp1", "cpu"), ("k10temp/temp2", null), ("amdgpu/temp1", null)]);

        Assert.Equal(new[] { "gpu", "cpu" }, plan.Names);
        Assert.Equal("cpu", plan.GroupById["k10temp/temp2"]);
        Assert.Equal("gpu", plan.GroupById["amdgpu/temp1"]);
    }

    [Fact]
    public void Plan_AllSensorsGrouped_SuggestsNothing()
    {
        SensorGroupPlan plan = SensorGroupOrder.Plan(["CPU"], [("k10temp/temp1", "CPU"), ("nvme/temp1", "Disks")]);

        Assert.False(plan.HasSuggestions);
        Assert.Equal(new[] { "CPU", "Disks" }, plan.Names);
    }
}
