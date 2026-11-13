// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Transport;

/// <summary>
/// Transport-Naht der Server-Seite: bindet einen Listener am Endpunkt und akzeptiert Verbindungen,
/// die als Duplex-<see cref="Stream"/> herauskommen. Die Protokoll-Schicht (NDJSON in
/// <see cref="IpcServer"/>) bleibt transport-neutral; plattform-spezifische Belange (Socket-Datei,
/// Zugriffsrechte für unprivilegierte Clients) leben in der konkreten Implementierung.
/// </summary>
public interface IIpcServerTransport : IDisposable
{
    /// <summary>
    /// Bindet/erzeugt den Listener am Endpunkt und setzt die nötigen Zugriffsrechte, damit ein
    /// unprivilegierter Client sich mit einem privilegierten Daemon verbinden kann. Throws
    /// <see cref="IpcEndpointInUseException"/> where it can tell that another server still answers there (Unix socket).
    /// </summary>
    void Listen(string endpoint);

    /// <summary>
    /// Like <see cref="Listen(string)"/>, for a transport whose start can wait (a probe of the endpoint's current
    /// holder): the token ends that wait when the host stops meanwhile.
    /// </summary>
    void Listen(string endpoint, CancellationToken ct) => Listen(endpoint);

    /// <summary>
    /// Akzeptiert die nächste Verbindung und liefert sie als Duplex-Stream (besitzt die Verbindung).
    /// Wirft <see cref="OperationCanceledException"/> bzw. <see cref="ObjectDisposedException"/>, wenn
    /// abgebrochen/disposed wird - das beendet die Accept-Schleife.
    /// </summary>
    Task<Stream> AcceptAsync(CancellationToken ct);

    /// <summary>
    /// Who is on the other end of a connection this transport accepted, for the audit trail (Linux: uid, gid and pid of
    /// the peer); <c>null</c> when the transport cannot tell.
    /// </summary>
    string? DescribePeer(Stream connection) => null;
}
