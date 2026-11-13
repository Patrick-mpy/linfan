// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// A reload from a file that no longer lists a fan the curve drove: the fan is handed back to firmware auto by the
/// next tick, while the reload itself leaves every other fan alone (it stays harmless and repeatable).
/// </summary>
public partial class ControlLoopServiceTests
{
    [Fact]
    public async Task Reload_WithoutAFanTheCurveDrove_HandsThatFanBack()
    {
        var hw = CurveRig();
        FakeConfigStore store = CurveStore();
        var ipc = new FakeIpcServer();
        using var cts = new CancellationTokenSource();
        var service = await StartOnCurveAsync(WritingService(hw, store, ipc), hw, ipc, cts.Token);

        store.Stored = store.Stored with { Fans = store.Stored.Fans.Where(f => f.FanId == "A").ToArray() };
        ipc.Emit(new IpcCommand(IpcCommand.Reload));

        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s
            && s.Fans.Any(f => f.Id == "B" && f.ControlState == "HardwareAuto"));
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);

        Assert.Equal(FanMode.Auto, hw.ModeLog["B"]);    // not left at the curve's 153
        Assert.Equal(1, CurveWrites(hw, "B"));
        Assert.Equal(FanMode.Manual, hw.ModeLog["A"]);  // A stays on its curve
        Assert.Equal("Curve", LastSnapshot(ipc)!.Fans.Single(f => f.Id == "A").ControlState);

        await service.StopAsync(cts.Token);
    }
}
