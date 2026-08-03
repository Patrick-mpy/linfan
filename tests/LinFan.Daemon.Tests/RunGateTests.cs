// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Daemon;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Deckt das aus <see cref="CalibrationCoordinator"/>/<see cref="IdentifyCoordinator"/> herausgezogene
/// Lauf-Gate ab: genau ein Lauf gleichzeitig, Kopplung ans Host-Token, Abbruch-und-Warten, sowie die
/// Unter-der-Sperre-Rückrufe (Cooldown-Prüfung / Cooldown-Anker).
/// </summary>
public class RunGateTests
{
    [Fact]
    public void TryBegin_WhenIdle_Succeeds_SecondCallWhileRunning_Fails()
    {
        var gate = new RunGate(CancellationToken.None);

        Assert.True(gate.TryBegin(out _));
        Assert.True(gate.IsRunning);
        Assert.False(gate.TryBegin(out _)); // läuft bereits — genau ein Lauf gleichzeitig

        gate.End();
        Assert.False(gate.IsRunning);
        Assert.True(gate.TryBegin(out _)); // Gate wieder frei
    }

    [Fact]
    public void TryBegin_CanStartFalse_Rejects_WithoutOpeningGate()
    {
        var gate = new RunGate(CancellationToken.None);

        Assert.False(gate.TryBegin(out _, canStart: () => false));
        Assert.False(gate.IsRunning); // Guard hat abgelehnt → kein Lauf begonnen

        Assert.True(gate.TryBegin(out _, canStart: () => true));
    }

    [Fact]
    public void Cancel_WhenRunning_CancelsToken_ReturnsTrue_WhenIdleNotRun()
    {
        var gate = new RunGate(CancellationToken.None);
        Assert.True(gate.TryBegin(out CancellationToken token));

        bool idleRan = false;
        Assert.True(gate.Cancel(whenIdle: () => idleRan = true));
        Assert.False(idleRan);                    // ein Lauf war aktiv → whenIdle NICHT ausgeführt
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_WhenIdle_RunsWhenIdle_ReturnsFalse()
    {
        var gate = new RunGate(CancellationToken.None);

        bool idleRan = false;
        Assert.False(gate.Cancel(whenIdle: () => idleRan = true));
        Assert.True(idleRan); // kein Lauf aktiv → whenIdle ausgeführt (z. B. Abschluss-Status quittieren)
    }

    [Fact]
    public void End_FreesGate_AndRunsUnderLockAction()
    {
        var gate = new RunGate(CancellationToken.None);
        Assert.True(gate.TryBegin(out _));

        bool underLockRan = false;
        gate.End(underLock: () => underLockRan = true);

        Assert.True(underLockRan);
        Assert.False(gate.IsRunning);
        Assert.True(gate.TryBegin(out _)); // Gate freigegeben
    }

    [Fact]
    public async Task StopAsync_CancelsRun_AndAwaitsAttachedTask()
    {
        var gate = new RunGate(CancellationToken.None);
        Assert.True(gate.TryBegin(out CancellationToken token));

        // Ein Lauf, der bis zum Abbruch hängt und im finally — wie die Koordinatoren — das Gate schließt.
        Task run = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { /* erwartet */ }
            finally { gate.End(); }
        });
        gate.Attach(run);
        Assert.True(gate.IsRunning);

        await gate.StopAsync(); // bricht ab UND wartet auf das Lauf-Ende

        Assert.True(run.IsCompleted);
        Assert.False(gate.IsRunning); // End() lief im finally des Laufs
    }

    [Fact]
    public void TryBegin_LinksRunTokenToHostToken()
    {
        using var host = new CancellationTokenSource();
        var gate = new RunGate(host.Token);

        Assert.True(gate.TryBegin(out CancellationToken token));
        Assert.False(token.IsCancellationRequested);

        host.Cancel();
        Assert.True(token.IsCancellationRequested); // Shutdown (Host-Token) bricht den Lauf ab
    }
}
