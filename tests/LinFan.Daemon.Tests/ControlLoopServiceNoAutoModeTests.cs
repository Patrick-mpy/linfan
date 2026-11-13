// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// A channel with a writable PWM but no auto mode (no <c>pwmN_enable</c>, e.g. dell_smm_hwmon) cannot be handed to
/// the firmware. A fan the loop drove is handed back at full speed instead, once, like RestoreDefaults - and the
/// daemon says so once, not every tick. A fan the loop never drove is not rewritten every tick; the start-up
/// RestoreDefaults has already put it to full speed.
/// </summary>
public partial class ControlLoopServiceTests
{
    private const string NoAutoFan = "hwmon7/pwm1";

    private static int NoAutoModeWarnings(CapturingLogger<ControlLoopService> log) =>
        log.Snapshot().Count(e => e.Level == LogLevel.Warning && e.Message.Contains(NoAutoFan));

    [Fact]
    public async Task ReleasedManualFan_WithoutAutoMode_GoesToFullSpeedOnce_AndWarnsOnce()
    {
        var hw = FanRig();
        hw.NoAutoMode.Add(NoAutoFan);
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        using var cts = new CancellationTokenSource();
        var service = WritingService(hw, new FakeConfigStore(), ipc, log: log);
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: NoAutoFan, Value: 0));
        await WaitUntilAsync(() => hw.Writes.ToArray().Contains((NoAutoFan, (byte)0)));

        ipc.Emit(new IpcCommand(IpcCommand.SetFanAuto, Target: NoAutoFan));
        await WaitUntilAsync(() => hw.Writes.ToArray().Contains((NoAutoFan, (byte)255)));
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 5);

        Assert.Equal(1, hw.Writes.ToArray().Count(w => w == (NoAutoFan, (byte)255))); // not rewritten every tick
        Assert.Equal((byte)255, hw.Writes.ToArray().Last(w => w.Fan == NoAutoFan).Pwm); // not left at pwm 0
        Assert.Equal(1, NoAutoModeWarnings(log));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task FanWithoutCurve_WithoutAutoMode_NeverDriven_IsNotRewrittenEveryTick()
    {
        // A dell laptop before onboarding: no curves at all. The start-up RestoreDefaults sets the channel to full
        // speed (thermally safe, needed after a SIGKILL); the "no curve" path then visits the fan every tick but must
        // not write it again (or log a failed write each time) - the loop never drove it.
        var hw = FanRig();
        hw.NoAutoMode.Add(NoAutoFan);
        var store = new FakeConfigStore
        {
            Exists = true,
            Stored = AppConfig.Empty with { OnboardingCompleted = true, Fans = new[] { new FanConfig { FanId = NoAutoFan } } },
        };
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        using var cts = new CancellationTokenSource();
        var service = WritingService(hw, store, ipc, log: log);
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 6);

        Assert.Equal((byte)255, hw.Pwm[NoAutoFan]);     // the start-up RestoreDefaults (the fake logs no write)
        Assert.Empty(hw.Writes);                        // nothing from the loop
        Assert.Equal(0, NoAutoModeWarnings(log));
        Assert.Equal("HardwareAuto", LastSnapshot(ipc)!.Fans.Single(f => f.Id == NoAutoFan).ControlState);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task CurveSwitchedOff_WithoutAutoMode_HandsBackAtFullSpeedOnce_AndWarnsOnce()
    {
        var hw = FanRig();
        hw.NoAutoMode.Add(NoAutoFan);
        hw.Values["t"] = 60;                            // 60 % → pwm 153
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
                        Id = "c", Name = "c", SourceSensorIds = new[] { "t" }, SmoothingSeconds = 0,
                        Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
                    },
                },
                Fans = new[] { new FanConfig { FanId = NoAutoFan, AssignedCurveId = "c" } },
            },
        };
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        using var cts = new CancellationTokenSource();
        var service = WritingService(hw, store, ipc, log: log);
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => hw.Writes.ToArray().Contains((NoAutoFan, (byte)153)));

        ipc.Emit(new IpcCommand(IpcCommand.SetCurveEnabled, Target: "c", Value: 0));
        await WaitUntilAsync(() => hw.Writes.ToArray().Contains((NoAutoFan, (byte)255)));
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 5);

        Assert.Equal(1, hw.Writes.ToArray().Count(w => w == (NoAutoFan, (byte)255)));
        Assert.Equal(1, NoAutoModeWarnings(log));
        Assert.Equal("HardwareAuto", LastSnapshot(ipc)!.Fans.Single(f => f.Id == NoAutoFan).ControlState);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ATransientAutoFailure_WarnsOncePerEpisode_NotPerTickOrFlip_AndAgainAfterRecovering()
    {
        static int FullSpeedWarnings(CapturingLogger<ControlLoopService> log) =>
            log.Snapshot().Count(e => e.Level == LogLevel.Warning && e.Message.Contains("Hardware-Auto nicht setzbar"));
        int Writes(FakeHardware hw, byte pwm) => hw.Writes.ToArray().Count(w => w == (NoAutoFan, pwm));

        var hw = FanRig();
        hw.Values["t"] = 60; // 60 % → pwm 153
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
                        Id = "c", Name = "c", SourceSensorIds = new[] { "t" }, SmoothingSeconds = 0,
                        Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
                    },
                },
                Fans = new[] { new FanConfig { FanId = NoAutoFan, AssignedCurveId = "c" } },
            },
        };
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        using var cts = new CancellationTokenSource();
        var service = WritingService(hw, store, ipc, log: log);
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => Writes(hw, 153) >= 1);

        // Auto fails transiently: full speed every tick (the loop retries auto), one warning.
        hw.FailAutoMode = true;
        ipc.Emit(new IpcCommand(IpcCommand.SetCurveEnabled, Target: "c", Value: 0));
        await WaitUntilAsync(() => Writes(hw, 255) >= 3);

        // Flapping between a failed write and full speed is the same episode.
        hw.FailWrites = true;
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        hw.FailWrites = false;
        int fullSpeed = Writes(hw, 255);
        await WaitUntilAsync(() => Writes(hw, 255) >= fullSpeed + 2);
        Assert.Equal(1, FullSpeedWarnings(log));

        // Auto works again, then fails again in a later hand-back: a new episode, reported again.
        hw.FailAutoMode = false;
        seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        hw.FailAutoMode = true;
        int driven = Writes(hw, 153);
        ipc.Emit(new IpcCommand(IpcCommand.SetCurveEnabled, Target: "c", Value: 1));
        await WaitUntilAsync(() => Writes(hw, 153) > driven);
        fullSpeed = Writes(hw, 255);
        ipc.Emit(new IpcCommand(IpcCommand.SetCurveEnabled, Target: "c", Value: 0));
        await WaitUntilAsync(() => Writes(hw, 255) > fullSpeed);
        Assert.Equal(2, FullSpeedWarnings(log));

        await service.StopAsync(cts.Token);
    }
}
