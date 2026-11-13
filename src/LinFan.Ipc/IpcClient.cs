// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Text.Json;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;

namespace LinFan.Ipc;

/// <summary>
/// IPC-Client (läuft im GUI-/CLI-Prozess, ohne Root). Verbindet sich über einen
/// <see cref="IIpcClientTransport"/> mit dem Daemon-Endpunkt, liest den Strom von
/// <see cref="IpcSnapshot"/>s (NDJSON) und sendet <see cref="IpcCommand"/>s. Die Protokoll-Schicht
/// hier ist transport-neutral - Unix-Socket vs. Named Pipe entscheidet der Transport.
/// </summary>
public sealed class IpcClient : IIpcClient
{
    /// <summary>
    /// Longest snapshot line read. A snapshot may carry the whole config: a saved one stays below 4 MiB
    /// (<c>JsonConfigStore.MaxSaveBytes</c>), commands below 8 MiB - the bound leaves room for that and stops an impostor
    /// endpoint or an inflated config from exhausting GUI memory. Only a hand-edited start file far past what the app
    /// writes can exceed it; the GUI then stays connected without data, as with a snapshot it cannot read.
    /// </summary>
    private const int MaxSnapshotBytes = 16 * 1024 * 1024;

    private readonly IReadOnlyList<string> _candidates;
    private readonly IIpcClientTransport _transport;
    private readonly int _maxSnapshotBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1); // serialisiert gleichzeitige Sends (kein verschränktes NDJSON)
    private Stream? _stream;

    /// <summary>Der Endpunkt, mit dem zuletzt erfolgreich verbunden wurde (nach <see cref="ConnectAsync"/>).</summary>
    public string? ConnectedPath { get; private set; }

    /// <param name="path">Expliziter Endpunkt (v. a. Tests); ohne Angabe werden die Kandidaten durchprobiert.</param>
    /// <param name="transport">Transport-Implementierung; ohne Angabe der OS-passende Default.</param>
    public IpcClient(string? path = null, IIpcClientTransport? transport = null)
        : this(path, transport, MaxSnapshotBytes)
    {
    }

    /// <summary>Test seam: a smaller line bound, to check it without a 16-MiB payload.</summary>
    internal IpcClient(string? path, IIpcClientTransport? transport, int maxSnapshotBytes)
    {
        _candidates = path is not null ? new[] { path } : IpcEndpoint.ClientCandidates();
        _transport = transport ?? IpcTransportFactory.CreateClient();
        _maxSnapshotBytes = maxSnapshotBytes;
    }

    /// <summary>Verbindet der Reihe nach mit den angegebenen Kandidaten (erster erreichbarer gewinnt).</summary>
    public IpcClient(IReadOnlyList<string> candidates, IIpcClientTransport? transport = null)
    {
        _candidates = candidates.Count > 0 ? candidates : IpcEndpoint.ClientCandidates();
        _transport = transport ?? IpcTransportFactory.CreateClient();
        _maxSnapshotBytes = MaxSnapshotBytes;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (string candidate in _candidates)
        {
            Stream stream;
            try
            {
                stream = await _transport.ConnectAsync(candidate, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex; // dieser Endpunkt nicht erreichbar - nächsten Kandidaten probieren
                continue;
            }

            _stream = stream;
            ConnectedPath = candidate;
            // The config travels only when it changed (ReadSnapshotsAsync fills it in). A daemon that predates this
            // logs the command as unknown and keeps sending it every tick - both work.
            await SendCommandAsync(new IpcCommand(IpcCommand.ConfigOnChange), ct);
            return;
        }

        throw last ?? new InvalidOperationException("Keine Endpunkt-Kandidaten zum Verbinden.");
    }

    /// <summary>
    /// Reads the snapshot stream until the connection ends or is cancelled. Every snapshot it yields carries the daemon's
    /// config: one sent without it (unchanged revision, see <see cref="IpcSnapshot.ConfigRevision"/>) gets the last one.
    /// A line it cannot read - and a snapshot whose config this connection never received - yields <c>null</c>, so a
    /// reader does not keep showing the last snapshot as current.
    /// </summary>
    public async IAsyncEnumerable<IpcSnapshot?> ReadSnapshotsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_stream is null)
            throw new InvalidOperationException("Nicht verbunden - zuerst ConnectAsync aufrufen.");

        var buffer = new byte[16 * 1024];
        // Snapshots arrive every tick at about the same size, so the buffer a large one needed is kept for the next.
        var lines = new NdjsonLineBuffer(_maxSnapshotBytes, retainedBytes: _maxSnapshotBytes);
        var received = new List<IpcSnapshot?>(); // null: a line that could not be read
        IpcConfig? lastConfig = null;
        long? lastRevision = null;
        LineHandler parse = line =>
        {
            IpcSnapshot? snapshot = null;
            try { snapshot = JsonSerializer.Deserialize<IpcSnapshot>(line, IpcJson.Options); }
            catch { /* reported as unreadable below */ }
            received.Add(snapshot);
            return true;
        };
        Func<bool> tooLong = () =>
        {
            received.Add(null);
            return true;
        };

        while (!ct.IsCancellationRequested)
        {
            int read = await _stream.ReadAsync(buffer, ct);
            if (read == 0)
                yield break; // Verbindung geschlossen

            lines.Feed(buffer.AsSpan(0, read), parse, tooLong);
            foreach (IpcSnapshot? snapshot in received)
            {
                if (snapshot is null)
                {
                    yield return null;
                }
                else if (snapshot.Config is not null)
                {
                    lastConfig = snapshot.Config;
                    lastRevision = snapshot.ConfigRevision;
                    yield return snapshot;
                }
                else if (snapshot.ConfigRevision is not { } revision)
                {
                    yield return snapshot; // a daemon without revisions: the config was simply not sent
                }
                else if (revision == lastRevision)
                {
                    yield return snapshot with { Config = lastConfig };
                }
                else
                {
                    // A config this connection never received (its line was too long or unreadable): readers would take
                    // the missing one for an empty one. The server sends it again with the next change.
                    yield return null;
                }
            }
            received.Clear();
        }
    }

    public async Task SendCommandAsync(IpcCommand command, CancellationToken ct = default)
    {
        if (_stream is null)
            throw new InvalidOperationException("Nicht verbunden - zuerst ConnectAsync aufrufen.");

        byte[] payload = IpcJson.SerializeLine(command);

        // Mehrere Aufrufer (GUI-Befehle aus IpcLiveMonitor) dürfen sich nicht auf dem Stream
        // überlappen - sonst verschränken sich die NDJSON-Zeilen bzw. NetworkStream wirft.
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(payload, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _stream?.Dispose(); } catch { /* egal */ }
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
