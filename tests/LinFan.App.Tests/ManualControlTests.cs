// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert die geteilte temporäre Manuell-Steuerung (Geräte-Tab, Onboarding, Positions-Modal): Engagieren sendet,
/// Slider-Änderungen senden, Verlassen (<see cref="ManualControl.Revert"/>) stellt auf Auto zurück, read-only blockt.
/// </summary>
public sealed class ManualControlTests
{
    private static ManualControl Make(List<byte> sent, List<string> auto, bool canControl = true) =>
        new("f", canControl,
            sendManual: (_, p) => { sent.Add(p); return Task.CompletedTask; },
            sendAuto: id => { auto.Add(id); return Task.CompletedTask; })
        { Throttle = TimeSpan.Zero };

    [Fact]
    public async Task Engage_SendsCurrentPercentAsManualPwm()
    {
        var sent = new List<byte>();
        var mc = Make(sent, new());

        mc.Percent = 100;   // noch nicht aktiv → kein Send
        mc.IsActive = true; // engagieren → aktuellen Stellwert senden
        await mc.PumpCompletion;

        Assert.Equal(new[] { PwmScale.ToPwm(100) }, sent);
    }

    /// <summary>
    /// Engaging starts from the power the fan runs at, not from the last (initially 0 %) setting - otherwise the fan
    /// would stop or rev by itself the moment manual control begins.
    /// </summary>
    [Fact]
    public async Task Engage_StartsAtTheLivePower_WhenKnown()
    {
        var sent = new List<byte>();
        var mc = Make(sent, new());
        mc.SetLivePwm(153); // 60 %

        mc.ModeIndex = 1;
        await mc.PumpCompletion;

        Assert.True(mc.IsActive);
        Assert.Equal(60, mc.Percent);
        Assert.All(sent, p => Assert.Equal(PwmScale.ToPwm(60), p));
        Assert.NotEmpty(sent);
    }

    /// <summary>Without a known live power engaging starts at full speed - the safe side - not at the old 0 %.</summary>
    [Fact]
    public async Task Engage_WithoutKnownPower_StartsAtFullSpeed()
    {
        var sent = new List<byte>();
        var mc = Make(sent, new());
        Assert.Equal("-", mc.ShownPercentText);

        mc.IsActive = true;
        await mc.PumpCompletion;

        Assert.Equal(100, mc.Percent);
        Assert.Equal((byte)255, Assert.Single(sent.Distinct()));
        Assert.Equal("100 %", mc.ShownPercentText);
    }

    /// <summary>While the row is busy or offline manual control cannot start; letting go always works.</summary>
    [Fact]
    public void EngageBlocked_RefusesToStart_ButNeverToStop()
    {
        var auto = new List<string>();
        var mc = Make(new(), auto);
        var changed = new List<string>();
        mc.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        mc.EngageBlocked = true;
        mc.ModeIndex = 1;
        Assert.False(mc.IsActive);
        Assert.Contains(nameof(ManualControl.ModeIndex), changed); // the switch is told to jump back

        mc.EngageBlocked = false;
        mc.ModeIndex = 1;
        Assert.True(mc.IsActive);

        mc.EngageBlocked = true;
        mc.ModeIndex = 0;
        Assert.False(mc.IsActive);
        Assert.Equal(new[] { "f" }, auto);
    }

    [Fact]
    public void ShownPercent_IsTheLivePower_UntilManual_AndOnlyTakesWritesWhileManual()
    {
        var mc = Make(new(), new());
        mc.SetLivePwm(128);
        Assert.Equal(50, mc.ShownPercent);

        mc.ShownPercent = 80; // not manual: ignored
        Assert.Equal(0, mc.Percent);
        Assert.Equal(50, mc.ShownPercent);

        mc.IsActive = true;
        mc.ShownPercent = 80;
        Assert.Equal(80, mc.Percent);
        Assert.Equal(80, mc.ShownPercent);
    }

    [Fact]
    public async Task SliderChange_WhileActive_Sends()
    {
        var sent = new List<byte>();
        var mc = Make(sent, new());

        mc.IsActive = true;  // live power unknown → starts at full speed
        mc.Percent = 50;
        await mc.PumpCompletion;

        Assert.Equal((byte)255, sent[0]);
        Assert.Equal(PwmScale.ToPwm(50), sent[^1]);
    }

    [Fact]
    public void Revert_WhenActive_StopsAndSendsAuto()
    {
        var auto = new List<string>();
        var mc = Make(new(), auto);

        mc.IsActive = true;
        mc.Revert();

        Assert.False(mc.IsActive);
        Assert.Equal(new[] { "f" }, auto); // genau einmal auf Auto zurück
    }

    [Fact]
    public void Revert_WhenIdle_IsNoOp()
    {
        var auto = new List<string>();
        var mc = Make(new(), auto);

        mc.Revert();

        Assert.False(mc.IsActive);
        Assert.Empty(auto); // nichts zu reverten → kein Auto-Befehl
    }

    [Fact]
    public async Task ReadOnly_DoesNotSend_OnEngage()
    {
        var sent = new List<byte>();
        var auto = new List<string>();
        var mc = Make(sent, auto, canControl: false);

        mc.IsActive = true; // read-only: kein Send, kein Auto
        mc.Percent = 80;
        await mc.PumpCompletion;

        Assert.Empty(sent);
        Assert.Empty(auto);
    }

    [Fact]
    public void SetLiveRpm_FormatsValueOrNa()
    {
        var mc = Make(new(), new());

        mc.SetLiveRpm(1234);
        Assert.Equal("1234 RPM", mc.LiveRpm);

        mc.SetLiveRpm(null);
        Assert.Equal("n/a", mc.LiveRpm);
    }
    // --- following the daemon ---------------------------------------------------------------------

    /// <summary>
    /// A command on its way is not a refusal: the switch holds for a few ticks without a confirmation, then shows the
    /// daemon's truth - without sending "auto", there is nothing to hand back.
    /// </summary>
    [Fact]
    public async Task Engaged_WithoutAConfirmation_HoldsForAFewTicks_ThenFollowsTheDaemon()
    {
        var auto = new List<string>();
        var mc = Make(new(), auto);
        mc.IsActive = true;
        await mc.PumpCompletion;

        for (int tick = 0; tick < 3; tick++)
            mc.ApplyDaemonOverride(false);
        Assert.True(mc.IsActive);

        mc.ApplyDaemonOverride(false);
        Assert.False(mc.IsActive);
        Assert.Equal(0, mc.ModeIndex);
        Assert.Empty(auto);
    }

    /// <summary>After a fail-safe or a daemon restart the override is gone: the switch must not keep claiming "manual".</summary>
    [Fact]
    public async Task Confirmed_ThenTheDaemonDropsTheOverride_TheSwitchGoesOffAtOnce()
    {
        var auto = new List<string>();
        var sent = new List<byte>();
        var mc = Make(sent, auto);
        mc.IsActive = true;
        await mc.PumpCompletion;
        mc.ApplyDaemonOverride(true);

        mc.ApplyDaemonOverride(false);

        Assert.False(mc.IsActive);
        Assert.Empty(auto);
        int sends = sent.Count;
        mc.Percent = 30; // no longer manual: moving the slider sends nothing
        await mc.PumpCompletion;
        Assert.Equal(sends, sent.Count);
    }

    [Fact]
    public void Inactive_AnOverrideFromElsewhere_DoesNotTurnTheSwitchOn()
    {
        var mc = Make(new(), new());

        mc.ApplyDaemonOverride(true);

        Assert.False(mc.IsActive);
    }
}
