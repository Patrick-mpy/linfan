// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinFan.Ipc;

/// <summary>
/// IPC-Server (läuft im privilegierten Daemon). Hört über einen <see cref="IIpcServerTransport"/> am
/// Endpunkt, pusht <see cref="IpcSnapshot"/>s (NDJSON, eine Zeile pro Tick) an alle verbundenen
/// Clients und leitet eingehende <see cref="IpcCommand"/>s an <see cref="CommandHandler"/> weiter.
/// Die Protokoll-Schicht ist transport-neutral - Unix-Socket vs. Named Pipe entscheidet der Transport.
/// </summary>
public sealed class IpcServer : IIpcServer
{
    /// <summary>
    /// Obergrenze für eine einzelne eingehende NDJSON-Kommandozeile. Ein legitimes Kommando ist winzig;
    /// selbst eine komplette Config (SaveConfig/ReplaceConfig, Dutzende Lüfter/Kurven) bleibt weit darunter.
    /// Die Grenze verhindert, dass ein bösartiger oder defekter Client den privilegierten Prozess über eine
    /// endlose Zeile ohne Zeilenende zu unbegrenztem Speicherwachstum treibt (DoS).
    /// </summary>
    private const int MaxCommandBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Wartezeit, bevor eine fehlgeschlagene Verbindungsannahme wiederholt wird. Ohne sie würde ein
    /// <b>dauerhafter</b> Accept-Fehler (z. B. fehlendes Recht, eine weitere Pipe-Instanz anzulegen) die
    /// Schleife frei drehen lassen - im privilegierten Daemon eine Dauerlast, die nichts erreicht.
    /// </summary>
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Snapshots queued per client before it counts as stalled. Broadcasting runs on the daemon's control
    /// loop, so it must never wait on a client: a client that stops reading (stopped GUI, hostile peer)
    /// would otherwise freeze the loop and with it the over-temperature watchdog.
    /// </summary>
    private const int OutboxCapacity = 4;

    /// <summary>Upper bound for writing one snapshot to a client before it is dropped as stalled.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Connections served at once. A GUI per logged-in user and a <c>monitor-ipc</c> or two stay far below it; without
    /// a bound, every connection may hold up to <see cref="MaxCommandBytes"/> of line buffer in the root daemon.
    /// </summary>
    private const int MaxClients = 16;

    /// <summary>
    /// A command line that has begun must end within this time. A peer that sends part of a line and then nothing
    /// would otherwise keep its buffer for as long as it stays connected; a whole config needs milliseconds locally.
    /// </summary>
    private static readonly TimeSpan PartialLineTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Line buffer a connection keeps between commands; a larger one, left by a whole config, is released.</summary>
    private const int RetainedLineBufferBytes = 64 * 1024;

    /// <summary>
    /// Commands logged with the connection that sent them: every member of the group has the same authority, so the
    /// log is the only way to tell who changed the config or started a device action. Frequent ones (manual PWM while a
    /// slider moves) stay out of it.
    /// </summary>
    private static readonly HashSet<string> AuditedCommands = new(StringComparer.Ordinal)
    {
        IpcCommand.SaveConfig, IpcCommand.ReplaceConfig, IpcCommand.ResetConfig, IpcCommand.Reload,
        IpcCommand.SetActiveProfile, IpcCommand.SetCurveEnabled, IpcCommand.SetFanTachometer,
        IpcCommand.AcceptHardware, IpcCommand.ForgetHardware,
        IpcCommand.StartCalibration, IpcCommand.Identify, IpcCommand.StartTachMapping,
    };

    private readonly string _path;
    private readonly IIpcServerTransport _transport;
    private readonly ILogger _log;
    private readonly int _maxCommandBytes;
    private readonly TimeSpan _acceptRetryDelay;
    private readonly TimeSpan _writeTimeout;
    private readonly int _maxClients;
    private readonly TimeSpan _partialLineTimeout;
    private readonly List<Client> _clients = new();
    private readonly object _lock = new();
    private int _lastConnectionId;

    /// <summary>Wird für jedes empfangene Kommando aufgerufen (z. B. Config-Reload).</summary>
    public Action<IpcCommand>? CommandHandler { get; set; }

