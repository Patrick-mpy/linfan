// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// <see cref="HardwareDevice"/> takes text from drivers, libraries and hand-edited files: whichever way an instance is
/// made, its text reaches logs, the config and the GUI only trimmed, without control or format characters and bounded.
/// </summary>
public class HardwareDeviceTests
{
    [Fact]
    public void Constructor_Trims_BothValues()
    {
        var device = new HardwareDevice("  nvme\n", "nvme:KBG6AZNV1T02 LA KIOXIA                  \n");

        Assert.Equal("nvme", device.Name);
        Assert.Equal("nvme:KBG6AZNV1T02 LA KIOXIA", device.Fingerprint);
    }

    [Fact]
    public void Constructor_StripsControlAndFormatCharacters()
    {
        var device = new HardwareDevice("amd\u001b[2Jgpu", "pci:1002\n:1114‮");

        Assert.Equal("amd[2Jgpu", device.Name);
        Assert.Equal("pci:1002:1114", device.Fingerprint);
    }

    /// <summary>
    /// An empty fingerprint is unknown: as text it would differ from the one the backend reports and read as another
    /// device.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0000​")] // only characters that are stripped
    public void Constructor_AnEmptyFingerprint_IsNull_AndReadsAsNoReplacement(string? fingerprint)
    {
        var recorded = new HardwareDevice("amdgpu", fingerprint);

        Assert.Null(recorded.Fingerprint);
        var config = new AppConfig { Sensors = [new SensorConfig { SensorId = "amdgpu/temp1", Device = recorded }] };
        SensorDescriptor detected = new(new SensorId("amdgpu/temp1"), "edge", SensorKind.Temperature, "°C", "amdgpu/temp1",
            new HardwareDevice("amdgpu", "pci:1002:73bf:1849:5201"));
        Assert.Empty(HardwareChanges.Detect(config, [], [detected], dryRun: false));
    }

    [Fact]
    public void Constructor_ANullOrEmptyName_IsAnEmptyString()
    {
        Assert.Equal("", new HardwareDevice(null, "x").Name);
        Assert.Equal("", new HardwareDevice(" \t", "x").Name);
    }

    [Fact]
    public void Constructor_CapsEachValue_AndTrimsWhatTheCutLeavesAtTheEnd()
    {
        string longText = new string('a', HardwareDevice.MaxLength - 1) + " " + new string('b', 50);

        var device = new HardwareDevice(longText, longText);

        Assert.Equal(new string('a', HardwareDevice.MaxLength - 1), device.Name);
        Assert.Equal(device.Name, device.Fingerprint);
        Assert.Equal(HardwareDevice.MaxLength, new HardwareDevice(new string('c', 500), null).Name.Length);
    }

    [Fact]
    public void Constructor_NeverCutsASurrogatePairInHalf()
    {
        string text = new string('a', HardwareDevice.MaxLength - 1) + "🌀🌀";

        string name = new HardwareDevice(text, null).Name;

        Assert.Equal(new string('a', HardwareDevice.MaxLength - 1), name);
    }

    /// <summary>Stamping and detection compare devices by value - two that describe the same device are equal.</summary>
    [Fact]
    public void Equality_IsByTheCleanedValues()
    {
        Assert.Equal(new HardwareDevice("amdgpu", "pci:1002:73bf"), new HardwareDevice(" amdgpu\n", "pci:1002:73bf "));
        Assert.Equal(new HardwareDevice("nvme", null), new HardwareDevice("nvme", "  "));
        Assert.NotEqual(new HardwareDevice("amdgpu", "pci:1002:73bf"), new HardwareDevice("amdgpu", "pci:1002:744c"));
        Assert.NotEqual(new HardwareDevice("amdgpu", null), new HardwareDevice("radeon", null));
    }
}
