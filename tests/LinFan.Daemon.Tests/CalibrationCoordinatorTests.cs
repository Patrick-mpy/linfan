// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

public class CalibrationCoordinatorTests
{
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    /// <summary>Standard-Rig: ein steuerbarer Lüfter mit Tacho, der ab pwm=96 anläuft.</summary>
    private static FakeHardware FanRig(double temp = 40, Func<byte, int>? rpm = null, bool canControl = true)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", temp);
        hw.AddFanSensor("hwmon7/fan1", 0);
        hw.AddFan("hwmon7/pwm1", canControl, tachId: "hwmon7/fan1");
        hw.TachId = "hwmon7/fan1";
        hw.RpmForPwm = rpm ?? (pwm => pwm < 96 ? 0 : 300 + pwm * 4);
        return hw;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Bedingung nicht innerhalb des Timeouts erfüllt.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static CalibrationCoordinator NewCoordinator(
        FakeHardware hw, CancellationToken hostToken,
        Action<string>? onSuspend = null, Action<string>? onResume = null,
        Action<string, FanCalibration>? onResult = null) =>
        new(
            hw, hw, NullLogger.Instance,
            onSuspend ?? (_ => { }),
            onResume ?? (_ => { }),
            onResult ?? ((_, _) => { }),
            hostToken,
            (s, f) => new CalibrationService(s, f, NoDelay)); // Null-Delay → schnelle Tests

    [Fact]
    public void Start_SetsStatusRunning()
    {
        var hw = FanRig();
        using var cts = new CancellationTokenSource();
        var coordinator = NewCoordinator(hw, cts.Token);

        coordinator.Start(new FanId("hwmon7/pwm1"));

        // Status ist synchron in Start gesetzt (vor dem ersten await im RunAsync).
        Assert.NotNull(coordinator.Status);
        Assert.Equal("hwmon7/pwm1", coordinator.Status!.FanId);
        Assert.True(coordinator.Status.Running || coordinator.Status.Done); // evtl. schon fertig
    }

    [Fact]
    public async Task Calibration_Completes_DeliversResult_AndStatusDone()
    {
        var hw = FanRig();
        FanCalibration? delivered = null;
        using var cts = new CancellationTokenSource();
        var coordinator = NewCoordinator(hw, cts.Token, onResult: (_, c) => delivered = c);

        coordinator.Start(new FanId("hwmon7/pwm1"));
        await coordinator.StopAsync(); // wartet auf das Ende der Rampe

        Assert.NotNull(delivered);
        Assert.NotNull(coordinator.Status);
        Assert.True(coordinator.Status!.Done || !coordinator.Status.Running);
    }

    [Fact]
    public async Task Cancel_AbortsRunning_LeavesNonRunningStatus()
    {
        // Langsame Rampe (echtes, aber kurzes Delay), damit der Cancel mitten hineinfällt.
        var hw = FanRig();
        using var cts = new CancellationTokenSource();
        var coordinator = new CalibrationCoordinator(
            hw, hw, NullLogger.Instance, _ => { }, _ => { }, (_, _) => { }, cts.Token,
            (s, f) => new CalibrationService(s, f, (d, ct) => Task.Delay(50, ct)));

        coordinator.Start(new FanId("hwmon7/pwm1"));
        coordinator.Cancel();
        await coordinator.StopAsync();

        // Nach Abbruch ist der Status nicht mehr "Running" (Abgebrochen / fertig).
        Assert.True(coordinator.Status is null || !coordinator.Status.Running);
    }

    [Fact]
    public async Task Cancel_AfterCompletion_AcknowledgesStatus_ToNull()
    {
        var hw = FanRig();
        using var cts = new CancellationTokenSource();
        var coordinator = NewCoordinator(hw, cts.Token);

        coordinator.Start(new FanId("hwmon7/pwm1"));
        await coordinator.StopAsync();           // Rampe ist fertig, _cts == null
        await WaitUntilAsync(() => coordinator.Status is not null);

        coordinator.Cancel();                    // quittiert den Abschluss-Status

        Assert.Null(coordinator.Status);
    }

    [Fact]
    public async Task StopAsync_WaitsForCalibrationToFinish()
    {
        var hw = FanRig();
        bool resultDelivered = false;
        using var cts = new CancellationTokenSource();
        var coordinator = new CalibrationCoordinator(
            hw, hw, NullLogger.Instance, _ => { }, _ => { }, (_, _) => resultDelivered = true, cts.Token,
            (s, f) => new CalibrationService(s, f, (d, ct) => Task.Delay(20, ct)));

        coordinator.Start(new FanId("hwmon7/pwm1"));
        await coordinator.StopAsync(); // muss blockieren, bis die laufende Task wirklich endet

        // Egal ob abgeschlossen oder abgebrochen — nach StopAsync läuft nichts mehr und der
        // Lüfter ist wieder freigegeben (kein Status mit Running == true).
        Assert.True(coordinator.Status is null || !coordinator.Status.Running);
        Assert.True(resultDelivered || coordinator.Status is { Running: false });
    }

