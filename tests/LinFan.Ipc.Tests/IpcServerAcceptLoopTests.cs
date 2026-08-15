// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Ipc;
using LinFan.Ipc.Transport;
using Xunit;

namespace LinFan.Ipc.Tests;

/// <summary>
/// Verhalten der Accept-Schleife bei fehlschlagender Verbindungsannahme. Regression: sie wiederholte
/// sofort und ohne Pause - ein <b>dauerhafter</b> Fehler (unter Windows z. B. das fehlende Recht, eine
/// weitere Pipe-Instanz anzulegen) ließ den privilegierten Daemon damit frei drehen, statt in Ruhe zu
/// wiederholen. Erholen muss sie sich weiterhin.
/// </summary>
public class IpcServerAcceptLoopTests
{
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task AcceptLoop_WithPermanentFailure_BacksOffInsteadOfSpinning()
    {
        var transport = new FlakyServerTransport(failures: int.MaxValue);
        await using var server = new IpcServer("test-accept-spin", transport, log: null, maxCommandBytes: 1024,
                                               acceptRetryDelay: Retry);
        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(400));
        cts.Cancel();

        // Mit Backoff sind ~8 Versuche zu erwarten; ohne Backoff waren es Zehntausende. Die Obergrenze ist
        // bewusst großzügig - geprüft wird „dreht nicht frei", nicht die exakte Taktung.
        Assert.InRange(transport.Attempts, 1, 40);
    }

    [Fact]
    public async Task AcceptLoop_AfterTransientFailure_AcceptsNextClient()
    {
        var transport = new FlakyServerTransport(failures: 2);
        await using var server = new IpcServer("test-accept-recover", transport, log: null, maxCommandBytes: 1024,
                                               acceptRetryDelay: Retry);
        var accepted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientsChanged = count =>
        {
            if (count > 0)
                accepted.TrySetResult(count);
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);

        Assert.Equal(1, await accepted.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(transport.Attempts >= 3, $"Erwartet: 2 Fehlversuche + 1 Erfolg, tatsächlich {transport.Attempts}.");
        cts.Cancel();
    }

    /// <summary>
    /// Scheitert die ersten <c>failures</c> Annahmen, liefert danach genau eine Verbindung und blockiert
    /// anschließend bis zum Abbruch (wie ein echter Listener ohne wartende Clients).
    /// </summary>
    private sealed class FlakyServerTransport : IIpcServerTransport
    {
        private readonly int _failures;
        private int _attempts;

        public FlakyServerTransport(int failures) => _failures = failures;

        public int Attempts => Volatile.Read(ref _attempts);

        public void Listen(string endpoint) { }

        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= _failures)
                throw new UnauthorizedAccessException("Zugriff verweigert (simuliert).");
            if (attempt == _failures + 1)
                return new MemoryStream(); // liefert sofort EOF - der Server nimmt sie an und räumt sie ab

            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }

        public void Dispose() { }
    }
}
