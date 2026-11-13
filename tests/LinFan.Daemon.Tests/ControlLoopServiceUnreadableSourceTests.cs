// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// A configured fan whose curve source is unreadable (here: a sensor the backend does not know) is released from
/// manual control: it goes back to firmware auto instead of holding its manual PWM.
/// </summary>
public partial class ControlLoopServiceTests
{
    [Fact]
    public async Task ReleasedManualFan_WhoseCurveHasNoReadableSource_GoesBackToAuto()
    {
        const string fan = "hwmon7/pwm1";
        var hw = FanRig();                              // "t" stays readable: no blind fail-safe
        var store = new FakeConfigStore
        {
            Exists = true,
            Stored = AppConfig.Empty with
            {
                OnboardingCompleted = true,
                Curves = new[]
                {
                    new CurveConfig
                    {
                        Id = "g", Name = "g", SourceSensorIds = new[] { "gpu" }, SmoothingSeconds = 0,
                        Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
                    },
                },
                Fans = new[] { new FanConfig { FanId = fan, AssignedCurveId = "g" } },
            },
        };
        var ipc = new FakeIpcServer();
        using var cts = new CancellationTokenSource();
        var service = WritingService(hw, store, ipc);
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: fan, Value: 0));
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s && s.Fans.Any(f => f.Id == fan && f.ControlState == "Manual"));
        Assert.Equal(FanMode.Manual, hw.ModeLog[fan]);

        ipc.Emit(new IpcCommand(IpcCommand.SetFanAuto, Target: fan));
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s
            && s.Fans.Any(f => f.Id == fan && f.ControlState == "HardwareAuto" && f.ControlCurveId == "g"));

        Assert.Equal(FanMode.Auto, hw.ModeLog[fan]);    // not left at the manual 0
        Assert.Equal((fan, (byte)0), hw.Writes.ToArray().Last());

        await service.StopAsync(cts.Token);
    }
}
