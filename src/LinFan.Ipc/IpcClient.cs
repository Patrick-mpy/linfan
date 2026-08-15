// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Text;
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
    private readonly IReadOnlyList<string> _candidates;
    private readonly IIpcClientTransport _transport;
    private readonly SemaphoreSlim _writeLock = new(1, 1); // serialisiert gleichzeitige Sends (kein verschränktes NDJSON)
    private Stream? _stream;
    private StreamReader? _reader;

    /// <summary>Der Endpunkt, mit dem zuletzt erfolgreich verbunden wurde (nach <see cref="ConnectAsync"/>).</summary>
    public string? ConnectedPath { get; private set; }

    /// <param name="path">Expliziter Endpunkt (v. a. Tests); ohne Angabe werden die Kandidaten durchprobiert.</param>
    /// <param name="transport">Transport-Implementierung; ohne Angabe der OS-passende Default.</param>
    public IpcClient(string? path = null, IIpcClientTransport? transport = null)
    {
        _candidates = path is not null ? new[] { path } : IpcEndpoint.ClientCandidates();
        _transport = transport ?? IpcTransportFactory.CreateClient();
    }

    /// <summary>Verbindet der Reihe nach mit den angegebenen Kandidaten (erster erreichbarer gewinnt).</summary>
    public IpcClient(IReadOnlyList<string> candidates, IIpcClientTransport? transport = null)
    {
        _candidates = candidates.Count > 0 ? candidates : IpcEndpoint.ClientCandidates();
        _transport = transport ?? IpcTransportFactory.CreateClient();
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
            // leaveOpen: _stream ist der alleinige Besitzer (Dispose schließt die Verbindung) -
            // sonst würde DisposeAsync den Stream doppelt entsorgen.
            _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            ConnectedPath = candidate;
            return;
        }

        throw last ?? new InvalidOperationException("Keine Endpunkt-Kandidaten zum Verbinden.");
    }

    /// <summary>Liest den Snapshot-Strom, bis die Verbindung endet oder abgebrochen wird.</summary>
    public async IAsyncEnumerable<IpcSnapshot> ReadSnapshotsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_reader is null)
            throw new InvalidOperationException("Nicht verbunden - zuerst ConnectAsync aufrufen.");

        while (!ct.IsCancellationRequested)
        {
            string? line = await _reader.ReadLineAsync(ct);
            if (line is null)
                yield break; // Verbindung geschlossen

            IpcSnapshot? snapshot = null;
            try { snapshot = JsonSerializer.Deserialize<IpcSnapshot>(line, IpcJson.Options); }
            catch { /* ungültige Zeile überspringen */ }

            if (snapshot is not null)
                yield return snapshot;
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
        try { _reader?.Dispose(); } catch { /* egal */ }
        try { _stream?.Dispose(); } catch { /* egal */ }
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