    /// <summary>Wird bei jeder Verbindungs-Änderung mit der aktuellen Client-Anzahl aufgerufen.</summary>
    public Action<int>? ClientsChanged { get; set; }

    /// <param name="path">Expliziter Endpunkt (v. a. Tests); ohne Angabe der OS-Default.</param>
    /// <param name="transport">Transport-Implementierung; ohne Angabe der OS-passende Default.</param>
    /// <param name="log">Logger für die Audit-/Fehler-Spur; ohne Angabe stiller <see cref="NullLogger"/>.</param>
    public IpcServer(string? path = null, IIpcServerTransport? transport = null, ILogger? log = null)
        : this(path, transport, log, MaxCommandBytes)
    {
    }

    /// <summary>
    /// Test-Seam: erlaubt eine kleinere Zeilen-Obergrenze, um den DoS-Guard ohne 8-MiB-Payload zu prüfen,
    /// und eine kürzere <see cref="AcceptRetryDelay"/>, damit der Backoff ohne echte Wartezeit prüfbar ist.
    /// </summary>
    internal IpcServer(string? path, IIpcServerTransport? transport, ILogger? log, int maxCommandBytes,
                       TimeSpan? acceptRetryDelay = null, TimeSpan? writeTimeout = null, int maxClients = MaxClients,
                       TimeSpan? partialLineTimeout = null)
    {
        _path = path ?? IpcEndpoint.SocketPath;
        _log = log ?? NullLogger.Instance;
        _transport = transport ?? IpcTransportFactory.CreateServer(_log); // Logger für Zugriffskontrolle/Audit
        _maxCommandBytes = maxCommandBytes;
        _acceptRetryDelay = acceptRetryDelay ?? AcceptRetryDelay;
        _writeTimeout = writeTimeout ?? WriteTimeout;
        _maxClients = maxClients;
        _partialLineTimeout = partialLineTimeout ?? PartialLineTimeout;
    }

    public string Path => _path;

