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

    /// <summary>
    /// A channel whose auto write fails may still sit at a low manual value: it gets full speed, and the restore names
    /// it instead of passing over it silently. /dev/full fails every write, root's included.
    /// </summary>
    [Fact]
    public void RestoreDefaults_FallsBackToFullSpeed_AndReportsTheChannelsItCouldNotRestore()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/full"))
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("testchip");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");                      // auto fails, full speed works
        File.CreateSymbolicLink(Path.Combine(chip, "pwm1_enable"), "/dev/full");
        File.WriteAllText(Path.Combine(chip, "pwm2"), "40");                      // healthy
        File.WriteAllText(Path.Combine(chip, "pwm2_enable"), "1");
        File.WriteAllText(Path.Combine(chip, "pwm3"), "40");                      // no auto mode at all
        File.CreateSymbolicLink(Path.Combine(chip, "pwm4"), "/dev/full");          // nothing works
        File.CreateSymbolicLink(Path.Combine(chip, "pwm4_enable"), "/dev/full");

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);
        backend.RestoreDefaults();

        Assert.Equal("255", File.ReadAllText(Path.Combine(chip, "pwm1")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(chip, "pwm2_enable")));
        Assert.Equal("40", File.ReadAllText(Path.Combine(chip, "pwm2")));
        Assert.Equal("255", File.ReadAllText(Path.Combine(chip, "pwm3")));
        Assert.Collection(backend.LastRestoreProblems.Order(StringComparer.Ordinal),
            p => Assert.StartsWith("testchip/pwm1: Hardware-Auto nicht setzbar", p),
            p => Assert.StartsWith("testchip/pwm4: weder Hardware-Auto", p));

        File.Delete(Path.Combine(chip, "pwm1_enable"));
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "1");
        File.Delete(Path.Combine(chip, "pwm4"));
        File.Delete(Path.Combine(chip, "pwm4_enable"));
        File.WriteAllText(Path.Combine(chip, "pwm4"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm4_enable"), "1");
        backend.RestoreDefaults();
        Assert.Empty(backend.LastRestoreProblems);
    }

    /// <summary>
    /// A mode that cannot be read is no mode: an unreadable manual channel used to show as Auto (security audit run-2,
    /// h6). Reading /proc/self/mem at offset 0 fails with EIO, root's read included - like a flaky driver.
    /// </summary>
    [Fact]
    public void GetMode_OfAnUnreadableEnableFile_Throws_InsteadOfReportingAuto()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mem"))
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("testchip");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");
        File.CreateSymbolicLink(Path.Combine(chip, "pwm1_enable"), "/proc/self/mem");
        File.WriteAllText(Path.Combine(chip, "pwm2"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm2_enable"), "1");
        File.WriteAllText(Path.Combine(chip, "pwm3"), "40");                       // no enable file: no control mode

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);

        Assert.Throws<IOException>(() => backend.GetMode(new FanId("testchip/pwm1")));
        Assert.Equal(FanMode.Manual, backend.GetMode(new FanId("testchip/pwm2")));
        Assert.Equal(FanMode.Auto, backend.GetMode(new FanId("testchip/pwm3")));
    }

    /// <summary>
    /// The CLI's read-only commands dispose the backend too - as root, next to a running daemon. An instance that never
    /// wrote must leave that daemon's fans where they are.
    /// </summary>
    [Fact]
    public void Dispose_WithoutAWrite_LeavesTheChannelsAlone()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("testchip");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "1");

        var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);
        Assert.True(backend.CanControl(new FanId("testchip/pwm1")));
        backend.Dispose();

        Assert.Equal("1", File.ReadAllText(Path.Combine(chip, "pwm1_enable")));
        Assert.Equal("40", File.ReadAllText(Path.Combine(chip, "pwm1")));
    }

    [Fact]
    public void Dispose_AfterAWrite_HandsEveryChannelBack()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("testchip");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "2");
        File.WriteAllText(Path.Combine(chip, "pwm2"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm2_enable"), "1");

        var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);
        backend.SetPwm(new FanId("testchip/pwm1"), 30);
        Assert.Equal("1", File.ReadAllText(Path.Combine(chip, "pwm1_enable")));
        backend.Dispose();

        Assert.Equal("2", File.ReadAllText(Path.Combine(chip, "pwm1_enable")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(chip, "pwm2_enable"))); // like RestoreDefaults: every channel
    }

    [Fact]
    public void Dispose_AfterSettingManualMode_HandsTheChannelBack()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("testchip");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "2");

        var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);
        backend.SetMode(new FanId("testchip/pwm1"), FanMode.Manual);
        backend.Dispose();

        Assert.Equal("2", File.ReadAllText(Path.Combine(chip, "pwm1_enable")));
    }

    [Fact]
    public void ASetPwmThatFailsAfterSwitchingToManual_HandsTheChannelBack_AndThrows()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("testchip");
        string pwm = Path.Combine(chip, "pwm1");
        File.WriteAllText(pwm, "40");
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "2");
        var id = new FanId("testchip/pwm1");
        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);

        // The value write fails after the enable write went through (a directory fails for root too, unlike a mode).
        File.Delete(pwm);
        Directory.CreateDirectory(pwm);

        Assert.ThrowsAny<Exception>(() => backend.SetPwm(id, 30));
        Assert.Equal("2", File.ReadAllText(Path.Combine(chip, "pwm1_enable"))); // not left manual on the old value
    }

    [Fact]
    public void ASetModeManualThatFails_HandsTheChannelBack_AndNamesAFailedHandBack()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("testchip");
        string enable = Path.Combine(chip, "pwm1_enable");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");
        File.WriteAllText(enable, "2");
        var id = new FanId("testchip/pwm1");
        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);

        // The enable file refuses every write: neither manual nor the way back to auto works.
        File.Delete(enable);
        Directory.CreateDirectory(enable);

        var ex = Assert.ThrowsAny<Exception>(() => backend.SetMode(id, FanMode.Manual));
        Assert.Equal("255", File.ReadAllText(Path.Combine(chip, "pwm1"))); // full speed as the safe state instead
        Assert.Contains("Volllast", ex.Message); // the caller learns that auto failed as well
    }

    [Fact]
    public void AManualWriteAfterDispose_IsRefused_TheChannelStaysOnAuto()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("testchip");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "2");
        var id = new FanId("testchip/pwm1");

        var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);
        backend.SetPwm(id, 30);
        backend.Dispose();

        Assert.Throws<ObjectDisposedException>(() => backend.SetPwm(id, 10));
        Assert.Throws<ObjectDisposedException>(() => backend.SetMode(id, FanMode.Manual));
        backend.SetMode(id, FanMode.Auto); // the safe direction stays open
        Assert.Equal("2", File.ReadAllText(Path.Combine(chip, "pwm1_enable")));
        Assert.Equal("30", File.ReadAllText(Path.Combine(chip, "pwm1")));
    }

    /// <summary>
    /// thinkpad_acpi without fan_control=1 refuses every fan write with EPERM, root's included - which access(2) does
    /// not show. The channel is read-only then: the restore leaves it alone instead of reporting it as a failure at every
    /// start, and the backend says why.
    /// </summary>
    [Fact]
    public void Thinkpad_WithFanControlOff_IsReadOnly_AndSaysWhy()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("thinkpad");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "255");
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "0"); // full speed; an unlocked restore would write 2
        rig.ThinkpadFanControl("N");

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);
        var id = new FanId("thinkpad/pwm1");

        Assert.False(backend.CanControl(id));
        FanDescriptor fan = Assert.Single(backend.DiscoverFans());
        Assert.False(fan.CanControl);
        Assert.Equal(FanReadOnlyReason.DriverLocked, fan.ReadOnlyReason);
        Assert.Contains("fan_control=1", backend.StartupWarning);
        Assert.Throws<NotSupportedException>(() => backend.SetPwm(id, 30));
        backend.RestoreDefaults();
        Assert.Empty(backend.LastRestoreProblems);
        Assert.Equal("0", File.ReadAllText(Path.Combine(chip, "pwm1_enable"))); // a read-only channel is not written
    }

    /// <summary>
    /// A ThinkPad fan the EC still holds on a manual level (the module reloaded without fan_control while it was manual)
    /// cannot be released either - but it must not pass for restored.
    /// </summary>
    [Fact]
    public void Thinkpad_WithFanControlOff_OnAManualLevel_IsReportedByTheRestore()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("thinkpad");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "1");
        rig.ThinkpadFanControl("N");

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);
        backend.RestoreDefaults();

        string problem = Assert.Single(backend.LastRestoreProblems);
        Assert.StartsWith("thinkpad/pwm1: steht auf einer manuellen Stufe", problem);
        Assert.Equal("1", File.ReadAllText(Path.Combine(chip, "pwm1_enable")));
    }

    [Theory]
    [InlineData("Y")]
    [InlineData(null)] // parameter not readable: the write check decides, as before
    public void Thinkpad_WithFanControlOnOrUnknown_IsControllable_WithoutAWarning(string? fanControl)
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        string chip = rig.Chip("thinkpad");
        File.WriteAllText(Path.Combine(chip, "pwm1"), "40");
        File.WriteAllText(Path.Combine(chip, "pwm1_enable"), "2");
        if (fanControl is not null)
            rig.ThinkpadFanControl(fanControl);

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);

        Assert.True(backend.CanControl(new FanId("thinkpad/pwm1")));
        Assert.Null(Assert.Single(backend.DiscoverFans()).ReadOnlyReason);
        Assert.Null(backend.StartupWarning);
    }

    /// <summary>Without write access (e.g. no root) a channel is read-only for lack of rights. Root writes anyway.</summary>
    [Fact]
    public void Channel_WithoutWriteAccess_IsReadOnly_ForLackOfRights()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            return;

        using var rig = new FakeSysfs();
        string pwm = Path.Combine(rig.Chip("nct6798"), "pwm1");
        File.WriteAllText(pwm, "40");
        File.SetUnixFileMode(pwm, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);

        FanDescriptor fan = Assert.Single(backend.DiscoverFans());
        Assert.False(fan.CanControl);
        Assert.Equal(FanReadOnlyReason.NoWriteAccess, fan.ReadOnlyReason);
    }

    [Fact]
    public void FanControlOff_OnlyLocksTheThinkpadChip()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var rig = new FakeSysfs();
        File.WriteAllText(Path.Combine(rig.Chip("nct6798"), "pwm1"), "40");
        rig.ThinkpadFanControl("N"); // module loaded, but no thinkpad fan in this machine's hwmon

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);

        Assert.True(backend.CanControl(new FanId("nct6798/pwm1")));
        Assert.Null(backend.StartupWarning);
    }

    /// <summary>
    /// Each chip's channels carry the device its <c>device</c> link leads to: PCI ids, an nvme model, a class device's
    /// PCI parent - and no fingerprint wherever the link leads nowhere useful.
    /// </summary>
    [Fact]
    public void Device_FingerprintsWhatTheDeviceLinkLeadsTo()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mem"))
            return;

        using var rig = new FakeSysfs();
        BuildMachine(rig, linked: true);

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);

        var expected = new Dictionary<string, string?>
        {
            ["amdgpu"] = "pci:1002:1114:17aa:5129",
            ["k10temp"] = "pci:1022:124b:0000:0000",
            ["nvme"] = "nvme:KBG6AZNV1T02 LA KIOXIA",           // the space-padded model, trimmed; no serial
            ["mt7925_phy0"] = "pci:14c3:7925:17aa:e0f4",        // class device: its PCI parent, hex in lowercase
            ["nosub"] = "pci:8086:5678",                        // no subsystem ids at all
            ["thinkpad"] = null,                                // platform device: no ids
            ["acpitz"] = null,                                  // class device with an ACPI parent
            ["odd"] = null,                                     // "device" is a file, but no vendor
            ["drivetemp"] = null,                               // SCSI: vendor is text, not an id
            ["flaky"] = null,                                   // vendor unreadable (EIO)
            ["halfsub"] = null,                                 // one subsystem id only
            ["dangling"] = null,                                // link to nothing
            ["loop"] = null,                                    // link to itself
        };
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal),
            backend.DiscoverSensors().Select(s => s.Id.Value.Split('/')[0]).Distinct().Order(StringComparer.Ordinal));
        foreach (SensorDescriptor sensor in backend.DiscoverSensors())
        {
            string chip = sensor.Id.Value.Split('/')[0];
            Assert.Equal(new HardwareDevice(chip, expected[chip]), sensor.Device);
        }
        foreach (FanDescriptor fan in backend.DiscoverFans())
        {
            string chip = fan.Id.Value.Split('/')[0];
            Assert.Equal(new HardwareDevice(chip, expected[chip]), fan.Device);
        }
    }

    /// <summary>
    /// The model names an nvme drive only where the nvme driver's chip has one that reads; otherwise the PCI parent, or
    /// - for a model that exists but does not read - no fingerprint rather than one that differs from the usual.
    /// </summary>
    [Fact]
    public void Device_NvmeModel_OnlyForTheNvmeChip_AndOnlyWhenItReads()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mem"))
            return;

        using var rig = new FakeSysfs();
        foreach (string nvme in new[] { "nvme0", "nvme1" })
        {
            string pci = rig.Device($"pci0000:00/0000:00:02.{nvme[^1]}/0000:c{nvme[^1]}:00.0",
                ("vendor", "0x1e0f"), ("device", "0x000c"), ("subsystem_vendor", "0x1e0f"), ("subsystem_device", "0x0001"));
            string controller = rig.ClassDevice(pci, $"nvme/{nvme}");
            File.WriteAllText(Path.Combine(rig.ChipOf("nvme", controller), "temp1_input"), "40000");
            if (nvme == "nvme0")
                File.CreateSymbolicLink(Path.Combine(controller, "model"), "/proc/self/mem"); // nvme1 has no model
        }
        string otherPci = rig.Device("pci0000:00/0000:00:03.0", ("vendor", "0x10ec"), ("device", "0x8168"));
        string otherClass = rig.ClassDevice(otherPci, "other/other0", ("model", "Something"));
        File.WriteAllText(Path.Combine(rig.ChipOf("other", otherClass), "temp1_input"), "40000");

        using var backend = new LinuxHwmonBackend(rig.HwmonRoot, rig.ModuleRoot);
        var devices = backend.DiscoverSensors().ToDictionary(s => s.Id.Value, s => s.Device);

        Assert.Equal(new HardwareDevice("nvme", null), devices["nvme@nvme0/temp1"]);
        Assert.Equal(new HardwareDevice("nvme", "pci:1e0f:000c:1e0f:0001"), devices["nvme@nvme1/temp1"]);
        Assert.Equal(new HardwareDevice("other", "pci:10ec:8168"), devices["other/temp1"]);
    }

    /// <summary>
    /// Fail-safe: the device is read next to the channels, never into them. Ids, names, controllability and the channel
    /// count are the same whether the chips have device links (fingerprints, broken links, unreadable ids) or none.
    /// </summary>
    [Fact]
    public void Device_NeverChangesTheChannels()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mem"))
            return;

        using var plain = new FakeSysfs();
        BuildMachine(plain, linked: false);
        using var linked = new FakeSysfs();
        BuildMachine(linked, linked: true);

        using var withoutLinks = new LinuxHwmonBackend(plain.HwmonRoot, plain.ModuleRoot);
        using var withLinks = new LinuxHwmonBackend(linked.HwmonRoot, linked.ModuleRoot);

        Assert.Equal(13 * 2, withoutLinks.DiscoverSensors().Count);
        Assert.Equal(13, withoutLinks.DiscoverFans().Count);
        Assert.Equal(Channels(withoutLinks), Channels(withLinks));
        // No link, no fingerprint - but every channel still names its chip.
        Assert.All(withoutLinks.DiscoverSensors(), s => Assert.Equal(new HardwareDevice(Chip(s.Id.Value), null), s.Device));
        Assert.All(withoutLinks.DiscoverFans(), f => Assert.Equal(new HardwareDevice(Chip(f.Id.Value), null), f.Device));

        static string Chip(string id) => id.Split('/')[0];

        static IEnumerable<string> Channels(LinuxHwmonBackend backend) =>
            backend.DiscoverSensors().Select(s => $"{s.Id.Value} '{s.Name}' {s.Kind} {s.Unit}")
                .Concat(backend.DiscoverFans().Select(f =>
                    $"{f.Id.Value} '{f.Name}' {f.CanControl} {backend.CanControl(f.Id)} {f.Tachometer?.Value} {f.ReadOnlyReason}"));
    }

    /// <summary>
    /// The dev ThinkPad's chips plus the odd cases, one chip per kind of <c>device</c> link, each with a temperature, a
    /// tachometer and a PWM channel. Without <paramref name="linked"/> the same chips have no device link at all.
    /// </summary>
    private static void BuildMachine(FakeSysfs rig, bool linked)
    {
        string gpu = rig.Device("pci0000:00/0000:00:08.1/0000:c5:00.0",
            ("vendor", "0x1002"), ("device", "0x1114"), ("subsystem_vendor", "0x17aa"), ("subsystem_device", "0x5129"));
        string cpu = rig.Device("pci0000:00/0000:00:18.3",
            ("vendor", "0x1022"), ("device", "0x124b"), ("subsystem_vendor", "0x0000"), ("subsystem_device", "0x0000"));
        string ssdPci = rig.Device("pci0000:00/0000:00:02.4/0000:c1:00.0",
            ("vendor", "0x1e0f"), ("device", "0x000c"), ("subsystem_vendor", "0x1e0f"), ("subsystem_device", "0x0001"));
        string ssd = rig.ClassDevice(ssdPci, "nvme/nvme0",
            ("model", "KBG6AZNV1T02 LA KIOXIA                  "), ("serial", "SERIAL-NEVER-READ"));
        string wifiPci = rig.Device("pci0000:00/0000:00:02.2/0000:c3:00.0",
            ("vendor", "0x14C3"), ("device", "0x7925"), ("subsystem_vendor", "0x17AA"), ("subsystem_device", "0xE0F4"));
        string wifi = rig.ClassDevice(wifiPci, "ieee80211/phy0", ("macaddress", "00:11:22:33:44:55"));
        string noSub = rig.Device("pci0000:00/0000:00:01.3", ("vendor", "0x8086"), ("device", "0x5678"));
        string thinkpad = rig.Device("platform/thinkpad_hwmon", ("modalias", "platform:thinkpad_hwmon"));
        string acpi = rig.Device("LNXSYSTM:00/LNXSYBUS:01/LNXTHERM:00", ("hid", "LNXTHERM"));
        string thermalZone = rig.ClassDevice(acpi, "thermal_zone0", ("type", "acpitz"));
        string odd = rig.Device("platform/odd.0", ("device", "0x1234"));
        string scsi = rig.Device("pci0000:00/0000:00:17.0/ata1/host0/target0:0:0/0:0:0:0",
            ("vendor", "ATA     "), ("model", "Samsung SSD 860 "));
        string flaky = rig.Device("pci0000:00/0000:00:01.1", ("device", "0x14ee"));
        File.CreateSymbolicLink(Path.Combine(flaky, "vendor"), "/proc/self/mem");
        string halfSub = rig.Device("pci0000:00/0000:00:01.2",
            ("vendor", "0x8086"), ("device", "0x1234"), ("subsystem_vendor", "0x8086"));

        foreach ((string chip, string device) in new[]
                 {
                     ("amdgpu", gpu), ("k10temp", cpu), ("nvme", ssd), ("mt7925_phy0", wifi), ("nosub", noSub),
                     ("thinkpad", thinkpad), ("acpitz", thermalZone), ("odd", odd), ("drivetemp", scsi),
                     ("flaky", flaky), ("halfsub", halfSub),
                 })
            AddChannels(linked ? rig.ChipOf(chip, device) : rig.Chip(chip));

        string dangling = rig.Chip("dangling");
        string loop = rig.Chip("loop");
        if (linked)
        {
            File.CreateSymbolicLink(Path.Combine(dangling, "device"), "../../devices/gone");
            File.CreateSymbolicLink(Path.Combine(loop, "device"), "device");
        }
        AddChannels(dangling);
        AddChannels(loop);

        static void AddChannels(string chipDir)
        {
            File.WriteAllText(Path.Combine(chipDir, "temp1_input"), "45000");
            File.WriteAllText(Path.Combine(chipDir, "fan1_input"), "1800");
            File.WriteAllText(Path.Combine(chipDir, "pwm1"), "40");
            File.WriteAllText(Path.Combine(chipDir, "pwm1_enable"), "2");
        }
    }

    /// <summary>A scratch directory laid out like <c>/sys/class/hwmon</c> plus <c>/sys/module</c>, removed on dispose.</summary>
    private sealed class FakeSysfs : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "linfan-sysfs-" + Guid.NewGuid().ToString("N"));
        private int _chips;

        public FakeSysfs()
        {
            Directory.CreateDirectory(HwmonRoot);
            Directory.CreateDirectory(ModuleRoot);
        }

        public string HwmonRoot => Path.Combine(_root, "hwmon");
        public string ModuleRoot => Path.Combine(_root, "module");

        public string Chip(string name)
        {
            string dir = Path.Combine(HwmonRoot, $"hwmon{_chips++}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "name"), name);
            return dir;
        }

        /// <summary>A device directory like <c>/sys/devices/pci0000:00/0000:c5:00.0</c> with the given attribute files.</summary>
        public string Device(string path, params (string Name, string Value)[] attributes)
        {
            string dir = Path.Combine(_root, "devices", path);
            Directory.CreateDirectory(dir);
            foreach ((string name, string value) in attributes)
                File.WriteAllText(Path.Combine(dir, name), value + "\n");
            return dir;
        }

        /// <summary>A class device (<c>nvme0</c>, <c>phy0</c>) below <paramref name="parent"/>, linked to it as <c>device</c>.</summary>
        public string ClassDevice(string parent, string path, params (string Name, string Value)[] attributes)
        {
            string dir = Path.Combine(parent, path);
            Directory.CreateDirectory(dir);
            foreach ((string name, string value) in attributes)
                File.WriteAllText(Path.Combine(dir, name), value + "\n");
            File.CreateSymbolicLink(Path.Combine(dir, "device"), Path.GetRelativePath(dir, parent));
            return dir;
        }

        /// <summary>
        /// A chip the way the kernel lays it out: the hwmon directory lives below its device, the hwmon root only links
        /// to it, and its <c>device</c> link is relative to the real directory - resolved against the linked path, it
        /// would miss the device.
        /// </summary>
        public string ChipOf(string name, string device)
        {
            string hwmon = $"hwmon{_chips++}";
            string dir = Path.Combine(device, "hwmon", hwmon);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "name"), name);
            File.CreateSymbolicLink(Path.Combine(dir, "device"), Path.Combine("..", "..", "..", Path.GetFileName(device)));
            Directory.CreateSymbolicLink(Path.Combine(HwmonRoot, hwmon), Path.GetRelativePath(HwmonRoot, dir));
            return dir;
        }

        public void ThinkpadFanControl(string value)
        {
            string dir = Path.Combine(ModuleRoot, "thinkpad_acpi", "parameters");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "fan_control"), value + "\n");
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
