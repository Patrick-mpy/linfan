// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert das Trim-/Fallback-Verhalten von <see cref="SensorOption.ToConfig"/> ab: ein geleerter
/// Anzeigename darf nicht still als leerer String persistiert werden (sonst Datenverlust).
/// </summary>
public sealed class SensorOptionTests
{
    private static SensorOption Make(string name) =>
        new("hwmon6/temp1", name, visible: true, group: null, unit: "°C");

    [Fact]
    public void ToConfig_TrimsSurroundingWhitespace()
    {
        var opt = Make("k10temp Tctl");
        opt.Name = "  CPU-Paket  ";

        Assert.Equal("CPU-Paket", opt.ToConfig().Name);
    }

    // Without the label (an older daemon) an own name cannot be told from it - an emptied field keeps the loaded name.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ToConfig_WithoutALabel_EmptyName_FallsBackToOriginal(string emptied)
    {
        var opt = Make("k10temp Tctl");
        opt.Name = emptied;

        Assert.Equal("k10temp Tctl", opt.ToConfig().Name);
    }

    private static SensorOption WithLabel(string name, string label = "k10temp Tctl") =>
        new("k10temp/temp1", name, hardwareLabel: label);

    // The label is no own name: saved as one it stayed when the hardware behind the id changed (a replaced graphics card
    // kept the old model's name).
    [Theory]
    [InlineData("k10temp Tctl")]
    [InlineData(" k10temp Tctl ")]
    [InlineData("")]
    [InlineData("  ")]
    public void ToConfig_TheLabelOrAnEmptyField_IsNoOwnName(string name)
    {
        var opt = WithLabel("k10temp Tctl");
        opt.Name = name;

        Assert.Equal("", opt.ToConfig().Name);
    }

    [Fact]
    public void ToConfig_KeepsAnOwnName()
    {
        Assert.Equal("CPU", WithLabel("CPU").ToConfig().Name);
    }

    [Fact]
    public void ResetName_BringsBackTheLabel_AndIsOnlyOfferedForAnotherName()
    {
        var opt = WithLabel("AMD Radeon RX 6600 XT GPU Core", label: "AMD Radeon RX 9070 GPU Core");
        Assert.True(opt.CanResetName);
        Assert.Equal("AMD Radeon RX 9070 GPU Core", opt.NamePlaceholder);

        opt.ResetNameCommand.Execute(null);

        Assert.Equal("AMD Radeon RX 9070 GPU Core", opt.Name);
        Assert.False(opt.CanResetName);
        Assert.False(opt.ResetNameCommand.CanExecute(null));
        Assert.Equal("", opt.ToConfig().Name);
    }

    [Fact]
    public void ApplyConfig_WithoutAnOwnName_ShowsTheLabel()
    {
        var opt = WithLabel("CPU");

        opt.ApplyConfig(new SensorConfig { SensorId = "k10temp/temp1", Name = "" });

        Assert.Equal("k10temp Tctl", opt.Name);
    }

    [Fact]
    public void FillEmptyName_ShowsTheLabel()
    {
        var opt = WithLabel("CPU");
        opt.Name = " ";

        opt.FillEmptyName();

        Assert.Equal("k10temp Tctl", opt.Name);
    }

    // The airflow analysis names a sensor without an own name by its label; a sensor without a known label is left out.
    [Fact]
    public void Labels_ListsTheKnownHardwareLabels()
    {
        IReadOnlyDictionary<string, string> labels = SensorOption.Labels([WithLabel("CPU"), Make("k10temp Tctl")]);

        Assert.Equal("k10temp Tctl", Assert.Single(labels).Value);
    }

    [Fact]
    public void ToConfig_KeepsIdAndVisibility()
    {
        var opt = Make("k10temp Tctl");
        opt.Visible = false;

        SensorConfig cfg = opt.ToConfig();

        Assert.Equal("hwmon6/temp1", cfg.SensorId);
        Assert.True(cfg.Hidden);
    }

    [Fact]
    public void Selection_IsViewStateOnly_NotPartOfTheConfig()
    {
        var opt = Make("k10temp Tctl");
        SensorConfig before = opt.ToConfig();

        opt.IsSelected = true;
        opt.IsDragged = true;

        Assert.Equal(before, opt.ToConfig());
    }

    [Fact]
    public void GroupLabel_IsTheTrimmedGroup_OrUngrouped()
    {
        var opt = Make("k10temp Tctl");
        Assert.Equal("Ungruppiert", opt.GroupLabel);

        var changed = new List<string?>();
        opt.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        opt.Group = " CPU ";

        Assert.Equal("CPU", opt.GroupLabel);
        Assert.Contains(nameof(SensorOption.GroupLabel), changed); // the chip follows a move
    }

    [Fact]
    public void ToConfig_EmptyGroup_IsNull_NonEmptyGroup_Trimmed()
    {
        var opt = Make("k10temp Tctl");

        opt.Group = "   ";
        Assert.Null(opt.ToConfig().Group);

        opt.Group = "  CPU  ";
        Assert.Equal("CPU", opt.ToConfig().Group);
    }
}
