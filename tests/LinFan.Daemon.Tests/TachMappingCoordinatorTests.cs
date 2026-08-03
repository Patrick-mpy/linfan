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
        Func<double>? failSafe = null, TimeSpan? cooldown = null, Func<DateTimeOffset>? now = null) =>
        new(hw, hw, NullLogger.Instance, suspend ?? (_ => { }), resume ?? (_ => { }),
            onMatched ?? ((_, __) => { }), token,
            mappingFactory: (s, f) => new TachometerMappingService(s, f, NoDelay),
            failSafeTempC: failSafe, cooldown: cooldown, now: now);

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
        Assert.Equal(90.0, s.OverLimitC);
        Assert.True(hw.RestoreCount >= 1);
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

    [Fact]
    public async Task Map_Cooldown_RejectsImmediateSecondRun()
    {
        var hw = Rig();
        var matched = new List<(string, string)>();
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        var co = New(hw, cts.Token, onMatched: (f, t) => matched.Add((f, t)),
            cooldown: TimeSpan.FromSeconds(3), now: () => clock.Now);

        co.Start(new FanId("f1"));
        await co.StopAsync();
        Assert.Single(matched);

        // Sofort erneut → Cooldown aktiv → abgelehnt.
        co.Start(new FanId("f1"));
        await co.StopAsync();
        Assert.Single(matched);

        // Nach Ablauf des Cooldowns → wieder erlaubt.
        clock.Advance(TimeSpan.FromSeconds(4));
        co.Start(new FanId("f1"));
        await co.StopAsync();
        Assert.Equal(2, matched.Count);
    }

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan t) => Now += t;
    }
}
