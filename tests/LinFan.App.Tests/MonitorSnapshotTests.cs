// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

public class MonitorSnapshotTests
{
    [Fact]
    public void Unavailable_IsNotConnected()
    {
        MonitorSnapshot snap = MonitorSnapshot.Unavailable("Daemon nicht erreichbar", ConnectionProblem.AccessDenied);

        Assert.False(snap.Connected); // steuert den „nicht verbunden"-Banner in der GUI
        Assert.Equal(ConnectionProblem.AccessDenied, snap.Problem);
        Assert.False(snap.DryRun);
        Assert.Empty(snap.Sensors);
        Assert.Empty(snap.Fans);
    }

    [Theory]
    [InlineData(false, ConnectionProblem.None, ConnectionProblem.Unreachable)] // not connected, yet no reason given
    [InlineData(false, ConnectionProblem.Connecting, ConnectionProblem.Connecting)]
    [InlineData(true, ConnectionProblem.AccessDenied, ConnectionProblem.None)] // connected wins
    [InlineData(true, ConnectionProblem.None, ConnectionProblem.None)]
    public void Problem_NeverContradictsConnected(bool connected, ConnectionProblem reported, ConnectionProblem expected)
    {
        var snap = new MonitorSnapshot("x", Array.Empty<SensorReading>(), Array.Empty<FanReading>(), AppConfig.Empty,
                                       Connected: connected, ConnectionProblem: reported);

        Assert.Equal(expected, snap.Problem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Convert_CarriesDryRun_AndReportsNoProblem(bool dryRun)
    {
        var ipc = new IpcSnapshot(DaemonStatus.Active, dryRun, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>());

        MonitorSnapshot snap = IpcLiveMonitor.Convert(ipc);

        Assert.True(snap.Connected);
        Assert.Equal(dryRun, snap.DryRun); // the dry-run banner reads this
        Assert.Equal(ConnectionProblem.None, snap.Problem);
    }

    [Fact]
    public void Convert_MapsControlState_CurveId_AndFailSafe()
    {
        var ipc = new IpcSnapshot(DaemonStatus.Active, false, 40, Array.Empty<IpcSensor>(),
            new[] { new IpcFan("f", "Fan", 900, 128, "Manual", true, ControlState: "CurveHolding", ControlCurveId: "c") },
            FailSafe: "Blind");

        MonitorSnapshot snap = IpcLiveMonitor.Convert(ipc);

        FanReading fan = Assert.Single(snap.Fans);
        Assert.Equal(FanControlState.CurveHolding, fan.ControlState);
        Assert.Equal("c", fan.ControlCurveId);
        Assert.Equal(FailSafeState.Blind, snap.FailSafe);
    }

    [Fact]
    public void Convert_MapsTheConfigProblem_AndStripsControlCharactersFromThePath()
    {
        var ipc = new IpcSnapshot(DaemonStatus.Active, false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(),
            ConfigProblem: "SetAside", ConfigSetAsidePath: "/etc/linfan/config.json.invalid\u001b[2J");

        MonitorSnapshot snap = IpcLiveMonitor.Convert(ipc);

        Assert.Equal(ConfigLoadProblem.SetAside, snap.ConfigProblem);
        Assert.Equal("/etc/linfan/config.json.invalid[2J", snap.ConfigSetAsidePath);
        Assert.Null(IpcLiveMonitor.Convert(ipc with { ConfigProblem = "Unheard" }).ConfigProblem);
    }

    [Fact]
    public void Convert_CarriesTheDaemonVersion_WithoutControlCharacters()
    {
        var ipc = new IpcSnapshot(DaemonStatus.Active, false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(),
            DaemonVersion: "0.5.0+abc\u202e");

        Assert.Equal("0.5.0+abc", IpcLiveMonitor.Convert(ipc).DaemonVersion);
        Assert.Null(IpcLiveMonitor.Convert(ipc with { DaemonVersion = null }).DaemonVersion);
    }

    [Theory]
    [InlineData("DriverLocked", false, FanReadOnlyReason.DriverLocked)]
    [InlineData("SomethingNew", false, null)] // a newer daemon's reason reads as unknown
    [InlineData("DriverLocked", true, null)]  // a controllable fan has no reason, whatever the wire says
    public void Convert_MapsTheReadOnlyReason(string name, bool canControl, FanReadOnlyReason? expected)
    {
        var ipc = new IpcSnapshot(DaemonStatus.Active, false, 40, Array.Empty<IpcSensor>(),
            new[] { new IpcFan("f", "Fan", null, 0, "Auto", canControl, ReadOnlyReason: name) });

        Assert.Equal(expected, Assert.Single(IpcLiveMonitor.Convert(ipc).Fans).ReadOnlyReason);
    }

    [Theory]
    [InlineData(null)]           // a daemon that predates the field
    [InlineData("SomethingNew")] // a newer daemon's state
    [InlineData("curve")]        // names only, exact case
    [InlineData("0")]            // no ordinals
    public void Convert_MissingOrUnknownNames_AreUnknown(string? name)
    {
        var ipc = new IpcSnapshot(DaemonStatus.Active, false, 40, Array.Empty<IpcSensor>(),
            new[] { new IpcFan("f", "Fan", null, 0, "Auto", true, ControlState: name) }, FailSafe: name);

        MonitorSnapshot snap = IpcLiveMonitor.Convert(ipc);

        Assert.Null(Assert.Single(snap.Fans).ControlState);
        Assert.Null(snap.FailSafe);
    }
}
