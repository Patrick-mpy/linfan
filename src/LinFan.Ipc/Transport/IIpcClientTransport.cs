// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Transport;

/// <summary>
/// Transport-Naht der Client-Seite: stellt zu einem Endpunkt eine Duplex-Verbindung her und liefert
/// sie als <see cref="Stream"/>. Die Protokoll-Schicht (NDJSON in <see cref="IpcClient"/>) kennt nur
/// den Stream - ob darunter ein Unix-Domain-Socket (Linux/macOS) oder eine Named Pipe (Windows)
/// liegt, entscheidet die konkrete Implementierung (<see cref="IpcTransportFactory"/>).
/// </summary>
public interface IIpcClientTransport
{
    /// <summary>
    /// Verbindet mit genau einem Endpunkt und liefert einen Duplex-Stream, der die Verbindung
    /// besitzt (Dispose schließt sie). Wirft, wenn der Endpunkt nicht erreichbar ist.
    /// </summary>
    Task<Stream> ConnectAsync(string endpoint, CancellationToken ct);
}
