// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// A manually driven fan without a config entry (a fresh install has none) goes back to firmware auto when its
/// manual value is released - by the GUI or by the last GUI disconnecting - instead of keeping its manual PWM.
/// </summary>
public partial class ControlLoopServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedManualFan_WithoutConfigEntry_GoesBackToAuto(bool byDisconnect)
    {
        const string fan = "hwmon7/pwm1";
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        using var cts = new CancellationTokenSource();
        var service = WritingService(hw, store, ipc);
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => LastSnapshot(ipc) is not null);
        Assert.DoesNotContain(LastSnapshot(ipc)!.Config!.Fans, f => f.FanId == fan); // the daemon's live config

        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: fan, Value: 200));
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s && s.Fans.Any(f => f.Id == fan && f.ControlState == "Manual"));
        Assert.Contains((fan, (byte)200), hw.Writes.ToArray());

        if (byDisconnect)
            ipc.EmitClients(0);
        else
            ipc.Emit(new IpcCommand(IpcCommand.SetFanAuto, Target: fan));

        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s
            && s.Fans.Any(f => f.Id == fan && f.ControlState == "HardwareAuto" && !f.ManualOverride));
        Assert.Equal(FanMode.Auto, hw.ModeLog[fan]);   // truthfully on firmware auto, not left at pwm 200

        await service.StopAsync(cts.Token);
    }
}
