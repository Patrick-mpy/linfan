// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Daemon;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// A second daemon whose endpoint is already served leaves the fans to the first one (security audit run-2, h1), and the
/// snapshot's config is mapped and numbered once per change rather than every tick.
/// </summary>
public partial class ControlLoopServiceTests
{
    [Fact]
    public async Task Start_WhereAnotherDaemonServes_LeavesEveryFanAlone()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer { StartFailure = new IpcEndpointInUseException("/run/linfan/linfan.sock", serving: true) };
        var log = new CapturingLogger<ControlLoopService>();
        var service = NewService(hw, new FakeConfigStore(), ipc, log: log);
        using var cts = new CancellationTokenSource();

        try { await service.StartAsync(cts.Token); }
        catch (IpcEndpointInUseException) { /* the host gets it: the run ends */ }
        await WaitUntilAsync(() => service.ExecuteTask?.IsCompleted == true);
        await service.StopAsync(CancellationToken.None); // what the host does next - restores nothing either

        Assert.True(service.ExecuteTask!.IsFaulted);
        Assert.Equal(0, hw.RestoreCount); // the fans belong to the daemon that answered - not even the start restore
        Assert.Empty(hw.Writes);
        Assert.Empty(ipc.Broadcasts);
        Assert.Null(ipc.CommandHandler);
        Assert.Contains(log.Snapshot(), e => e.Level == LogLevel.Critical && e.Message.Contains("/run/linfan/linfan.sock"));
        Assert.True(service.AnotherDaemonHoldsTheEndpoint); // exit code 3: the unit neither restores nor restarts
    }

    [Fact]
    public async Task Start_WhereAnotherDaemonHoldsTheEndpointButSendsNothing_HandsTheFansToTheHardware_Once()
    {
        // A stopped or stuck daemon may have left a fan on a low manual value with no watchdog behind it.
        var hw = FanRig();
        var ipc = new FakeIpcServer { StartFailure = new IpcEndpointInUseException("/run/linfan/linfan.sock", serving: false) };
        var service = NewService(hw, new FakeConfigStore(), ipc);
        using var cts = new CancellationTokenSource();

        try { await service.StartAsync(cts.Token); }
        catch (IpcEndpointInUseException) { }
        await WaitUntilAsync(() => service.ExecuteTask?.IsCompleted == true);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, hw.RestoreCount); // hardware auto, then nothing more - not even on stop
        Assert.Empty(hw.Writes);
        Assert.True(service.AnotherDaemonHoldsTheEndpoint);
    }

    [Fact]
    public async Task Start_WhereAnotherDaemonHoldsTheEndpointButSendsNothing_InADryRun_TouchesNothing()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer { StartFailure = new IpcEndpointInUseException("/run/linfan/linfan.sock", serving: false) };
        var service = NewService(hw, new FakeConfigStore(), ipc, dryRun: true);
        using var cts = new CancellationTokenSource();

        try { await service.StartAsync(cts.Token); }
        catch (IpcEndpointInUseException) { }
        await WaitUntilAsync(() => service.ExecuteTask?.IsCompleted == true);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, hw.RestoreCount);
    }

    [Fact]
    public async Task Start_WhenTheIpcFailsOtherwise_StillRestores()
    {
        // Any other setup failure keeps the old guarantee: whatever a predecessor left behind goes back to auto.
        var hw = FanRig();
        var ipc = new FakeIpcServer { StartFailure = new IOException("no runtime directory") };
        var service = NewService(hw, new FakeConfigStore(), ipc);
        using var cts = new CancellationTokenSource();

        try { await service.StartAsync(cts.Token); }
        catch (IOException) { }
        await WaitUntilAsync(() => service.ExecuteTask?.IsCompleted == true);
        await service.StopAsync(CancellationToken.None);

        Assert.True(hw.RestoreCount >= 1);
    }

    [Fact]
    public async Task TheSnapshotConfig_IsMappedOncePerChange_AndItsRevisionFollowsIt()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, new FakeConfigStore(), ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 3);

        List<IpcSnapshot> before = ipc.Broadcasts;
        long first = before[0].ConfigRevision!.Value;
        Assert.All(before, s => Assert.Equal(first, s.ConfigRevision));
        Assert.All(before, s => Assert.Same(before[0].Config, s.Config)); // not mapped again per tick

        ipc.Emit(new IpcCommand(IpcCommand.SaveConfig,
            Config: IpcWith(new IpcFanAssignment("hwmon7/pwm1", "Front", 30, 220))));
        await WaitUntilAsync(() => LastSnapshot(ipc)?.ConfigRevision == first + 1);
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 2);
        await service.StopAsync(cts.Token);

        List<IpcSnapshot> after = ipc.Broadcasts.SkipWhile(s => s.ConfigRevision == first).ToList();
        Assert.All(after, s => Assert.Equal(first + 1, s.ConfigRevision));
        Assert.Equal("Front", Assert.Single(after[0].Config!.Fans).Name);
        Assert.All(after, s => Assert.Same(after[0].Config, s.Config));
    }
}
