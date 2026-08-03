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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ToConfig_EmptyOrWhitespaceName_FallsBackToOriginal(string emptied)
    {
        var opt = Make("k10temp Tctl");
        opt.Name = emptied;

        Assert.Equal("k10temp Tctl", opt.ToConfig().Name);
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
    public void ToConfig_EmptyGroup_IsNull_NonEmptyGroup_Trimmed()
    {
        var opt = Make("k10temp Tctl");

        opt.Group = "   ";
        Assert.Null(opt.ToConfig().Group);

        opt.Group = "  CPU  ";
        Assert.Equal("CPU", opt.ToConfig().Group);
    }
}
