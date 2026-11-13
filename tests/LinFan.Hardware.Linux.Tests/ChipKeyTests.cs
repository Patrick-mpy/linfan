// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Hardware.Linux;
using Xunit;
using ChipDir = LinFan.Hardware.Linux.LinuxHwmonBackend.ChipDir;

namespace LinFan.Hardware.Linux.Tests;

/// <summary>
/// Reine Tests der Chip-Schlüssel-Bestimmung (<see cref="LinuxHwmonBackend.ResolveChipKeys"/>) - die
/// Logik hinter den stabilen <c>chip/channel</c>-Ids. Ohne Hardware, deterministisch. (Die Logik selbst
/// ist plattformneutral; das Attribut spiegelt nur den Linux-Typ, auf dem sie sitzt - CA1416.)
/// </summary>
[SupportedOSPlatform("linux")]
public class ChipKeyTests
{
    private static ChipDir Dir(string hwmon, string chip, string? bus = null) => new(
        Dir: "/sys/class/hwmon/" + hwmon, HwmonName: hwmon, Chip: chip, BusAddr: bus);

    [Fact]
    public void UniqueChipNames_UseBareChipName()
    {
        var keys = LinuxHwmonBackend.ResolveChipKeys(new[]
        {
            Dir("hwmon6", "k10temp", "0000:00:18.3"),
            Dir("hwmon3", "nct6797", "nct6775.2592"),
            Dir("hwmon1", "amdgpu", "0000:03:00.0"),
        });

        Assert.Equal("k10temp", keys["hwmon6"]);
        Assert.Equal("nct6797", keys["hwmon3"]);
        Assert.Equal("amdgpu", keys["hwmon1"]);
    }

    [Fact]
    public void ChipKeys_KeepTheDriverInFront_WhereTheSensorClassifierReadsIt()
    {
        // Sensor ids are "<chip key>/<channel>" (AddSensor); the GUI groups sensors by the driver before '@' or '/'.
        var keys = LinuxHwmonBackend.ResolveChipKeys(new[]
        {
            Dir("hwmon2", "coretemp", "0000:00:00.0"),
            Dir("hwmon5", "coretemp", "0000:80:00.0"),
            Dir("hwmon1", "amdgpu", "0000:03:00.0"),
            Dir("hwmon4", "nvme", "nvme0"),
            Dir("hwmon3", "nct6797", "nct6775.2592"),
        });

        SensorCategory Classify(string hwmon) => SensorClassifier.Classify($"{keys[hwmon]}/temp1");

        Assert.Equal(SensorCategory.Cpu, Classify("hwmon2")); // coretemp@0000:00:00.0
        Assert.Equal(SensorCategory.Cpu, Classify("hwmon5"));
        Assert.Equal(SensorCategory.Gpu, Classify("hwmon1"));
        Assert.Equal(SensorCategory.Storage, Classify("hwmon4"));
        Assert.Equal(SensorCategory.Mainboard, Classify("hwmon3"));
    }

    [Fact]
    public void DuplicateChipNames_DisambiguateByBusAddress()
    {
        var keys = LinuxHwmonBackend.ResolveChipKeys(new[]
        {
            Dir("hwmon2", "coretemp", "0000:00:00.0"),
            Dir("hwmon5", "coretemp", "0000:80:00.0"),
            Dir("hwmon6", "k10temp", "0000:00:18.3"),
        });

        Assert.Equal("coretemp@0000:00:00.0", keys["hwmon2"]);
        Assert.Equal("coretemp@0000:80:00.0", keys["hwmon5"]);
        Assert.Equal("k10temp", keys["hwmon6"]); // der eindeutige bleibt schlicht
    }

    [Fact]
    public void DuplicateChipNames_WithoutBusAddress_FallBackToHwmonName()
    {
        var keys = LinuxHwmonBackend.ResolveChipKeys(new[]
        {
            Dir("hwmon2", "nvme", bus: null),
            Dir("hwmon4", "nvme", bus: null),
        });

        // Ohne Bus-Adresse bleibt nur der (instabile) hwmon-Name als letzter Ausweg - aber eindeutig.
        Assert.Equal("hwmon2", keys["hwmon2"]);
        Assert.Equal("hwmon4", keys["hwmon4"]);
        Assert.NotEqual(keys["hwmon2"], keys["hwmon4"]);
    }

    [Fact]
    public void IdenticalChipNameAndBusAddress_StillResolveUniquely()
    {
        // Pathologisch (sollte real nicht vorkommen): gleicher Name UND gleiche Adresse.
        var keys = LinuxHwmonBackend.ResolveChipKeys(new[]
        {
            Dir("hwmon7", "dummy", "isa-0000"),
            Dir("hwmon8", "dummy", "isa-0000"),
        });

        Assert.NotEqual(keys["hwmon7"], keys["hwmon8"]);
        Assert.Equal(2, new HashSet<string>(keys.Values).Count);
    }

    [Fact]
    public void Deterministic_IndependentOfInputOrder()
    {
        var a = new[]
        {
            Dir("hwmon2", "coretemp", "0000:00:00.0"),
            Dir("hwmon5", "coretemp", "0000:80:00.0"),
        };
        var b = new[]
        {
            Dir("hwmon5", "coretemp", "0000:80:00.0"),
            Dir("hwmon2", "coretemp", "0000:00:00.0"),
        };

        Assert.Equal(
            LinuxHwmonBackend.ResolveChipKeys(a)["hwmon2"],
            LinuxHwmonBackend.ResolveChipKeys(b)["hwmon2"]);
    }
}
