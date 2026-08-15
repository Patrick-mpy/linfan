// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
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

    private readonly string _path;
    private readonly IIpcServerTransport _transport;
    private readonly ILogger _log;
    private readonly int _maxCommandBytes;
    private readonly TimeSpan _acceptRetryDelay;
    private readonly List<Stream> _clients = new();
    private readonly object _lock = new();

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
                       TimeSpan? acceptRetryDelay = null)
    {
        _path = path ?? IpcEndpoint.SocketPath;
        _log = log ?? NullLogger.Instance;
        _transport = transport ?? IpcTransportFactory.CreateServer(_log); // Logger für Zugriffskontrolle/Audit
        _maxCommandBytes = maxCommandBytes;
        _acceptRetryDelay = acceptRetryDelay ?? AcceptRetryDelay;
    }

    public string Path => _path;

    public Task StartAsync(CancellationToken ct)
    {
        _transport.Listen(_path);
        _ = AcceptLoopAsync(ct);
        return Task.CompletedTask;
    }

    public async Task BroadcastAsync(IpcSnapshot snapshot)
    {
        byte[] payload = IpcJson.SerializeLine(snapshot);

        List<Stream> targets;
        lock (_lock)
            targets = _clients.ToList();

        foreach (Stream client in targets)
        {
            try
            {
                await client.WriteAsync(payload);
                await client.FlushAsync();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "IPC: Broadcast an einen Client fehlgeschlagen - Client entfernt.");
                Remove(client);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // Je Fehler-Serie nur einmal warnen (und einmal je Erholung zurücksetzen): ein Dauerfehler würde
        // sonst im Sekundentakt dasselbe ins Log schreiben.
        bool failureLogged = false;

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
            int count;
            lock (_lock)
            {
                _clients.Add(client);
                count = _clients.Count;
            }
            ClientsChanged?.Invoke(count);
            _ = ReadCommandsAsync(client, ct);
        }
    }

    /// <summary>
    /// Liest NDJSON-Kommandozeilen roh aus dem Stream und begrenzt dabei die Länge einer einzelnen Zeile
    /// auf <see cref="MaxCommandBytes"/> (DoS-Schutz im privilegierten Prozess). Bewusst kein
    /// <see cref="StreamReader.ReadLineAsync()"/>: der puffert eine Zeile ohne Zeilenende unbegrenzt.
    /// </summary>
    private async Task ReadCommandsAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[4096];
            using var line = new MemoryStream();
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "IPC: Lesefehler auf der Client-Verbindung - Verbindung wird geschlossen.");
                    break;
                }

                if (read == 0)
                    break; // Verbindung sauber geschlossen

                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (b == (byte)'\n')
                    {
                        DispatchLine(line);
                        line.SetLength(0);
                    }
                    else if (line.Length >= _maxCommandBytes)
                    {
                        _log.LogWarning(
                            "IPC: Kommando überschreitet {Max} Bytes ohne Zeilenende - Verbindung getrennt (möglicher DoS).",
                            _maxCommandBytes);
                        return; // finally entfernt den Client
                    }
                    else
                    {
                        line.WriteByte(b);
                    }
                }
            }
        }
        finally
        {
            Remove(stream);
        }
    }

    /// <summary>Deserialisiert eine gepufferte NDJSON-Zeile (UTF-8) und übergibt das Kommando an den Handler.</summary>
    private void DispatchLine(MemoryStream line)
    {
        if (line.Length == 0)
            return; // Leerzeile (z. B. CRLF-Rest oder Keepalive) überspringen

        IpcCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<IpcCommand>(
                line.GetBuffer().AsSpan(0, (int)line.Length), IpcJson.Options);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPC: ungültige Kommandozeile verworfen ({Bytes} Bytes).", line.Length);
            return;
        }

        if (command is null)
            return;

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

    private void Remove(Stream client)
    {
        bool removed;
        int count;
        lock (_lock)
        {
            removed = _clients.Remove(client);
            count = _clients.Count;
        }
        try { client.Dispose(); } catch { /* egal */ }
        if (removed)
            ClientsChanged?.Invoke(count); // nur bei echtem Entfernen (Remove wird teils doppelt gerufen)
    }

    public ValueTask DisposeAsync()
    {
        List<Stream> clients;
        lock (_lock)
        {
            clients = _clients.ToList();
            _clients.Clear();
        }
        foreach (Stream c in clients)
            try { c.Dispose(); } catch { /* egal */ }

        try { _transport.Dispose(); } catch { /* egal */ } // Listener schließen + Socket-File aufräumen
        return ValueTask.CompletedTask;
    }
}
