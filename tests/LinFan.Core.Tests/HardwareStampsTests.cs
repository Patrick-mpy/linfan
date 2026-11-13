// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// <see cref="HardwareStamps.Stamp"/> only fills in a device the config does not know yet: a recorded device that differs
/// from the current one is a replacement to report, never something to overwrite.
/// </summary>
public class HardwareStampsTests
{
    private static readonly HardwareDevice Gpu = new("amdgpu", "pci:1002:73bf:1849:5201");
    private static readonly HardwareDevice OtherGpu = new("amdgpu", "pci:1002:744c:1da2:e471");

    private static FanDescriptor Fan(string id, HardwareDevice? device) =>
        new(new FanId(id), "pwm", CanControl: true, Tachometer: null, Source: id, Device: device);

    private static SensorDescriptor Sensor(string id, HardwareDevice? device) =>
        new(new SensorId(id), "temp", SensorKind.Temperature, "°C", id, device);

    [Fact]
    public void AnEntryWithoutDevice_GetsTheDescriptorsDevice()
    {
        var config = new AppConfig
        {
            Fans = [new FanConfig { FanId = "amdgpu/pwm1" }],
            Sensors = [new SensorConfig { SensorId = "amdgpu/temp1" }],
        };

        AppConfig stamped = HardwareStamps.Stamp(
            config, [Fan("amdgpu/pwm1", Gpu)], [Sensor("amdgpu/temp1", Gpu)], out int count);

        Assert.Equal(2, count);
        Assert.Equal(Gpu, stamped.Fans.Single().Device);
        Assert.Equal(Gpu, stamped.Sensors.Single().Device);
    }

    [Fact]
    public void AFingerprintRecordedAsUnknown_IsFilledIn_ForTheSameDeviceName()
    {
        var config = new AppConfig { Fans = [new FanConfig { FanId = "amdgpu/pwm1", Device = new HardwareDevice("amdgpu", null) }] };

        AppConfig stamped = HardwareStamps.Stamp(config, [Fan("amdgpu/pwm1", Gpu)], [], out int count);

        Assert.Equal(1, count);
        Assert.Equal(Gpu, stamped.Fans.Single().Device);
    }

    [Fact]
    public void AFingerprintRecordedAsUnknown_StaysUnknown_ForAnotherDeviceName()
    {
        var recorded = new HardwareDevice("radeon", null);
        var config = new AppConfig { Fans = [new FanConfig { FanId = "gpu/pwm1", Device = recorded }] };

        AppConfig stamped = HardwareStamps.Stamp(config, [Fan("gpu/pwm1", Gpu)], [], out int count);

        Assert.Equal(0, count);
        Assert.Same(config, stamped);
    }

    [Fact]
    public void AnotherRecordedFingerprint_IsLeftForTheUserToResolve()
    {
        var config = new AppConfig
        {
            Fans = [new FanConfig { FanId = "amdgpu/pwm1", Device = OtherGpu }],
            Sensors = [new SensorConfig { SensorId = "amdgpu/temp1", Device = OtherGpu }],
        };

        AppConfig stamped = HardwareStamps.Stamp(
            config, [Fan("amdgpu/pwm1", Gpu)], [Sensor("amdgpu/temp1", Gpu)], out int count);

        Assert.Equal(0, count);
        Assert.Same(config, stamped);
    }

    [Fact]
    public void ADescriptorWithoutDevice_OrAnEntryThatIsNotDetected_StaysAsItIs()
    {
        var config = new AppConfig
        {
            Fans = [new FanConfig { FanId = "thinkpad/pwm1" }, new FanConfig { FanId = "gone/pwm1" }],
        };

        AppConfig stamped = HardwareStamps.Stamp(config, [Fan("thinkpad/pwm1", null)], [], out int count);

        Assert.Equal(0, count);
        Assert.Same(config, stamped);
    }

    [Fact]
    public void AnIdListedTwice_TakesTheFirstDescriptor_WithoutThrowing()
    {
        var config = new AppConfig { Fans = [new FanConfig { FanId = "amdgpu/pwm1" }] };

        AppConfig stamped = HardwareStamps.Stamp(
            config, [Fan("amdgpu/pwm1", Gpu), Fan("amdgpu/pwm1", OtherGpu)], [], out _);

        Assert.Equal(Gpu, stamped.Fans.Single().Device);
    }

    [Fact]
    public void Stamping_TouchesNoOtherField()
    {
        var fan = new FanConfig
        {
            FanId = "amdgpu/pwm1",
            Name = "GPU",
            MinPwm = 60,
            MaxPwm = 200,
            AssignedCurveId = "c",
            Hidden = true,
            Location = FanLocation.GpuCooler,
            RpmSource = "amdgpu/fan1",
            Calibration = new FanCalibration { StartPwm = 60, MinRpm = 500, MaxRpm = 3000 },
        };
        var sensor = new SensorConfig { SensorId = "amdgpu/temp1", Name = "Hotspot", Group = "GPU", Hidden = true };
        var config = new AppConfig { Fans = [fan], Sensors = [sensor], CaseType = CaseType.Server, ActiveProfileId = "p" };

        AppConfig stamped = HardwareStamps.Stamp(
            config, [Fan("amdgpu/pwm1", Gpu)], [Sensor("amdgpu/temp1", Gpu)], out _);

        Assert.Equal(fan with { Device = Gpu }, stamped.Fans.Single());
        Assert.Equal(sensor with { Device = Gpu }, stamped.Sensors.Single());
        Assert.Equal(config with { Fans = stamped.Fans, Sensors = stamped.Sensors }, stamped);
    }
}