    public int ClientCount
    {
        get
        {
            lock (_lock) return _clients.Count;
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        _transport.Listen(_path, ct);
        _ = AcceptLoopAsync(ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues the snapshot for every connected client and returns without waiting for any of them; each
    /// client's own writer drains its queue. A client whose queue is full is dropped as stalled.
    /// <para>
    /// A client that asked for it (<see cref="IpcCommand.ConfigOnChange"/>) gets the config only when its
    /// <see cref="IpcSnapshot.ConfigRevision"/> differs from the one last queued for it; every other snapshot goes
    /// without. The whole config is serialized only for a client that needs it.
    /// </para>
    /// </summary>
    public Task BroadcastAsync(IpcSnapshot snapshot)
    {
        List<Client> targets;
        lock (_lock)
            targets = _clients.ToList();

        byte[]? full = null;
        byte[]? withoutConfig = null;
        foreach (Client client in targets)
        {
            bool alreadyHasIt = client.ConfigOnChange && snapshot.Config is not null
                && snapshot.ConfigRevision is { } revision && client.QueuedConfigRevision == revision;
            byte[] payload = alreadyHasIt
                ? withoutConfig ??= IpcJson.SerializeLine(snapshot with { Config = null })
                : full ??= IpcJson.SerializeLine(snapshot);

            if (client.Outbox.Writer.TryWrite(payload))
            {
                client.QueuedConfigRevision = snapshot.ConfigRevision;
                continue;
            }

            if (client.IsClosed)
                continue; // disconnected in the meantime, not stalled

            _log.LogWarning("IPC: Client liest nicht mehr ({Capacity} Snapshots offen) - Verbindung getrennt.",
                OutboxCapacity);
            Remove(client);
        }
        return Task.CompletedTask;
    }

    private async Task WriteSnapshotsAsync(Client client)
    {
        try
        {
            await foreach (byte[] payload in client.Outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // WaitAsync instead of a cancellation token: not every transport stream honours cancellation
                // of a pending write. On timeout, Remove disposes the stream, which aborts the pending write.
                await SendAsync(client.Stream, payload).WaitAsync(_writeTimeout).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            _log.LogWarning("IPC: Snapshot-Schreiben an einen Client dauerte über {Ms} ms - Verbindung getrennt.",
                _writeTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPC: Broadcast an einen Client fehlgeschlagen - Client entfernt.");
        }
        finally
        {
            Remove(client);
        }
    }

    private static async Task SendAsync(Stream stream, byte[] payload)
    {
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // Je Fehler-Serie nur einmal warnen (und einmal je Erholung zurücksetzen): ein Dauerfehler würde
        // sonst im Sekundentakt dasselbe ins Log schreiben.
        bool failureLogged = false;
        bool capLogged = false;

        while (!ct.IsCancellationRequested)
        {
            Stream client;
            try
            {
                client = await _transport.AcceptAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                // Warnung statt Debug: schlägt die Annahme dauerhaft fehl, nimmt der Daemon keine GUI mehr
                // an - das darf nicht nur im Debug-Log stehen.
                if (!failureLogged)
                {
                    failureLogged = true;
                    _log.LogWarning(ex, "IPC: Verbindungsannahme fehlgeschlagen - Wiederholung alle {Ms} ms.",
                        _acceptRetryDelay.TotalMilliseconds);
                }

                try { await Task.Delay(_acceptRetryDelay, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            failureLogged = false;
            var connection = new Client(client, Interlocked.Increment(ref _lastConnectionId), DescribePeer(client));
            int count;
            bool full;
            lock (_lock)
            {
                full = _clients.Count >= _maxClients;
                if (!full)
                    _clients.Add(connection);
                count = _clients.Count;
            }
            if (full)
            {
                // Once per run of rejections: a peer reconnecting in a loop must not flood the log either.
                if (!capLogged)
                {
                    capLogged = true;
                    _log.LogWarning("IPC: bereits {Max} Verbindungen - weitere werden abgewiesen.", _maxClients);
                }
                connection.Close();
                continue;
            }
            capLogged = false;
            _log.LogInformation("IPC: Verbindung #{Id} angenommen ({Peer}).", connection.Id, connection.Peer);
            ClientsChanged?.Invoke(count);
            _ = WriteSnapshotsAsync(connection);
            _ = ReadCommandsAsync(connection, ct);
        }
    }

    /// <summary>
    /// Liest NDJSON-Kommandozeilen roh aus dem Stream und begrenzt dabei die Länge einer einzelnen Zeile
    /// auf <see cref="MaxCommandBytes"/> (DoS-Schutz im privilegierten Prozess, <see cref="NdjsonLineBuffer"/>).
    /// </summary>
    private async Task ReadCommandsAsync(Client client, CancellationToken ct)
    {
        Stream stream = client.Stream;
        try
        {
            var buffer = new byte[4096];
            var lines = new NdjsonLineBuffer(_maxCommandBytes, RetainedLineBufferBytes);
            // A dropped client (stalled writer) has already been reported as gone; commands still buffered from it
            // must not act after that, e.g. re-set a manual override that the disconnect just cleared.
            LineHandler dispatch = line =>
            {
                if (client.IsClosed)
                    return false;
                DispatchLine(client, line);
                return true;
            };
            Func<bool> tooLong = () =>
            {
                _log.LogWarning(
                    "IPC: Kommando überschreitet {Max} Bytes ohne Zeilenende - Verbindung getrennt (möglicher DoS).",
                    _maxCommandBytes);
                return false; // finally entfernt den Client
            };
            while (true)
            {
                int read;
                try
                {
                    ValueTask<int> pending = stream.ReadAsync(buffer, ct);
                    if (!lines.InLine)
                    {
                        read = await pending;
                    }
                    else
                    {
                        // On timeout the finally below closes the stream, which ends the pending read - observe its
                        // failure then, so it does not surface as an unobserved task exception.
                        Task<int> readTask = pending.AsTask();
                        _ = readTask.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        TimeSpan left = _partialLineTimeout - Stopwatch.GetElapsedTime(lines.LineStarted);
                        read = await readTask.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, ct);
                    }
                }
                catch (TimeoutException)
                {
                    _log.LogWarning("IPC: Kommandozeile nach {Seconds} s nicht abgeschlossen - Verbindung getrennt.",
                        _partialLineTimeout.TotalSeconds);
                    break;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "IPC: Lesefehler auf der Client-Verbindung - Verbindung wird geschlossen.");
                    break;
                }

                if (read == 0)
                    break; // Verbindung sauber geschlossen

                if (!lines.Feed(buffer.AsSpan(0, read), dispatch, tooLong))
                    return;
            }
        }
        finally
        {
            Remove(client);
        }
    }

    /// <summary>Deserialisiert eine gepufferte NDJSON-Zeile (UTF-8) und übergibt das Kommando an den Handler.</summary>
    private void DispatchLine(Client client, ReadOnlySpan<byte> line)
    {
        IpcCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<IpcCommand>(line, IpcJson.Options);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPC: ungültige Kommandozeile verworfen ({Bytes} Bytes).", line.Length);
            return;
        }

        if (command is null)
            return;

        // How this connection wants its snapshots - a matter of the protocol, not a command for the daemon.
        if (command.Command == IpcCommand.ConfigOnChange)
        {
            client.ConfigOnChange = true;
            return;
        }

        // Only names from the set reach the log: the command text itself comes from the peer. Manual PWM arrives many
        // times a second while a slider moves - its first one names who took a fan over by hand.
        bool firstManual = command.Command == IpcCommand.SetManualPwm && !client.ManualLogged;
        if (AuditedCommands.Contains(command.Command) || firstManual)
        {
            client.ManualLogged |= firstManual;
            _log.LogInformation("IPC: {Command} von Verbindung #{Id} ({Peer}).", command.Command, client.Id, client.Peer);
        }

        try
        {
            CommandHandler?.Invoke(command);
        }
        catch (Exception ex)
        {
            // Der Handler (Daemon) darf die Client-Leseschleife nie reißen - ein einzelnes fehlerhaftes
            // Kommando brächte sonst den ganzen Reader (und damit diese GUI-Verbindung) zu Fall.
            _log.LogWarning(ex, "IPC: Verarbeitung des Kommandos {Command} fehlgeschlagen.", command.Command);
        }
    }

    private void Remove(Client client)
    {
        bool removed;
        int count;
        lock (_lock)
        {
            removed = _clients.Remove(client);
            count = _clients.Count;
        }
        client.Close();
        if (removed)
        {
            _log.LogInformation("IPC: Verbindung #{Id} beendet.", client.Id);
            ClientsChanged?.Invoke(count); // nur bei echtem Entfernen (Remove wird teils doppelt gerufen)
        }
    }

    private string DescribePeer(Stream connection)
    {
        try
        {
            return _transport.DescribePeer(connection) ?? "Peer unbekannt";
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPC: Peer einer Verbindung nicht ermittelbar.");
            return "Peer unbekannt";
        }
    }

    public ValueTask DisposeAsync()
    {
        List<Client> clients;
        lock (_lock)
        {
            clients = _clients.ToList();
            _clients.Clear();
        }
        foreach (Client c in clients)
            c.Close();

        try { _transport.Dispose(); } catch { /* egal */ } // Listener schließen + Socket-File aufräumen
        return ValueTask.CompletedTask;
    }

    private sealed class Client
    {
        public Client(Stream stream, int id, string peer)
        {
            Stream = stream;
            Id = id;
            Peer = peer;
        }

        public Stream Stream { get; }

        /// <summary>Numbers the connections of this daemon run, so the log can tie a command to its connect line.</summary>
        public int Id { get; }

        public string Peer { get; }

        /// <summary>Set from the connection's reader; read by the broadcaster.</summary>
        public volatile bool ConfigOnChange;

        /// <summary>The config revision last queued with a snapshot for this client; only the broadcaster touches it.</summary>
        public long? QueuedConfigRevision;

        /// <summary>Whether this connection's first manual PWM command is logged; only its reader touches it.</summary>
        public bool ManualLogged;

        public Channel<byte[]> Outbox { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(OutboxCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        private volatile bool _closed;

        public bool IsClosed => _closed;

        public void Close()
        {
            _closed = true;
            Outbox.Writer.TryComplete();
            try { Stream.Dispose(); } catch { /* egal */ }
        }
    }
}
