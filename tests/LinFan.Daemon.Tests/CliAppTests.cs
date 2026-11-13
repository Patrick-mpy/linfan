// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
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

    [Theory]
    [InlineData(FanReadOnlyReason.NoWriteAccess, false, "read-only ({elevation} nötig)")]
    [InlineData(FanReadOnlyReason.NoWriteAccess, true, "read-only (kein Schreibzugriff)")]
    [InlineData(FanReadOnlyReason.DriverLocked, false, "read-only (Treiber-Sperre)")]
    [InlineData(FanReadOnlyReason.NoControlInterface, false, "read-only (keine Steuerung)")] // root would not help
    [InlineData(null, false, "read-only")]
    public void ControlText_NamesTheBackendsReason(FanReadOnlyReason? reason, bool elevated, string expected)
    {
        var fan = new FanDescriptor(new FanId("smc/F1"), "Fan 2", CanControl: false, Tachometer: null, "F1Ac", reason);

        Assert.Equal(expected.Replace("{elevation}", Privileges.ElevationTerm, StringComparison.Ordinal), CliApp.ControlText(fan, elevated));
    }

    [Fact]
    public void ControlText_ControllableChannel() =>
        Assert.Equal("steuerbar", CliApp.ControlText(
            new FanDescriptor(new FanId("hwmon7/pwm1"), "pwm1", CanControl: true, Tachometer: null, "pwm1"), elevated: true));

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

    [Theory]
    [InlineData("set", true)]
    [InlineData("calibrate", true)]
    [InlineData("init", true)]     // would replace the config the daemon owns
    [InlineData("auto", false)]    // toward the safe state - also the way out after a crashed `set`
    [InlineData("restore", false)] // the emergency brake, and the unit's ExecStopPost
    [InlineData("list", false)]
    [InlineData("monitor", false)]
    public void RefusedNextToDaemon_OnlyTheWritingCommands_WhereADaemonHoldsTheEndpoint(string command, bool refused)
    {
        if (OperatingSystem.IsWindows())
            return; // the pipe's side is in LinFan.Ipc.Tests

        string path = Path.Combine(Path.GetTempPath(), $"linfan-cli-{Guid.NewGuid():N}.sock");
        using var daemon = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        daemon.Bind(new UnixDomainSocketEndPoint(path));
        daemon.Listen(4);

        Assert.Equal(refused, CliApp.RefusedNextToDaemon(command, [path]));
    }

    [Fact]
    public void RefusedNextToDaemon_NotWithoutADaemon()
    {
        if (OperatingSystem.IsWindows())
            return; // a socket path is no pipe name

        string path = Path.Combine(Path.GetTempPath(), $"linfan-cli-{Guid.NewGuid():N}.sock");

        Assert.False(CliApp.RefusedNextToDaemon("set", [path]));
        Assert.False(CliApp.RefusedNextToDaemon("calibrate", [path]));
        Assert.False(CliApp.RefusedNextToDaemon("init", [path]));
        Assert.False(CliApp.RefusedNextToDaemon("set", ["/" + new string('x', 200) + ".sock"])); // too long for a socket
    }

    [Fact]
    public void RefusedNextToDaemon_WhereADaemonHoldsAnyOfTheEndpoints()
    {
        // A LINFAN_SOCKET in the CLI's environment names another socket first - the service's must still count.
        if (OperatingSystem.IsWindows())
            return;

        string elsewhere = TempSocket();
        using var daemon = Listening(out string service);

        Assert.True(CliApp.RefusedNextToDaemon("set", [elsewhere, service]));
    }

    [Fact]
    public async Task Run_Refused_CreatesNoBackend_SoNothingIsRestored()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var daemon = Listening(out string service);
        int created = 0;
        (ISensorBackend, IFanController) Create()
        {
            created++;
            var hw = new FakeHardware();
            return (hw, hw);
        }

        Assert.Equal(3, await CliApp.RunAsync(["calibrate", "f1"], Create, [service]));
        Assert.Equal(0, created);

        // The control: without a daemon the same command gets its backends (and ends on the unknown fan).
        Assert.Equal(1, await CliApp.RunAsync(["calibrate", "f1"], Create, [TempSocket()]));
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task Run_WithABackendThatCannotStart_EndsWithAnExitCode()
    {
        // `restore` is the units' ExecStopPost: it must end with a code, not an unhandled exception and a core dump.
        static (ISensorBackend, IFanController) Fails() => throw new IOException("driver gone");

        Assert.Equal(1, await CliApp.RunAsync(["restore"], Fails, [TempSocket()]));
    }

    private static string TempSocket() => Path.Combine(Path.GetTempPath(), $"linfan-cli-{Guid.NewGuid():N}.sock");

    /// <summary>A socket that listens like a daemon (never accepts, never sends).</summary>
    private static Socket Listening(out string path)
    {
        path = TempSocket();
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(4);
        return socket;
    }
}
