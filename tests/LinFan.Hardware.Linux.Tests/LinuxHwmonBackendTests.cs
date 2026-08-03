// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using LinFan.Core.Models;
using LinFan.Hardware.Linux;
using Xunit;

namespace LinFan.Hardware.Linux.Tests;

// Linux-spezifische Tests; zur Laufzeit zusätzlich per OperatingSystem.IsLinux() abgesichert.
[SupportedOSPlatform("linux")]
public class LinuxHwmonBackendTests
{
    [Fact]
    public void Discovery_OnLinux_DoesNotThrow_AndValuesAreNeverInfinite()
    {
        if (!OperatingSystem.IsLinux())
            return; // Backend ist linux-spezifisch; Windows/macOS folgen in Phase 2/3

        using var backend = new LinuxHwmonBackend();

        var sensors = backend.DiscoverSensors();
        Assert.NotNull(sensors);

        // Lesen darf nie werfen: nicht lesbare Kanäle (z. B. EIO) liefern NaN.
        foreach (var s in sensors)
        {
            double v = backend.ReadValue(s.Id);
            Assert.True(double.IsNaN(v) || double.IsFinite(v), $"{s.Id} lieferte {v}");
        }

        // Lüfter-Discovery ist ebenfalls fehlerfrei und konsistent.
        var fans = backend.DiscoverFans();
        Assert.NotNull(fans);
        foreach (var f in fans)
            Assert.Equal(f.CanControl, backend.CanControl(f.Id));
    }

    [Fact]
    public void ReadValue_UnknownSensor_Throws()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var backend = new LinuxHwmonBackend();
        Assert.Throws<KeyNotFoundException>(() => backend.ReadValue(new SensorId("does/not-exist")));
    }

    [Theory]
    [InlineData(SensorKind.Temperature, 45000, 45.0)]
    [InlineData(SensorKind.Temperature, 51900, 51.9)]
    [InlineData(SensorKind.FanRpm, 1972, 1972.0)]
    [InlineData(SensorKind.FanRpm, 0, 0.0)]
    public void InterpretRaw_ConvertsKnownValues(SensorKind kind, long raw, double expected) =>
        Assert.Equal(expected, LinuxHwmonBackend.InterpretRaw(kind, raw), 3);

    [Fact]
    public void InterpretRaw_FanSentinel0xFFFF_IsNaN() =>
        Assert.True(double.IsNaN(LinuxHwmonBackend.InterpretRaw(SensorKind.FanRpm, 65535)));
}
