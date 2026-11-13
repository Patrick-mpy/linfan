// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Everything that puts the fans on firmware auto without changing the config - a coordinator ending (identify,
/// a coupling without a match, a cancelled calibration) or the failed-tick path - must let the curves write again
/// in the next tick, although the temperature stays within the hysteresis: exactly one write per curve fan, none
/// while a fan is suspended, and no write storm afterwards. These run with real (fake) writes, not as a dry run.
/// </summary>
public partial class ControlLoopServiceTests
{
    /// <summary>The curve's value at the rig temperature: 60 °C → 60 % → 153, unlike any ramp or pulse value.</summary>
    private const byte CurvePwm = 153;

    private static FakeHardware CurveRig()
    {
        FakeHardware hw = TwoFanRig();
        hw.Values["t"] = 60;
        return hw;
    }

    private static FakeConfigStore CurveStore() => new()
    {
        Exists = true,
        Stored = AppConfig.Empty with
        {
            OnboardingCompleted = true,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c", SourceSensorIds = new[] { "t" }, HysteresisC = 2, SmoothingSeconds = 0,
                    Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
                },
            },
            Fans = new[]
            {
                new FanConfig { FanId = "A", AssignedCurveId = "c" },
                new FanConfig { FanId = "B", AssignedCurveId = "c" },
            },
        },
    };

    private static ControlLoopService WritingService(
        FakeHardware hw, FakeConfigStore store, FakeIpcServer ipc,
        Func<TimeSpan, CancellationToken, Task>? identifyDelay = null,
        Func<TimeSpan, CancellationToken, Task>? calibrationDelay = null, ILogger<ControlLoopService>? log = null) =>
        new(hw, hw, store, ipc, log ?? NullLogger<ControlLoopService>.Instance, FastTick,
            calibrationDelay is null ? null : (s, f) => new CalibrationService(s, f, calibrationDelay),
            identifyDelay ?? NoDelay,
            tachMappingFactory: (s, f) => new TachometerMappingService(s, f, NoDelay),
            dryRunOverride: false);

    private static int CurveWrites(FakeHardware hw, string fan) =>
        hw.Writes.ToArray().Count(w => w.Fan == fan && w.Pwm == CurvePwm);

    /// <summary>Waits for each curve fan's next curve write, then a few more ticks to rule out a write storm.</summary>
    private static async Task AssertEachCurveFanRewrittenOnceAsync(FakeHardware hw, FakeIpcServer ipc, int before)
    {
        await WaitUntilAsync(() => CurveWrites(hw, "A") >= before + 1 && CurveWrites(hw, "B") >= before + 1);
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 5);

        Assert.Equal(before + 1, CurveWrites(hw, "A"));
        Assert.Equal(before + 1, CurveWrites(hw, "B"));
        Assert.Equal(FanMode.Manual, hw.ModeLog["A"]); // driven by the curve again, not left on firmware auto
        Assert.Equal(FanMode.Manual, hw.ModeLog["B"]);
    }

    private static async Task<ControlLoopService> StartOnCurveAsync(
        ControlLoopService service, FakeHardware hw, FakeIpcServer ipc, CancellationToken ct)
    {
        await StartAndWaitReadyAsync(service, ipc, ct);
        await WaitUntilAsync(() => CurveWrites(hw, "A") == 1 && CurveWrites(hw, "B") == 1);
        return service;
    }

    [Fact]
    public async Task AfterIdentify_TheCurveDrivesEveryFanAgain_Once()
    {
        var hw = CurveRig();
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // holds the pulse
        using var cts = new CancellationTokenSource();
        var service = await StartOnCurveAsync(
            WritingService(hw, CurveStore(), ipc, identifyDelay: (_, ct) => gate.Task.WaitAsync(ct)), hw, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "B"));
        await WaitUntilAsync(() => hw.Writes.ToArray().Contains(("B", (byte)255)));
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        Assert.Equal(1, CurveWrites(hw, "A"));          // no curve write while suspended
        Assert.Equal(1, CurveWrites(hw, "B"));

        gate.SetResult();
        await AssertEachCurveFanRewrittenOnceAsync(hw, ipc, before: 1);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task AfterACouplingWithoutMatch_TheCurveDrivesEveryFanAgain_Once()
    {
        var hw = CurveRig();
        hw.RpmForPwm = null;                            // no tachometer responds → no match, no config change
        var ipc = new FakeIpcServer();
        using var cts = new CancellationTokenSource();
        var service = await StartOnCurveAsync(WritingService(hw, CurveStore(), ipc), hw, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.StartTachMapping, Target: "A"));
        await WaitUntilAsync(() => ipc.Broadcasts.ToArray().Any(b => b.TachMapping is { Running: false } tm
                                                                     && tm.Phase != TachMappingPhase.Matched));

        await AssertEachCurveFanRewrittenOnceAsync(hw, ipc, before: 1);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task AfterACancelledCalibration_TheCurveDrivesEveryFanAgain_Once()
    {
        // The calibration suspends only A but restores both fans when it ends.
        var hw = CurveRig();
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // holds the ramp at its first step
        using var cts = new CancellationTokenSource();
        var service = await StartOnCurveAsync(
            WritingService(hw, CurveStore(), ipc, calibrationDelay: (_, ct) => gate.Task.WaitAsync(ct)), hw, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "A"));
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s && s.Fans.Any(f => f.Id == "A" && f.ControlState == "Calibrating"));
        Assert.Equal(1, CurveWrites(hw, "A"));          // no curve write while suspended

        ipc.Emit(new IpcCommand(IpcCommand.CancelCalibration));
        await AssertEachCurveFanRewrittenOnceAsync(hw, ipc, before: 1);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task AfterAFailedTick_TheCurveDrivesEveryFanAgain_Once()
    {
        // The over-temperature fail-safe throws (its restore fails once), so the tick fails before it could reset the
        // loop; the failed-tick path then restores every fan. The next tick reads 60 °C again.
        var hw = CurveRig();
        var ipc = new FakeIpcServer();
        using var cts = new CancellationTokenSource();
        var service = await StartOnCurveAsync(WritingService(hw, CurveStore(), ipc), hw, ipc, cts.Token);

        int restored = hw.RestoreCount + 2;             // the failing one in the tick + the failed-tick path's
        hw.FailNextRestores = 1;
        hw.OnRead = () =>
        {
            if (hw.RestoreCount >= restored)
                hw.Values["t"] = 60;
        };
        hw.Values["t"] = 120;

        await WaitUntilAsync(() => hw.RestoreCount >= restored);
        await AssertEachCurveFanRewrittenOnceAsync(hw, ipc, before: 1);

        await service.StopAsync(cts.Token);
    }
}
