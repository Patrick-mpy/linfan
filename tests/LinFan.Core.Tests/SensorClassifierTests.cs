// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public sealed class SensorClassifierTests
{
    [Theory]
    // Linux: chip[@bus]/channel - the chip is the hwmon driver.
    [InlineData("k10temp/temp1", SensorCategory.Cpu)]
    [InlineData("coretemp@coretemp.0/temp2", SensorCategory.Cpu)]
    [InlineData("zenpower/temp1", SensorCategory.Cpu)]
    [InlineData("cpu_thermal/temp1", SensorCategory.Cpu)]
    [InlineData("amdgpu@0000:03:00.0/temp1", SensorCategory.Gpu)]
    [InlineData("radeon/temp1", SensorCategory.Gpu)]
    [InlineData("nouveau/temp1", SensorCategory.Gpu)]
    [InlineData("i915/temp1", SensorCategory.Gpu)]
    [InlineData("nvme@nvme0/temp1", SensorCategory.Storage)]
    [InlineData("drivetemp@0:0:0:0/temp1", SensorCategory.Storage)]
    [InlineData("nct6798/temp2", SensorCategory.Mainboard)]
    [InlineData("it8688/temp1", SensorCategory.Mainboard)]
    [InlineData("acpitz/temp1", SensorCategory.Mainboard)]
    [InlineData("asus_wmi_sensors/temp3", SensorCategory.Mainboard)]
    [InlineData("gigabyte_wmi/temp4", SensorCategory.Mainboard)]
    [InlineData("thinkpad/temp1", SensorCategory.Mainboard)]
    [InlineData("iwlwifi_1/temp1", SensorCategory.Other)]
    [InlineData("spd5118@0-0051/temp1", SensorCategory.Other)]
    [InlineData("BAT0/temp1", SensorCategory.Other)]
    [InlineData("hwmon3/temp1", SensorCategory.Other)] // the backend's fallback when a chip has no stable address
    // Windows: LibreHardwareMonitor identifier - the first segment names the hardware class.
    [InlineData("/amdcpu/0/temperature/2", SensorCategory.Cpu)]
    [InlineData("/intelcpu/0/temperature/0", SensorCategory.Cpu)]
    [InlineData("/gpu-nvidia/0/temperature/0", SensorCategory.Gpu)]
    [InlineData("/gpu-amd/0/temperature/1", SensorCategory.Gpu)]
    [InlineData("/nvme/0/temperature/0", SensorCategory.Storage)]
    [InlineData("/hdd/1/temperature/0", SensorCategory.Storage)]
    [InlineData("/lpc/nct6797d/0/temperature/1", SensorCategory.Mainboard)]
    [InlineData("/memory/dimm/0/temperature/0", SensorCategory.Other)]
    // macOS: smc/<key> - the key prefix is case-sensitive.
    [InlineData("smc/TC0P", SensorCategory.Cpu)]
    [InlineData("smc/Tp01", SensorCategory.Cpu)]
    [InlineData("smc/Te05", SensorCategory.Cpu)]
    [InlineData("smc/TG0D", SensorCategory.Gpu)]
    [InlineData("smc/Tg05", SensorCategory.Gpu)]
    [InlineData("smc/TCGC", SensorCategory.Gpu)] // integrated graphics, despite the CPU prefix
    [InlineData("smc/TH0x", SensorCategory.Storage)]
    [InlineData("smc/Tm0P", SensorCategory.Mainboard)]
    [InlineData("smc/TPCD", SensorCategory.Mainboard)]
    [InlineData("smc/Tp0P", SensorCategory.Other)] // PSU on Intel Macs, a CPU core on M1
    [InlineData("smc/Tf04", SensorCategory.Other)] // M3 uses Tf for CPU and GPU cores alike
    [InlineData("smc/TM0P", SensorCategory.Other)] // memory proximity - not Tm (mainboard)
    [InlineData("smc/TB0T", SensorCategory.Other)]
    [InlineData("", SensorCategory.Other)]
    public void Classify_ByTheHardwarePartOfTheId(string sensorId, SensorCategory expected) =>
        Assert.Equal(expected, SensorClassifier.Classify(sensorId));

    [Fact]
    public void Classify_IgnoresCase_OutsideSmcKeys()
    {
        Assert.Equal(SensorCategory.Cpu, SensorClassifier.Classify("K10Temp/temp1"));
        Assert.Equal(SensorCategory.Mainboard, SensorClassifier.Classify("NCT6798/temp1"));
    }
}
