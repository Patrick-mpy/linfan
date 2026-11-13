// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// The coordinators tell the loop what they do with each fan they take over, and the broadcast snapshot shows it -
/// target and throttled fans alike, also fans without a config entry (the fresh config here has none).
/// </summary>
public partial class ControlLoopServiceTests
{
    private static string? ControlOf(IpcSnapshot snapshot, string fanId) =>
        snapshot.Fans.FirstOrDefault(f => f.Id == fanId)?.ControlState;

    [Fact]
    public async Task Snapshot_DuringIdentify_ShowsTargetIdentifying_AndOthersThrottled()
    {
        var hw = TwoFanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // holds the identify pulse
        var service = new ControlLoopService(
            hw, hw, store, ipc, NullLogger<ControlLoopService>.Instance, FastTick,
            null, (_, ct) => gate.Task.WaitAsync(ct), dryRunOverride: false);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s && ControlOf(s, "A") == nameof(FanControlState.HardwareAuto));

        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "B"));
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s
            && ControlOf(s, "B") == nameof(FanControlState.Identifying)
            && ControlOf(s, "A") == nameof(FanControlState.IdentifyThrottled));
        Assert.Equal(nameof(FailSafeState.None), LastSnapshot(ipc)!.FailSafe);

        gate.SetResult();
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s
            && ControlOf(s, "A") == nameof(FanControlState.HardwareAuto)
            && ControlOf(s, "B") == nameof(FanControlState.HardwareAuto));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Snapshot_DuringCalibration_ShowsOnlyTheTargetCalibrating()
    {
        var hw = TwoFanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // holds the calibration ramp
        var service = new ControlLoopService(
            hw, hw, store, ipc, NullLogger<ControlLoopService>.Instance, FastTick,
            (s, f) => new CalibrationService(s, f, (_, ct) => gate.Task.WaitAsync(ct)), NoDelay,
            dryRunOverride: false);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "A"));
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s && ControlOf(s, "A") == nameof(FanControlState.Calibrating));
        Assert.Equal(nameof(FanControlState.HardwareAuto), ControlOf(LastSnapshot(ipc)!, "B"));

        gate.SetResult();
        await service.StopAsync(cts.Token);
    }
}
