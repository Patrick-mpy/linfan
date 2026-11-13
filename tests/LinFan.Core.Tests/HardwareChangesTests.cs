// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// <see cref="HardwareChanges"/> reports entries whose device changed and resolves them only as the user decides - an
/// entry is removed by <see cref="HardwareChanges.Forget"/> and nothing else.
/// </summary>
public class HardwareChangesTests
{
    private static readonly HardwareDevice OldGpu = new("amdgpu", "pci:1002:73bf:1849:5201");
    private static readonly HardwareDevice NewGpu = new("amdgpu", "pci:1002:744c:1da2:e471");
    private static readonly HardwareDevice Nvme = new("nvme", "nvme:KIOXIA");
    private static readonly HardwareDevice OtherNvme = new("nvme", "nvme:Samsung SSD 990 PRO");

    private static FanDescriptor Fan(string id, HardwareDevice? device) =>
        new(new FanId(id), "pwm", CanControl: true, Tachometer: null, Source: id, Device: device);

    private static SensorDescriptor Sensor(string id, HardwareDevice? device) =>
        new(new SensorId(id), "temp", SensorKind.Temperature, "°C", id, device);

    /// <summary>A GPU replaced by another model, an NVMe drive replaced as well, a drive removed, and an entry that
    /// never recorded a device and is gone too.</summary>
    private static AppConfig Config() => new()
    {
        Fans =
        [
            new FanConfig { FanId = "amdgpu/pwm1", Name = "GPU", Device = OldGpu },
            new FanConfig { FanId = "gone/pwm1" },
        ],
        Sensors =
        [
            new SensorConfig { SensorId = "amdgpu/temp2", Name = "Hotspot", Device = OldGpu },
            new SensorConfig { SensorId = "amdgpu/temp1", Name = "Edge", Device = OldGpu },
            new SensorConfig { SensorId = "nvme/temp1", Device = OtherNvme },
            new SensorConfig { SensorId = "nvme1/temp1", Device = Nvme },
            new SensorConfig { SensorId = "k10temp/temp1", Device = new HardwareDevice("k10temp", null) },
        ],
    };

    private static readonly FanDescriptor[] Fans = [Fan("amdgpu/pwm1", NewGpu)];

    private static readonly SensorDescriptor[] Sensors =
    [
        Sensor("amdgpu/temp1", NewGpu), Sensor("amdgpu/temp2", NewGpu), Sensor("nvme/temp1", Nvme),
        Sensor("k10temp/temp1", new HardwareDevice("k10temp", null)),
    ];

    [Fact]
    public void Detect_ReportsReplacedDevices_OnePerPair_AndMissingOnes_OnePerRecordedDevice()
    {
        IReadOnlyList<HardwareChange> changes = HardwareChanges.Detect(Config(), Fans, Sensors, dryRun: false);

        Assert.Collection(changes,
            gpu =>
            {
                Assert.Equal((HardwareChangeKind.Replaced, OldGpu, NewGpu), (gpu.Kind, gpu.Old, gpu.New));
                Assert.Equal(["amdgpu/pwm1"], gpu.FanIds);
                Assert.Equal(["amdgpu/temp1", "amdgpu/temp2"], gpu.SensorIds); // ordinal order, not config order
            },
            nvme =>
            {
                Assert.Equal((HardwareChangeKind.Replaced, OtherNvme, Nvme), (nvme.Kind, nvme.Old, nvme.New));
                Assert.Empty(nvme.FanIds);
                Assert.Equal(["nvme/temp1"], nvme.SensorIds);
            },
            noDevice =>
            {
                Assert.Equal((HardwareChangeKind.Missing, null, null), (noDevice.Kind, noDevice.Old, noDevice.New));
                Assert.Equal(["gone/pwm1"], noDevice.FanIds);
                Assert.Empty(noDevice.SensorIds);
            },
            drive =>
            {
                Assert.Equal((HardwareChangeKind.Missing, Nvme, null), (drive.Kind, drive.Old, drive.New));
                Assert.Equal(["nvme1/temp1"], drive.SensorIds);
            });
    }

    [Fact]
    public void Detect_AnUnknownFingerprintOnEitherSide_IsNoReplacement()
    {
        var config = new AppConfig
        {
            Sensors =
            [
                new SensorConfig { SensorId = "a/temp1", Device = new HardwareDevice("amdgpu", null) },
                new SensorConfig { SensorId = "b/temp1", Device = OldGpu },
                new SensorConfig { SensorId = "c/temp1", Device = OldGpu },
                new SensorConfig { SensorId = "d/temp1" },
            ],
        };
        SensorDescriptor[] sensors =
        [
            Sensor("a/temp1", NewGpu), Sensor("b/temp1", new HardwareDevice("amdgpu", null)), Sensor("c/temp1", null),
            Sensor("d/temp1", NewGpu),
        ];

        Assert.Empty(HardwareChanges.Detect(config, [], sensors, dryRun: false));
    }