    [Fact]
    public async Task SuspendAndResume_AreCalledSymmetrically()
    {
        var hw = FanRig();
        int suspend = 0, resume = 0;
        using var cts = new CancellationTokenSource();
        var coordinator = NewCoordinator(
            hw, cts.Token,
            onSuspend: _ => Interlocked.Increment(ref suspend),
            onResume: _ => Interlocked.Increment(ref resume));

        coordinator.Start(new FanId("hwmon7/pwm1"));
        await coordinator.StopAsync();
        await WaitUntilAsync(() => Volatile.Read(ref resume) == 1);

        Assert.Equal(1, suspend);
        Assert.Equal(1, resume); // finally im RunAsync gibt den Lüfter immer wieder frei
    }

    [Fact]
    public async Task Calibration_AbortsWhenTempExceedsConfiguredLimit()
    {
        // 60 °C liegt unter der Vorgabe (90), aber über dem konfigurierten Limit (50) → Watchdog bricht ab.
        var hw = FanRig(temp: 60);
        FanCalibration? delivered = null;
        using var cts = new CancellationTokenSource();
        var coordinator = new CalibrationCoordinator(
            hw, hw, NullLogger.Instance, _ => { }, _ => { }, (_, c) => delivered = c, cts.Token,
            (s, f) => new CalibrationService(s, f, NoDelay),
            failSafeTempC: () => 50);

        coordinator.Start(new FanId("hwmon7/pwm1"));
        await coordinator.StopAsync();

        Assert.Null(delivered);                       // kein Ergebnis — Rampe wurde gestoppt
        Assert.NotNull(coordinator.Status);
        Assert.False(coordinator.Status!.Running);
        Assert.Equal(CalibrationFailReason.OverTemperature, coordinator.Status.FailReason); // Über-Temperatur als Grund
    }

    [Fact]
    public async Task Calibration_WithoutConfiguredLimit_UsesDefault_AndCompletesBelow90()
    {
        // Ohne durchgereichtes Limit gilt die Vorgabe (90 °C) → 60 °C läuft sauber durch.
        var hw = FanRig(temp: 60);
        FanCalibration? delivered = null;
        using var cts = new CancellationTokenSource();
        var coordinator = NewCoordinator(hw, cts.Token, onResult: (_, c) => delivered = c);

        coordinator.Start(new FanId("hwmon7/pwm1"));
        await coordinator.StopAsync();

        Assert.NotNull(delivered);
    }

    [Fact]
    public async Task Calibration_TacholessFan_LogsInfo_WithoutWarningOrStacktrace()
    {
        // Steuerbarer Lüfter OHNE Tacho → CalibrationService wirft NotSupportedException.
        // „Kein Tacho" ist ein Normalzustand: saubere Info, kein warn + Stacktrace.
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 40);
        hw.AddFan("hwmon7/pwm1", canControl: true, tachId: null);
        var log = new CapturingLogger();
        using var cts = new CancellationTokenSource();
        var coordinator = new CalibrationCoordinator(
            hw, hw, log, _ => { }, _ => { }, (_, _) => { }, cts.Token,
            (s, f) => new CalibrationService(s, f, NoDelay));

        coordinator.Start(new FanId("hwmon7/pwm1"));
        await coordinator.StopAsync();

        Assert.NotNull(coordinator.Status);
        Assert.False(coordinator.Status!.Running);
        Assert.Equal(CalibrationFailReason.NoTacho, coordinator.Status.FailReason);  // kein Tacho als Grund gemeldet
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);  // kein warn
        Assert.DoesNotContain(log.Entries, e => e.Exception is not null);      // kein Exception-Trace
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information);     // saubere Info
    }

    [Fact]
    public async Task SuspendAndResume_Symmetric_EvenOnCancel()
    {
        var hw = FanRig();
        int suspend = 0, resume = 0;
        using var cts = new CancellationTokenSource();
        var coordinator = new CalibrationCoordinator(
            hw, hw, NullLogger.Instance,
            _ => Interlocked.Increment(ref suspend),
            _ => Interlocked.Increment(ref resume),
            (_, _) => { }, cts.Token,
            (s, f) => new CalibrationService(s, f, (d, ct) => Task.Delay(50, ct)));

        coordinator.Start(new FanId("hwmon7/pwm1"));
        coordinator.Cancel();
        await coordinator.StopAsync();

        Assert.Equal(1, suspend);
        Assert.Equal(1, resume); // auch bei Abbruch genau einmal freigegeben
    }

    /// <summary>Erfasst Level + Exception je Log-Eintrag, um die Log-Qualität zu prüfen (kein warn/Stacktrace).</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
