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
    /// unprivilegierter Client sich mit einem privilegierten Daemon verbinden kann.
    /// </summary>
    void Listen(string endpoint);

    /// <summary>
    /// Akzeptiert die nächste Verbindung und liefert sie als Duplex-Stream (besitzt die Verbindung).
    /// Wirft <see cref="OperationCanceledException"/> bzw. <see cref="ObjectDisposedException"/>, wenn
    /// abgebrochen/disposed wird — das beendet die Accept-Schleife.
    /// </summary>
    Task<Stream> AcceptAsync(CancellationToken ct);
}
