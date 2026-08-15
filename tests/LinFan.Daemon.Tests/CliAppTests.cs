// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Daemon;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Sichert die Entscheidung, nach welchen CLI-Kommandos der Fail-Safe-Restore läuft. Read-only-Kommandos
/// (<c>list</c>/<c>monitor</c>/<c>init</c>) dürfen KEIN RestoreDefaults auslösen - das würfe neben einem
/// laufenden Daemon alle Lüfter kurz auf Hardware-Auto und riss dessen Kurvenregelung weg.
/// </summary>
public class CliAppTests
{
    [Theory]
    [InlineData("set")]
    [InlineData("calibrate")]
    public void CommandTouchesPwm_True_ForPwmWritingCommands(string command) =>
        Assert.True(CliApp.CommandTouchesPwm(command));

    [Theory]
    [InlineData("list")]
    [InlineData("monitor")]
    [InlineData("init")]
    [InlineData("auto")]
    [InlineData("help")]
    public void CommandTouchesPwm_False_ForReadOnlyOrExplicitAutoCommands(string command) =>
        Assert.False(CliApp.CommandTouchesPwm(command));

    [Fact]
    public async Task Set_AllTempsUnreadable_AbortsToSafeAfterBlindHolds()
    {
        // Kein lesbarer Temperatursensor (EIO) → das manuelle Halten läuft NICHT unbegrenzt ohne Watchdog,
        // sondern fällt nach MaxBlindHolds Zyklen sicher auf Auto zurück (Fail-Safe-Rückgabecode 3).
        var hw = new FakeHardware();
        hw.AddThrowingTempSensor("bad");
        hw.AddFan("f1");
        using var cts = new CancellationTokenSource();

        int rc = await CliApp.SetAsync(hw, hw, new[] { "set", "f1", "128" }, cts.Token,
            delay: (_, _) => Task.CompletedTask);

        Assert.Equal(3, rc);                            // Fail-Safe
        Assert.Contains(("f1", (byte)128), hw.Writes);  // manuell gesetzt …
        Assert.True(hw.RestoreCount >= 1);              // … dann ohne Strg+C sicher zurückgestellt
    }

    [Fact]
    public async Task Set_TemperatureRecovers_ResetsBlindCounter_DoesNotAbort()
    {
        // Startet blind, wird nach dem 1. Zyklus wieder lesbar (kühl) → der Blind-Zähler setzt zurück,
        // KEIN Fail-Safe; sauberer Abschluss per Abbruch (Strg+C-Äquivalent).
        var hw = new FakeHardware();
        hw.AddThrowingTempSensor("bad");
        hw.AddFan("f1");
        using var cts = new CancellationTokenSource();

        int calls = 0;
        Task Delay(int _, CancellationToken __)
        {
            calls++;
            if (calls == 1) { hw.ThrowingReads.Remove("bad"); hw.Values["bad"] = 40; }
            if (calls >= 5) cts.Cancel();
            return Task.CompletedTask;
        }

        int rc = await CliApp.SetAsync(hw, hw, new[] { "set", "f1", "128" }, cts.Token, Delay);

        Assert.Equal(0, rc);               // sauber beendet, NICHT Fail-Safe (3)
        Assert.Equal(0, hw.RestoreCount);  // in der Schleife kein Restore (der äußere finally macht das)
    }

    [Fact]
    public async Task Set_ThrowingTachometer_DoesNotAbortWatchdog_BlindHoldStillFires()
    {
        // Kaputter Temp- UND Tacho-Kanal (typische Chip-Ko-Störung): der werfende Tacho-Read darf den
        // Loop nicht abreißen, sonst käme der Blind-Hold-Watchdog nie zum Zug. Erwartet: sauberer
        // Fail-Safe (3), kein propagierender Read-Fehler.
        var hw = new FakeHardware();
        hw.AddThrowingTempSensor("bad");
        hw.AddFanSensor("tach_bad", 0);
        hw.ThrowingReads.Add("tach_bad");
        hw.AddFan("f1", tachId: "tach_bad");
        using var cts = new CancellationTokenSource();

        int rc = await CliApp.SetAsync(hw, hw, new[] { "set", "f1", "128" }, cts.Token,
            delay: (_, _) => Task.CompletedTask);

        Assert.Equal(3, rc);                // Blind-Hold-Fail-Safe, nicht ein durchschlagender EIO
        Assert.True(hw.RestoreCount >= 1);
    }
}
