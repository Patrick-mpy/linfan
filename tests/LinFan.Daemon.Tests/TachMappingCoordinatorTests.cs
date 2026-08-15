// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

public class TachMappingCoordinatorTests
{
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    /// <summary>Rig: ein steuerbarer Lüfter mit Tacho (dessen RPM mit dem PWM steigt) + Temperatursensor.</summary>
    private static FakeHardware Rig(double temp = 40, Func<byte, int>? rpm = null)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", temp);
        hw.AddFanSensor("fan1", 0);
        hw.AddFan("f1", canControl: true, tachId: "fan1");
        hw.TachId = "fan1";
        hw.RpmForPwm = rpm ?? (pwm => pwm * 10);
        return hw;
    }

    private static TachMappingCoordinator New(
        FakeHardware hw, CancellationToken token,
        Action<string, string>? onMatched = null,
        Action<string>? suspend = null, Action<string>? resume = null,
        Func<double>? failSafe = null, TimeSpan? cooldown = null, Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(hw, hw, NullLogger.Instance, suspend ?? (_ => { }), resume ?? (_ => { }),
            onMatched ?? ((_, __) => { }), token,
            mappingFactory: (s, f) => new TachometerMappingService(s, f, NoDelay),
            failSafeTempC: failSafe, cooldown: cooldown, now: now, delay: delay ?? NoDelay);

    [Fact]
    public async Task Map_Matched_SetsStatus_AndPersistsOverride()
    {
        var hw = Rig();
        var matched = new List<(string Fan, string Tach)>();
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, onMatched: (f, t) => matched.Add((f, t)));

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Equal(("f1", "fan1"), Assert.Single(matched));   // eindeutiger Treffer → persistiert
        IpcTachMapping? s = co.Status;
        Assert.NotNull(s);
        Assert.Equal(TachMappingPhase.Matched, s!.Phase);
        Assert.Equal("fan1", s.MatchedTachId);
        Assert.True(s.RiseRpm > 0);
        Assert.True(hw.RestoreCount >= 1);                      // Fail-Safe nach dem Antreiben
    }

    [Fact]
    public async Task Map_NoResponse_DoesNotPersist()
    {
        var hw = Rig(rpm: _ => 0);                              // Tacho reagiert nicht
        var matched = new List<(string, string)>();
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, onMatched: (f, t) => matched.Add((f, t)));

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Empty(matched);                                  // nichts zugeordnet
        Assert.Equal(TachMappingPhase.NoResponse, co.Status!.Phase);
        Assert.True(hw.RestoreCount >= 1);
    }

    [Fact]
    public async Task Map_OverTemperature_Fails_AndRestores_WithoutPersist()
    {
        var hw = Rig(temp: 95);
        var matched = new List<(string, string)>();
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, onMatched: (f, t) => matched.Add((f, t)), failSafe: () => 90);

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Empty(matched);
        IpcTachMapping? s = co.Status;
        Assert.Equal(TachMappingPhase.Failed, s!.Phase);
        Assert.Equal(TachMappingFailReason.OverTemperature, s.FailReason);
        Assert.Equal(95.0, s.OverTempC);
        // Reported is the threshold that actually tripped: starting demands a margin below the fail-safe
        // limit (90 − StartMarginC 10), because the long window without airflow follows right after.
        Assert.Equal(80.0, s.OverLimitC);
        Assert.True(hw.RestoreCount >= 1);
    }

    /// <summary>
    /// Below the fail-safe limit but inside the start margin: the run would spend its whole measurement
    /// window (every fan near PWM 0) on its way into the watchdog - so do not start at all. The point is
    /// that no fan may have been throttled in the process.
    /// </summary>
    [Fact]
    public async Task Map_WithinStartMargin_RefusesToStart_WithoutThrottling()
    {
        var hw = Rig(temp: 85);   // < 90, but >= 90 − 10
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, failSafe: () => 90);

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Equal(TachMappingFailReason.OverTemperature, co.Status!.FailReason);
        Assert.Empty(hw.Writes);   // nothing throttled, nothing driven
    }

    [Fact]
    public async Task Map_SuspendsAndResumesAllControllable_Symmetric()
    {
        var hw = Rig();
        hw.AddFan("f2", canControl: true);   // weitere steuerbare Lüfter (werden gedrosselt)
        hw.AddFan("f3", canControl: true);
        int suspend = 0, resume = 0;
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token,
            suspend: _ => Interlocked.Increment(ref suspend),
            resume: _ => Interlocked.Increment(ref resume));

        co.Start(new FanId("f1"));
        await co.StopAsync();

        Assert.Equal(3, suspend);
        Assert.Equal(3, resume);             // finally gibt jeden Lüfter wieder frei
    }

    [Fact]
    public async Task Map_NotControllableTarget_DoesNotStart()
    {
        var hw = Rig();
        hw.AddFan("ro", canControl: false);
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token);

        co.Start(new FanId("ro"));
        await co.StopAsync();

        Assert.Empty(hw.Writes);             // nichts angetrieben
        Assert.Null(co.Status);
    }

    /// <summary>
    /// A second run started right away is <b>delayed, not dropped</b>. A silent drop made the GUI wait out
    /// its 60 s timeout: in the assistant every fan following a skipped one lost both its coupling AND its
    /// calibration.
    /// </summary>
    [Fact]
    public async Task Map_Cooldown_DelaysSecondRun_InsteadOfDroppingIt()
    {
        var hw = Rig();
        var matched = new List<(string, string)>();
        var waits = new List<TimeSpan>();
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, onMatched: (f, t) => matched.Add((f, t)),
            cooldown: TimeSpan.FromSeconds(3), now: () => clock.Now,
            delay: (d, _) => { waits.Add(d); return Task.CompletedTask; });

        co.Start(new FanId("f1"));
        await co.StopAsync();
        Assert.Single(matched);
        Assert.Empty(waits);                          // first run: no cooldown open

        // Immediately again → cooldown still open → the run waits it out and then goes ahead anyway.
        co.Start(new FanId("f1"));
        await co.StopAsync();
        Assert.Equal(2, matched.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(waits));

        // Once the cooldown has elapsed → straight away, no waiting.
        clock.Advance(TimeSpan.FromSeconds(4));
        co.Start(new FanId("f1"));
        await co.StopAsync();
        Assert.Equal(3, matched.Count);
        Assert.Single(waits);
    }

    /// <summary>No hardware may be touched while the cooldown is being waited out (fail-safe).</summary>
    [Fact]
    public async Task Map_CooldownWait_HappensBeforeThrottling()
    {
        var hw = Rig();
        var clock = new FakeClock();
        int suspendedAtWait = -1;
        using var cts = new CancellationTokenSource();
        int suspended = 0;
        var co = New(hw, cts.Token,
            suspend: _ => Interlocked.Increment(ref suspended),
            cooldown: TimeSpan.FromSeconds(3), now: () => clock.Now,
            delay: (_, __) => { suspendedAtWait = Volatile.Read(ref suspended); return Task.CompletedTask; });

        co.Start(new FanId("f1"));
        await co.StopAsync();
        int suspendedBeforeSecondRun = Volatile.Read(ref suspended);

        co.Start(new FanId("f1"));   // second run → waits out the cooldown
        await co.StopAsync();

        // Waiting happens BEFORE throttling → no further fan is suspended during the wait, so cooling keeps
        // running normally for its duration.
        Assert.Equal(suspendedBeforeSecondRun, suspendedAtWait);
    }

    /// <summary>A cancel inside the cooldown window touches no hardware (nothing throttled ⇒ nothing to restore).</summary>
    [Fact]
    public async Task Map_CanceledDuringCooldown_DoesNotTouchHardware()
    {
        var hw = Rig();
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        // The delay cancels, the way Task.Delay does on a cancelled token. The first run has no open cooldown
        // yet and therefore never calls it.
        var co = New(hw, cts.Token, cooldown: TimeSpan.FromSeconds(3), now: () => clock.Now,
            delay: (_, __) => Task.FromCanceled(new CancellationToken(canceled: true)));

        co.Start(new FanId("f1"));
        await co.StopAsync();
        int writesAfterFirst = hw.Writes.Count;
        int restoresAfterFirst = hw.RestoreCount;

        co.Start(new FanId("f1"));   // second run → cancelled while waiting out the cooldown
        await co.StopAsync();

        Assert.Equal(writesAfterFirst, hw.Writes.Count);        // no PWM write
        Assert.Equal(restoresAfterFirst, hw.RestoreCount);      // no needless RestoreDefaults
        Assert.Equal(TachMappingPhase.Failed, co.Status!.Phase);
        Assert.Equal(TachMappingFailReason.Canceled, co.Status.FailReason);
    }

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan t) => Now += t;
    }
}
