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

    [Fact]
    public async Task SliderChange_WhileActive_Sends()
    {
        var sent = new List<byte>();
        var mc = Make(sent, new());

        mc.IsActive = true;  // sendet 0 (Default-Prozent)
        mc.Percent = 50;
        await mc.PumpCompletion;

        Assert.Equal((byte)0, sent[0]);
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
}
