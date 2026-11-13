// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Follow-ups from the run-2 fail-safe audit and log-forging finding: what a peer's strings may reach, what a failing
/// tick tells the GUI and systemd, that a disconnect clears manual control however the commands raced, and that a dry
/// run never acts on a fan.
/// </summary>
public partial class ControlLoopServiceTests
{
    /// <summary>
    /// The discovery the daemon reads at start for its diagnostics and the tach assignment runs before IPC is up: a
    /// backend that throws there must not end the daemon - the loop and its watchdog still run.
    /// </summary>
    [Fact]
    public async Task Start_WithADiscoveryThatThrows_KeepsRunning_AndTheWatchdogStillTrips()
    {
        var hw = FanRig();
        hw.FailDiscovery = true;
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = NewService(hw, new FakeConfigStore(), ipc, log: log);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token); // handlers set: the start-up discovery has run
        hw.FailDiscovery = false;
        hw.Values["t"] = 99;                                   // over the default limit

        await WaitUntilAsync(() => LastSnapshot(ipc)?.FailSafe == nameof(FailSafeState.OverTemperature));
        await service.StopAsync(cts.Token);
        Assert.Contains(log.Snapshot(), e => e.Message.StartsWith("Prüfung der Kurven-Quellen fehlgeschlagen", StringComparison.Ordinal));
        Assert.Contains(log.Snapshot(), e => e.Message.StartsWith("Discovery nicht lesbar", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hwmon7/pwm1\n2026-01-01 00:00:00.000 [CRT] Forged: x")]
    [InlineData("hwmon7/pwm1\u0000<0>emerg")]
    [InlineData("hwmon7/pwm1\u001b]0;title\u0007")]
    public async Task OnCommand_WithControlCharacters_IsRejected_BeforeAnythingLogsIt(string target)
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = NewService(hw, new FakeConfigStore(), ipc, log: log);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: target, Value: 10));
        ipc.Emit(new IpcCommand(IpcCommand.SetFanAuto, Target: target));
        ipc.Emit(new IpcCommand(target));                                 // as the command name, too
        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 200));
        await WaitUntilAsync(() => FanIsManual(ipc, "hwmon7/pwm1"));     // the next good command still works

        await service.StopAsync(cts.Token);
        Assert.DoesNotContain(hw.Writes.ToArray(), w => w.Pwm == 10);
        Assert.Equal(3, log.Snapshot().Count(e => e.Message.StartsWith("IPC-Kommando verworfen", StringComparison.Ordinal)));
        Assert.All(log.Snapshot(), e => Assert.True(UntrustedText.IsClean(e.Message), e.Message));
    }

    [Fact]
    public async Task OnCommand_OverlongTarget_IsRejected()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = NewService(hw, new FakeConfigStore(), ipc, log: log);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.SetFanAuto, Target: new string('x', ConfigLimits.MaxTextLength + 1)));

        await service.StopAsync(cts.Token);
        Assert.Contains(log.Snapshot(), e => e.Message.StartsWith("IPC-Kommando verworfen", StringComparison.Ordinal));
    }

    /// <summary>
    /// The disconnect event clears manual control, but a command still buffered from the client that just went can
    /// arrive after it. With no client left, each tick clears it again - no event needed.
    /// </summary>
    [Fact]
    public async Task ManualOverride_WithNoClientConnected_IsClearedByTheNextTick()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, new FakeConfigStore(), ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.SetClientCountSilently(0);
        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 0)); // raced the disconnect
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);

        Assert.False(FanIsManual(ipc, "hwmon7/pwm1"));
        await service.StopAsync(cts.Token);
    }

    /// <summary>
    /// A tick that throws used to send no snapshot: the GUI kept showing the last good tick. Now it reports the error
    /// fail-safe, and systemd stops hearing from the watchdog until a tick runs again.
    /// </summary>
    [Fact]
    public async Task FailingTick_ReportsTheErrorFailSafe_AndStopsTheSystemdWatchdogPing()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var systemd = new FakeSystemdNotifier();
        var service = new ControlLoopService(hw, hw, new FakeConfigStore(), ipc, NullLogger<ControlLoopService>.Instance,
            FastTick, dryRunOverride: false, systemd: systemd);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => systemd.WatchdogPings >= 3);

        hw.ThrowOnRestore = true;
        hw.Values["t"] = 120; // every tick now throws from the over-temperature fail-safe
        await WaitUntilAsync(() => LastSnapshot(ipc)?.FailSafe == nameof(FailSafeState.Error));
        Assert.Equal(nameof(FanControlState.FailSafe), LastSnapshot(ipc)!.Fans.Single(f => f.Id == "hwmon7/pwm1").ControlState);
        int pings = systemd.WatchdogPings;
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        Assert.Equal(pings, systemd.WatchdogPings);

        hw.Values["t"] = 40;
        hw.ThrowOnRestore = false;
        await WaitUntilAsync(() => systemd.WatchdogPings > pings && LastSnapshot(ipc)?.FailSafe == nameof(FailSafeState.None));
        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task DryRun_RejectsDeviceActions_AndNeverRestores()
    {
        var hw = FanRig();
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = NewService(hw, new FakeConfigStore(), ipc, fastCalibration: true, identifyDelay: NoDelay,
            log: log, fastTachMapping: true, dryRun: true);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "hwmon7/pwm1"));
        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "hwmon7/pwm1"));
        ipc.Emit(new IpcCommand(IpcCommand.StartTachMapping, Target: "hwmon7/pwm1"));
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        await service.StopAsync(cts.Token);

        Assert.Empty(hw.Writes.ToArray());
        Assert.Equal(0, hw.RestoreCount);   // neither at start nor at stop
        Assert.All(ipc.Broadcasts, b => Assert.Null(b.Calibration));
        Assert.Equal(3, log.Snapshot().Count(e => e.Message.Contains("abgelehnt: Dry-Run", StringComparison.Ordinal)));
    }

    /// <summary>A fan whose writes keep failing (e.g. its hardware is gone) is reported once, not every tick.</summary>
    [Fact]
    public async Task FailedWrite_IsWarnedOncePerFan_UntilItRecovers()
    {
        var hw = FanRig();
        hw.Values["t"] = 60;
        hw.FailWrites = true;
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
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
                Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", AssignedCurveId = "c" } },
            },
        };
        var service = NewService(hw, store, ipc, log: log);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        int Warnings() => log.Snapshot().Count(e => e.Level == LogLevel.Warning && e.Message.Contains("Schreiben fehlgeschlagen"));

        await WaitUntilAsync(() => ipc.Broadcasts.Count(b => ControlOf(b, "hwmon7/pwm1") == nameof(FanControlState.Failed)) >= 5);
        Assert.Equal(1, Warnings());

        hw.FailWrites = false;   // recovers: the curve writes again
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s && ControlOf(s, "hwmon7/pwm1") == nameof(FanControlState.Curve));
        hw.FailWrites = true;
        hw.Values["t"] = 70;     // past the hysteresis, so the curve writes - and fails - again
        int seen = ipc.Broadcasts.Count(b => ControlOf(b, "hwmon7/pwm1") == nameof(FanControlState.Failed));
        await WaitUntilAsync(() => ipc.Broadcasts.Count(b => ControlOf(b, "hwmon7/pwm1") == nameof(FanControlState.Failed)) >= seen + 5);
        await service.StopAsync(cts.Token);

        Assert.Equal(2, Warnings());
    }

    /// <summary>
    /// A write wedged in the driver (here: on another fan, as from a calibration ramp) holds the hardware gate. The
    /// loop's writes time out, so its curve could not raise the fan any more: every fan goes to hardware auto past the
    /// gate, the snapshot says why, and control resumes once the gate is free.
    /// </summary>
    [Fact]
    public async Task WedgedHardwareWrite_PutsEveryFanOnHardwareAuto_UntilTheGateIsFree()
    {
        var hw = FanRig();
        hw.Values["t"] = 60;
        hw.AddFan("hwmon7/pwm2");
        var wedge = new WedgeOnSetPwm(hw, "hwmon7/pwm2");
        var fans = new SynchronizedFanController(wedge, gateTimeout: TimeSpan.FromMilliseconds(50));
        var ipc = new FakeIpcServer();
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
                Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", AssignedCurveId = "c" } },
            },
        };
        using var service = new ControlLoopService(hw, fans, store, ipc, NullLogger<ControlLoopService>.Instance,
            FastTick, dryRunOverride: false);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s && ControlOf(s, "hwmon7/pwm1") == nameof(FanControlState.Curve));

        var wedged = new Thread(() => fans.SetPwm(new FanId("hwmon7/pwm2"), 1)) { IsBackground = true };
        wedged.Start();
        Assert.True(wedge.Entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            hw.Values["t"] = 75; // the curve wants more - and cannot write it
            int restores = hw.RestoreCount;
            await WaitUntilAsync(() => LastSnapshot(ipc)?.FailSafe == nameof(FailSafeState.Error) && hw.RestoreCount > restores,
                timeoutMs: 5000);
            Assert.Equal(FanMode.Auto, hw.ModeLog["hwmon7/pwm1"]);
        }
        finally
        {
            wedge.Release.Set();
            wedged.Join(TimeSpan.FromSeconds(5));
        }

        await WaitUntilAsync(() => LastSnapshot(ipc) is { } s && s.FailSafe == nameof(FailSafeState.None)
            && ControlOf(s, "hwmon7/pwm1") == nameof(FanControlState.Curve), timeoutMs: 5000);
        await service.StopAsync(cts.Token);
    }

    /// <summary>Blocks <see cref="SetPwm"/> for one fan until released, like a write stuck in the EC or driver.</summary>
    private sealed class WedgeOnSetPwm(FakeHardware inner, string fanId) : IFanController
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public void SetPwm(FanId id, byte value)
        {
            if (id.Value == fanId)
            {
                Entered.Set();
                Release.Wait();
                return;
            }
            inner.SetPwm(id, value);
        }

        public IReadOnlyList<FanDescriptor> DiscoverFans() => inner.DiscoverFans();
        public bool CanControl(FanId id) => inner.CanControl(id);
        public FanMode GetMode(FanId id) => inner.GetMode(id);
        public byte GetPwm(FanId id) => inner.GetPwm(id);
        public void SetMode(FanId id, FanMode mode) => inner.SetMode(id, mode);
        public void RestoreDefaults() => inner.RestoreDefaults();
        public void Dispose() { }
    }

    private sealed class FakeSystemdNotifier : ISystemdNotifier
    {
        private int _watchdogPings;

        public int WatchdogPings => Volatile.Read(ref _watchdogPings);

        public bool IsEnabled => true;

        public void Notify(ServiceState state)
        {
            if (state.ToString() == "WATCHDOG=1")
                Interlocked.Increment(ref _watchdogPings);
        }
    }
}
