// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;

namespace LinFan.App.Tests;

/// <summary>
/// Direkte Abdeckung der geteilten Coalescing-Pumpe (Dashboard, Geräte-Tab, Onboarding, Positions-Modal):
/// gedrosseltes Senden, Coalescing der Slider-Flut auf den letzten Wert, und sauberes Anhalten beim Verlassen.
/// </summary>
public sealed class ManualPwmPumpTests
{
    private static ManualPwmPump Pump(Func<string, byte, Task> send) =>
        new("f") { Throttle = TimeSpan.Zero, Send = send };

    [Fact]
    public async Task Set_SendsValue()
    {
        var sent = new List<byte>();
        var pump = Pump((_, p) => { sent.Add(p); return Task.CompletedTask; });

        pump.Set(128);
        await pump.Completion;

        Assert.Equal(new byte[] { 128 }, sent);
    }

    [Fact]
    public async Task Set_WithoutSend_IsNoOp()
    {
        var pump = new ManualPwmPump("f") { Throttle = TimeSpan.Zero };

        pump.Set(200);            // keine Bindung → darf nicht werfen
        await pump.Completion;
    }

    [Fact]
    public async Task SliderFlood_WhileInFlight_CoalescesToLatest()
    {
        var sent = new List<byte>();
        var firstInFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var pump = Pump(async (_, p) =>
        {
            sent.Add(p);
            if (sent.Count == 1) { firstInFlight.SetResult(); await release.Task; } // ersten Send in der Luft halten
        });

        pump.Set(0);                       // erster Send, hängt am Gate
        await firstInFlight.Task;
        for (byte p = 1; p <= 100; p++)    // viele schnelle Änderungen, während der erste Send hängt
            pump.Set(p);
        release.SetResult();
        await pump.Completion;

        Assert.Equal((byte)0, sent[0]);    // erster Stellwert
        Assert.Equal((byte)100, sent[^1]); // Endwert garantiert gesendet
        Assert.True(sent.Count <= 2, $"Zwischenwerte müssen coalescen; gesendet: {sent.Count}");
    }

    [Fact]
    public async Task Stop_WhileInFlight_SuppressesPendingValue()
    {
        var sent = new List<byte>();
        var firstInFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var pump = Pump(async (_, p) =>
        {
            sent.Add(p);
            if (sent.Count == 1) { firstInFlight.SetResult(); await release.Task; }
        });

        pump.Set(50);                      // erster Send, hängt am Gate
        await firstInFlight.Task;
        pump.Set(200);                     // neuer Zielwert angemeldet …
        pump.Stop();                       // … aber beim Verlassen verworfen
        release.SetResult();
        await pump.Completion;

        Assert.Equal(new byte[] { 50 }, sent); // der angehaltene Zielwert (200) wird nicht mehr gesendet
    }
}