    [Fact]
    public void Detect_InADryRun_ReportsNothingMissing()
    {
        IReadOnlyList<HardwareChange> changes = HardwareChanges.Detect(Config(), Fans, Sensors, dryRun: true);

        Assert.All(changes, c => Assert.Equal(HardwareChangeKind.Replaced, c.Kind));
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void Detect_KeysDependOnKindAndDevicesOnly_AndAreStableAcrossRuns()
    {
        IReadOnlyList<HardwareChange> changes = HardwareChanges.Detect(Config(), Fans, Sensors, dryRun: false);
        AppConfig reordered = Config() with
        {
            Fans = Config().Fans.Reverse().ToList(),
            Sensors = Config().Sensors.Where(s => s.SensorId != "amdgpu/temp1").Reverse().ToList(),
        };
        IReadOnlyList<HardwareChange> again =
            HardwareChanges.Detect(reordered, Fans.Reverse(), Sensors.Reverse(), dryRun: false);

        Assert.Equal(changes.Select(c => c.Key), again.Select(c => c.Key)); // same order, and one entry fewer keeps the key
        // Pinned: a hash that differs between processes (string.GetHashCode) would make every key stale after a restart.
        Assert.Equal("replaced-1562558ef31dea92", changes[0].Key);
        Assert.Equal("missing-fd061d3c349808a6", changes[2].Key);
        Assert.Equal("missing-0dba2b4af33685a0", changes[3].Key);
    }

    [Fact]
    public void Detect_KeysPassAsAnIpcTarget_WhateverTheDevicesAreCalled()
    {
        string longName = new('x', ConfigLimits.MaxTextLength);
        var config = new AppConfig
        {
            Sensors =
            [
                new SensorConfig { SensorId = "a", Device = new HardwareDevice(longName + "\u001b", longName) },
                new SensorConfig { SensorId = "b", Device = new HardwareDevice("a", "1:b") },
                new SensorConfig { SensorId = "c", Device = new HardwareDevice("a,1:b", null) },
            ],
        };

        IReadOnlyList<HardwareChange> changes = HardwareChanges.Detect(config, [], [], dryRun: false);

        Assert.Equal(3, changes.Select(c => c.Key).Distinct().Count()); // devices that print alike stay apart
        Assert.All(changes, c => Assert.Matches(new Regex("^missing-[0-9a-f]{16}$"), c.Key));
    }

    [Fact]
    public void Accept_RecordsTheNewDevice_AndKeepsEverythingElse()
    {
        AppConfig config = Config() with
        {
            Fans = [new FanConfig { FanId = "amdgpu/pwm1", Name = "GPU", MinPwm = 70, AssignedCurveId = "c", Device = OldGpu }],
        };
        HardwareChange change = HardwareChanges.Detect(config, Fans, Sensors, dryRun: false)[0];

        AppConfig accepted = HardwareChanges.Accept(config, change, resetSensorNames: false);

        Assert.Equal(config.Fans[0] with { Device = NewGpu }, accepted.Fans.Single());
        Assert.Equal(["Hotspot", "Edge"], accepted.Sensors.Where(s => s.Device == NewGpu).Select(s => s.Name));
        Assert.Equal(OtherNvme, accepted.Sensors.Single(s => s.SensorId == "nvme/temp1").Device); // another change
        Assert.DoesNotContain(HardwareChanges.Detect(accepted, Fans, Sensors, dryRun: false), c => c.Key == change.Key);
    }

    [Fact]
    public void Accept_WithResetSensorNames_ClearsTheSensorsNames_ButNotTheFans()
    {
        HardwareChange change = HardwareChanges.Detect(Config(), Fans, Sensors, dryRun: false)[0];

        AppConfig accepted = HardwareChanges.Accept(Config(), change, resetSensorNames: true);

        Assert.Equal("GPU", accepted.Fans.Single(f => f.FanId == "amdgpu/pwm1").Name);
        Assert.All(accepted.Sensors.Where(s => s.SensorId.StartsWith("amdgpu/", StringComparison.Ordinal)),
            s => Assert.Equal("", s.Name));
    }

    [Fact]
    public void Accept_OfAMissingDevice_ChangesNothing()
    {
        AppConfig config = Config();
        HardwareChange missing = HardwareChanges.Detect(config, Fans, Sensors, dryRun: false)
            .First(c => c.Kind == HardwareChangeKind.Missing);

        Assert.Same(config, HardwareChanges.Accept(config, missing, resetSensorNames: true));
    }

    [Fact]
    public void Forget_RemovesTheEntries_AndTheirFansProfileAssignments_ButNoCurveSource()
    {
        var curve = new CurveConfig { Id = "c", SourceSensorIds = ["amdgpu/temp1"] };
        AppConfig config = Config() with
        {
            Curves = [curve],
            Profiles =
            [
                new Profile
                {
                    Id = "p", Curves = [curve],
                    Assignments = [new ProfileAssignment("amdgpu/pwm1", "c"), new ProfileAssignment("cpu/pwm1", "c")],
                },
            ],
        };
        HardwareChange change = HardwareChanges.Detect(config, Fans, Sensors, dryRun: false)[0];

        AppConfig forgotten = HardwareChanges.Forget(config, change);

        Assert.Equal(["gone/pwm1"], forgotten.Fans.Select(f => f.FanId));
        Assert.Equal(["nvme/temp1", "nvme1/temp1", "k10temp/temp1"], forgotten.Sensors.Select(s => s.SensorId));
        Assert.Equal(["cpu/pwm1"], forgotten.Profiles.Single().Assignments.Select(a => a.FanId));
        Assert.Equal(["amdgpu/temp1"], forgotten.Curves.Single().SourceSensorIds);
        Assert.Equal(["amdgpu/temp1"], forgotten.Profiles.Single().Curves.Single().SourceSensorIds);
    }

    [Fact]
    public void Forget_WithNothingToRemove_ChangesNothing()
    {
        AppConfig config = Config();
        var change = new HardwareChange("missing-0", HardwareChangeKind.Missing, null, null, ["other/pwm1"], ["other/temp1"]);

        Assert.Same(config, HardwareChanges.Forget(config, change));
    }
}
