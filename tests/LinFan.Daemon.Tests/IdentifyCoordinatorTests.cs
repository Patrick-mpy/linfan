// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using LinFan.Core.Models;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

public class IdentifyCoordinatorTests
{
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;
    private static readonly TimeSpan ShortHold = TimeSpan.FromMilliseconds(250);

    /// <summary>Rig mit drei steuerbaren Lüftern und einem Temperatursensor.</summary>
    private static FakeHardware Rig(double temp = 40)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", temp);
        hw.AddFan("f1", canControl: true);
        hw.AddFan("f2", canControl: true);
        hw.AddFan("f3", canControl: true);
        return hw;
    }

    private static IdentifyCoordinator New(
        FakeHardware hw, CancellationToken token,
        Action<string>? suspend = null, Action<string>? resume = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? failSafe = null,
        TimeSpan? hold = null, TimeSpan? cooldown = null, Func<DateTimeOffset>? now = null) =>
        new(hw, hw, NullLogger.Instance, suspend ?? (_ => { }), resume ?? (_ => { }), token,
            delay ?? NoDelay, failSafe, hold ?? ShortHold, cooldown, now);

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Bedingung nicht innerhalb des Timeouts erfüllt.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Identify_DrivesTargetTo255_AndThrottlesOthersTo0()
    {
        var hw = Rig();
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token);

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Contains(("f1", (byte)255), hw.Writes);
        Assert.Contains(("f2", (byte)0), hw.Writes);
        Assert.Contains(("f3", (byte)0), hw.Writes);
        Assert.True(hw.RestoreCount >= 1); // finally → alle auf Hardware-Auto
        Assert.Null(co.Status);            // Erfolg → kein Status mehr
    }

    [Fact]
    public async Task Identify_LeavesReadOnlyFansUntouched()
    {
        var hw = Rig();
        hw.AddFan("ro", canControl: false);
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token);

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.DoesNotContain(hw.Writes, w => w.Fan == "ro");
    }

    [Fact]
    public async Task Identify_TargetNotControllable_DoesNothing()
    {
        var hw = Rig();
        hw.AddFan("ro", canControl: false);
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token);

        co.Start(new FanId("ro"));
        await co.StopAsync();

        Assert.Empty(hw.Writes);
        Assert.Null(co.Status);
    }

    [Fact]
    public async Task Identify_SuspendsAndResumesAllControllableFans()
    {
        var hw = Rig();
        int suspend = 0, resume = 0;
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token,
            suspend: _ => Interlocked.Increment(ref suspend),
            resume: _ => Interlocked.Increment(ref resume));

        co.Start(new FanId("f1"));
        await co.StopAsync();
        await WaitUntilAsync(() => Volatile.Read(ref resume) == 3);

        Assert.Equal(3, suspend);
        Assert.Equal(3, resume); // finally gibt jeden Lüfter wieder frei
    }

    [Fact]
    public async Task Identify_OverTemp_Aborts_WithoutThrottling_AndRestores()
    {
        // 95 °C ≥ Limit 90 → der Watchdog bricht VOR dem Drosseln ab (keine Kühlungs-Reduktion).
        var hw = Rig(temp: 95);
        int suspend = 0, resume = 0;
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token,
            suspend: _ => Interlocked.Increment(ref suspend),
            resume: _ => Interlocked.Increment(ref resume),
            failSafe: () => 90);

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Empty(hw.Writes);              // pre-Guard wirft, bevor irgendein Lüfter getrieben wird
        Assert.True(hw.RestoreCount >= 1);    // finally → sicherer Zustand
        Assert.Equal(3, suspend);
        Assert.Equal(3, resume);              // auch bei Abbruch alle freigegeben
        Assert.NotNull(co.Status);
        Assert.False(co.Status!.Running);
        Assert.Equal(IdentifyFailReason.OverTemperature, co.Status.FailReason);
        Assert.Equal(95.0, co.Status.OverTempC);
        Assert.Equal(90.0, co.Status.OverLimitC);
    }

    [Fact]
    public async Task Identify_OneSensorThrows_ButAnotherReadable_CompletesNormally()
    {
        // Ein Kanal wirft EIO, ein zweiter ist lesbar (kühl). Der defensive Watchdog überspringt den
        // kaputten Kanal und überwacht mit dem Rest weiter - KEIN Spurious-Abort (vormals riss der
        // werfende Read den Lauf ab).
        var hw = new FakeHardware();
        hw.AddThrowingTempSensor("bad");
        hw.AddTempSensor("good", 40);
        hw.AddFan("f1"); hw.AddFan("f2"); hw.AddFan("f3");
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, failSafe: () => 90);

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Contains(("f1", (byte)255), hw.Writes);
        Assert.Contains(("f2", (byte)0), hw.Writes);
        Assert.True(hw.RestoreCount >= 1);
        Assert.Null(co.Status); // Erfolg → kein Fehlerstatus trotz kaputtem Kanal
    }

    [Fact]
    public async Task Identify_ThrowingSensorDoesNotMaskOverTemp()
    {
        // Defensiv heißt nicht blind: ein lesbarer heißer Sensor löst weiter den Übertemp-Abbruch aus,
        // auch wenn ein anderer Kanal EIO wirft.
        var hw = new FakeHardware();
        hw.AddThrowingTempSensor("bad");
        hw.AddTempSensor("hot", 95);
        hw.AddFan("f1"); hw.AddFan("f2"); hw.AddFan("f3");
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, failSafe: () => 90);

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Empty(hw.Writes);           // pre-Guard bricht vor dem Drosseln ab
        Assert.True(hw.RestoreCount >= 1);
        Assert.NotNull(co.Status);
        Assert.Equal(IdentifyFailReason.OverTemperature, co.Status!.FailReason);
        Assert.Equal(95.0, co.Status.OverTempC);
    }

    [Fact]
    public async Task Identify_AllSensorsThrow_AbortsWithNoTemperatureReading()
    {
        // Kein lesbarer Sensor (alle werfen EIO) → NaN je Guard → nach MaxBlindGuards Abbruch „kein
        // Watchdog", und der finally-Pfad stellt sicher wieder her.
        var hw = new FakeHardware();
        hw.AddThrowingTempSensor("bad");
        hw.AddFan("f1"); hw.AddFan("f2"); hw.AddFan("f3");
        int resume = 0;
        using var cts = new CancellationTokenSource();
        // Hold lang genug, dass der Watchdog MaxBlindGuards Prüfpunkte erreicht (NoDelay → keine Realzeit).
        var co = New(hw, cts.Token, resume: _ => Interlocked.Increment(ref resume),
            failSafe: () => 90, hold: TimeSpan.FromSeconds(1));

        co.Start(new FanId("f1"));
        await co.StopAsync();
        await WaitUntilAsync(() => Volatile.Read(ref resume) == 3);

        Assert.True(hw.RestoreCount >= 1);
        Assert.NotNull(co.Status);
        Assert.False(co.Status!.Running);
        Assert.Equal(IdentifyFailReason.NoTemperatureReading, co.Status.FailReason);
        Assert.Equal(3, resume); // auch bei Abbruch jeder Lüfter freigegeben
    }

    [Fact]
    public async Task Start_WhileRunning_IsIgnored()
    {
        var hw = Rig();
        var gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, delay: (_, ct) => gate.Task.WaitAsync(ct));

        co.Start(new FanId("f1"));                  // läuft, blockiert im Hold am gate
        await WaitUntilAsync(() => co.IsRunning);
        co.Start(new FanId("f2"));                  // zweiter Start wird ignoriert (es läuft schon einer)

        gate.SetResult();
        await co.StopAsync();

        Assert.Contains(("f1", (byte)255), hw.Writes);        // f1 ist das Ziel
        Assert.DoesNotContain(("f2", (byte)255), hw.Writes);  // f2 wurde NIE Ziel (nur als anderer gedrosselt)
    }

    [Fact]
    public async Task Start_WithinCooldownAfterRun_IsRejected_ThenAllowedAfter()
    {
        // Fail-Safe: Identifikation drosselt ALLE anderen Lüfter auf PWM 0. Aufeinanderfolgende Läufe
        // ohne Pause könnten die Kühlung dauerhaft reduzieren - daher nach jedem Lauf eine Abklingzeit.
        var hw = Rig();
        using var cts = new CancellationTokenSource();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var co = New(hw, cts.Token, cooldown: TimeSpan.FromSeconds(3), now: () => clock.Now);

        co.Start(new FanId("f1"));
        await co.StopAsync();                                   // Lauf endet bei t=0
        Assert.Contains(("f1", (byte)255), hw.Writes);
        int writesAfterFirst = hw.Writes.Count;

        // t=1s (< 3s Cooldown) → zweiter Start abgelehnt: keine neuen Writes, andere nicht erneut gedrosselt.
        clock.Advance(TimeSpan.FromSeconds(1));
        co.Start(new FanId("f2"));
        await co.StopAsync();
        Assert.Equal(writesAfterFirst, hw.Writes.Count);
        Assert.DoesNotContain(("f2", (byte)255), hw.Writes);

        // t=4s (> 3s seit Lauf-Ende) → erlaubt.
        clock.Advance(TimeSpan.FromSeconds(3));
        co.Start(new FanId("f2"));
        await co.StopAsync();
        Assert.Contains(("f2", (byte)255), hw.Writes);
    }

    /// <summary>Von Hand gestellte Uhr für den Cooldown-Test (deterministisch, ohne echte Wartezeit).</summary>
    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; private set; }
        public MutableClock(DateTimeOffset start) => Now = start;
        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public async Task Cancel_ResumesAllFans()
    {
        var hw = Rig();
        int suspend = 0, resume = 0;
        var gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token,
            suspend: _ => Interlocked.Increment(ref suspend),
            resume: _ => Interlocked.Increment(ref resume),
            delay: (_, ct) => gate.Task.WaitAsync(ct));

        co.Start(new FanId("f1"));
        await WaitUntilAsync(() => co.IsRunning);
        co.Cancel();
        await co.StopAsync();

        Assert.Equal(3, suspend);
        Assert.Equal(3, resume);            // auch bei Abbruch genau einmal je Lüfter freigegeben
        Assert.True(hw.RestoreCount >= 1);
    }
}
