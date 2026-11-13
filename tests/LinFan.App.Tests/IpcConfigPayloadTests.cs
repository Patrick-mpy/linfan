// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// What the GUI actually puts on the wire for a config. Calibration and tachometer assignment are
/// daemon-owned: a plain save (merge) must not carry them, a full replace (import/restore) must - otherwise
/// restoring a backup silently drops what the backup holds. The daemon side of this was covered, this
/// mapping was not, which is exactly where the field went missing.
/// </summary>
public sealed class IpcConfigPayloadTests
{
    private static AppConfig ConfigWithFan() => new()
    {
        Fans = new[]
        {
            new FanConfig
            {
                FanId = "/lpc/nct6797d/0/control/2",
                Name = "Back",
                RpmSource = "/lpc/nct6797d/0/fan/2",
                Calibration = new FanCalibration { StartPwm = 64, MinRpm = 300, MaxRpm = 1500 },
            },
        },
    };

    [Fact]
    public void Replace_CarriesRpmSourceAndCalibration()
    {
        IpcFanAssignment fan = Assert.Single(
            IpcLiveMonitor.ToIpcConfig(ConfigWithFan(), withDaemonOwned: true).Fans);

        Assert.Equal("/lpc/nct6797d/0/fan/2", fan.RpmSource);
        Assert.NotNull(fan.Calibration);
        Assert.Equal(64, fan.Calibration!.StartPwm);
    }

    [Fact]
    public void Save_OmitsRpmSourceAndCalibration()
    {
        IpcFanAssignment fan = Assert.Single(IpcLiveMonitor.ToIpcConfig(ConfigWithFan()).Fans);

        // The daemon keeps its own values on a merge - sending them would let a stale GUI snapshot win.
        Assert.Null(fan.RpmSource);
        Assert.Null(fan.Calibration);
    }

    [Fact]
    public void Save_CarriesEachFansBaseMinPwm()
    {
        AppConfig config = ConfigWithFan() with
        {
            Fans = new[] { ConfigWithFan().Fans[0] with { MinPwm = 60 }, new FanConfig { FanId = "new", MinPwm = 30 } },
        };
        var baseMinPwm = new Dictionary<string, byte> { ["/lpc/nct6797d/0/control/2"] = 40 };

        IReadOnlyList<IpcFanAssignment> fans = IpcLiveMonitor.ToIpcConfig(config, baseMinPwm: baseMinPwm).Fans;

        Assert.Equal(60, fans[0].MinPwm);
        Assert.Equal(40, fans[0].BaseMinPwm); // the daemon sees the user changed 40 → 60
        Assert.Null(fans[1].BaseMinPwm);      // no base: the sent value wins
    }

    [Fact]
    public void Replace_SendsNoBase()
    {
        IpcFanAssignment fan = Assert.Single(IpcLiveMonitor.ToIpcConfig(ConfigWithFan(), withDaemonOwned: true).Fans);

        Assert.Null(fan.BaseMinPwm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveAndReplace_CarryTheSensorGroupsInOrder(bool withDaemonOwned)
    {
        var config = new AppConfig { SensorGroups = new[] { "GPU", "CPU", "Leer" } };

        IpcConfig ipc = IpcLiveMonitor.ToIpcConfig(config, withDaemonOwned);

        // Never null: null tells the daemon "older GUI, keep your list", so a deliberately empty list must go as [].
        Assert.Equal(new[] { "GPU", "CPU", "Leer" }, ipc.SensorGroups);
        Assert.Empty(IpcLiveMonitor.ToIpcConfig(AppConfig.Empty, withDaemonOwned).SensorGroups!);
    }

    [Fact]
    public void Snapshot_SensorGroups_ReachTheAppConfig()
    {
        MonitorSnapshot snap = IpcLiveMonitor.Convert(SnapshotWith(IpcConfig.Empty with { SensorGroups = new[] { "CPU", "Leer" } }));

        Assert.Equal(new[] { "CPU", "Leer" }, snap.Config.SensorGroups);
    }

    [Fact]
    public void Snapshot_FromADaemonWithoutSensorGroups_ReadsAsEmpty()
    {
        MonitorSnapshot snap = IpcLiveMonitor.Convert(SnapshotWith(IpcConfig.Empty with { SensorGroups = null }));

        Assert.Empty(snap.Config.SensorGroups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveAndReplace_CarryTheCaseType_ByName(bool withDaemonOwned)
    {
        IpcConfig ipc = IpcLiveMonitor.ToIpcConfig(new AppConfig { CaseType = CaseType.Open }, withDaemonOwned);

        Assert.Equal("Open", ipc.CaseType); // never null - null would tell the daemon to keep its own
    }

    [Theory]
    [InlineData("Server", CaseType.Server)]
    [InlineData(null, CaseType.Tower)]      // a daemon that predates the field
    [InlineData("Submarine", CaseType.Tower)] // a newer one with a type this GUI does not know
    public void Snapshot_CaseType_ReachesTheAppConfig(string? sent, CaseType expected)
    {
        MonitorSnapshot snap = IpcLiveMonitor.Convert(SnapshotWith(IpcConfig.Empty with { CaseType = sent }));

        Assert.Equal(expected, snap.Config.CaseType);
    }

    private static IpcSnapshot SnapshotWith(IpcConfig config) =>
        new(DaemonStatus.Active, false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(), config);
}
