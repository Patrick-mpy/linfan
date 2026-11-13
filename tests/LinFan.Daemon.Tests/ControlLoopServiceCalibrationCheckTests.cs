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
/// The calibration check in the daemon: it reports in the snapshot, from what the snapshot read, and it can neither cost
/// a tick nor a snapshot.
/// </summary>
public partial class ControlLoopServiceTests
{
    private static readonly CurveConfig CheckedCurve = new()
    {
        Id = "c",
        Name = "c",
        SourceSensorIds = ["t"],
        SmoothingSeconds = 0,
        Points = [new CurvePoint(30, 50), new CurvePoint(80, 100)], // 40 °C → 60 % → PWM 153
    };

    /// <summary>
    /// <see cref="FanRig"/>'s fan on a curve, calibrated to twice the speed it turns at in the rig and measured with
    /// <paramref name="calibratedWith"/>.
    /// </summary>
    private static FakeConfigStore CheckedStore(string calibratedWith = "hwmon7/fan1")
    {
        var cal = new FanCalibration
        {
            StartPwm = 96,
            MinRpm = 2 * (300 + 96 * 4),
            MaxRpm = 2 * (300 + 224 * 4),
            Tachometer = calibratedWith,
            Samples = Enumerable.Range(0, 8)
                .Select(i => (byte)(i * 32))
                .Select(pwm => new CalibrationSample(pwm, pwm < 96 ? 0 : 2 * (300 + pwm * 4)))
                .ToList(),
        };
        return new FakeConfigStore
        {
            Exists = true,
            Stored = AppConfig.Empty with
            {
                OnboardingCompleted = true,
                Curves = [CheckedCurve],
                Fans = [new FanConfig { FanId = "hwmon7/pwm1", MinPwm = 96, AssignedCurveId = "c", Calibration = cal }],
            },
        };
    }

    private static ControlLoopService CheckedService(
        FakeHardware hw, FakeConfigStore store, FakeIpcServer ipc, CalibrationPlausibility? check,
        ILogger<ControlLoopService>? log = null, bool dryRun = false) =>
        new(hw, hw, store, ipc, log ?? NullLogger<ControlLoopService>.Instance, FastTick, dryRunOverride: dryRun,
            calibrationPlausibility: check);

    /// <summary>The check as the daemon makes it, with a tick of 5 s on a clock that moves 5 s per tick.</summary>
    private static CalibrationPlausibility FiveSecondTicks() =>
        new(TimeSpan.FromSeconds(5), time: new SteppingClock(TimeSpan.FromSeconds(5)));

    private static IpcFan CheckedFan(IpcSnapshot? snapshot) => Assert.Single(snapshot!.Fans);

    [Fact]
    public async Task Snapshot_ReportsACalibrationMeasuredWithAnotherSensor()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var service = CheckedService(hw, CheckedStore(calibratedWith: "hwmon7/fan2"), ipc, check: null,
            dryRun: true);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count > 0);

        Assert.Equal("OtherSensor", CheckedFan(ipc.Broadcasts[0]).CalibrationMismatch); // the first snapshot already
        Assert.Equal("Curve", CheckedFan(ipc.Broadcasts[0]).ControlState);             // a notice, nothing else

        await service.StopAsync(cts.Token);
    }

    /// <summary>
    /// Runs the calibrated rig for <paramref name="ticks"/> snapshots and returns the last one with the hardware reads
    /// between every two snapshots.
    /// </summary>
    private static async Task<(IpcSnapshot Last, List<(int GetPwm, int ReadValue)> PerTick)> RunCheckedAsync(int ticks)
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var reads = new List<(int, int)>();
        ipc.OnBroadcast = _ =>
        {
            lock (reads)
                reads.Add((hw.GetPwmCalls, hw.ReadValueCalls));
        };
        var service = CheckedService(hw, CheckedStore(), ipc, FiveSecondTicks());
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= ticks);
        await service.StopAsync(cts.Token);

        List<(int GetPwm, int ReadValue)> perTick;
        lock (reads)
            perTick = reads.Zip(reads.Skip(1), (a, b) => (b.Item1 - a.Item1, b.Item2 - a.Item2)).Take(ticks - 1).ToList();
        return (ipc.Broadcasts[ticks - 1], perTick);
    }

    /// <summary>
    /// The hardware reads of one tick before the check existed: the loop's tick and the snapshot of it, on the same rig
    /// and config, once both have settled.
    /// </summary>
    private static (int GetPwm, int ReadValue) ReadsOfATickAndItsSnapshot()
    {
        var hw = FanRig();
        AppConfig config = ProfileService.EnsureProfiles(CheckedStore().Stored);
        var loop = new ControlLoop(hw, hw);
        (int GetPwm, int ReadValue) before = default, perTick = default;
        for (int i = 0; i < 4; i++)
        {
            ControlTick tick = loop.Tick(config);
            SnapshotBuilder.Build(hw, hw, DaemonStatus.Active, dryRun: false, tick, config, ConfigMapper.ToIpc(config),
                configRevision: 1, new HashSet<string>(), calibration: null);
            perTick = (hw.GetPwmCalls - before.GetPwm, hw.ReadValueCalls - before.ReadValue);
            before = (hw.GetPwmCalls, hw.ReadValueCalls);
        }
        return perTick;
    }

    [Fact]
    public async Task Snapshot_ReportsAFanSlowerThanItsCalibration_WithoutReadingMoreHardware()
    {
        // 5 s per tick: settled after 10 s, a mismatch for 60 s - reported with the 15th snapshot.
        (IpcSnapshot last, List<(int, int)> perTick) = await RunCheckedAsync(ticks: 20);

        IpcFan fan = CheckedFan(last);
        Assert.Equal(("Slower", "Curve", 153), (fan.CalibrationMismatch, fan.ControlState, fan.Pwm)); // driven as before
        (int, int) withoutTheCheck = ReadsOfATickAndItsSnapshot();
        Assert.All(perTick.Skip(2), reads => Assert.Equal(withoutTheCheck, reads));
    }

    private sealed class FailingClock : TimeProvider
    {
        public override long GetTimestamp() => throw new InvalidOperationException("clock failed (simulated)");
    }

    [Fact]
    public async Task Snapshot_WhenTheCalibrationCheckThrows_IsStillBroadcast_AndTheTickRuns()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = CheckedService(hw, CheckedStore(calibratedWith: "hwmon7/fan2"), ipc,
            new CalibrationPlausibility(TimeSpan.FromSeconds(1), time: new FailingClock()), log);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 5);
        await service.StopAsync(cts.Token);

        Assert.Contains(("hwmon7/pwm1", (byte)153), hw.Writes);          // the curve drives the fan
        Assert.All(ipc.Broadcasts, b =>
        {
            Assert.Equal("None", b.FailSafe);                            // no tick failed
            Assert.Equal("Curve", CheckedFan(b).ControlState);
            Assert.Null(CheckedFan(b).CalibrationMismatch);              // without the findings, not without the snapshot
        });
        Assert.Single(log.Snapshot(), e => e.Message.StartsWith("Kalibrierung nicht mit der Drehzahl abgleichbar", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Snapshot(), e => e.Message.StartsWith("Snapshot-Broadcast fehlgeschlagen", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Snapshot(), e => e.Message.StartsWith("Tick fehlgeschlagen", StringComparison.Ordinal));
    }
}
