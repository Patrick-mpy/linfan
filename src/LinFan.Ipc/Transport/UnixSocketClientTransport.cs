// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;

namespace LinFan.Ipc.Transport;

/// <summary>
/// Client-Transport über einen Unix-Domain-Socket (Linux/macOS). Der Endpunkt ist ein Dateipfad.
/// </summary>
internal sealed class UnixSocketClientTransport : IIpcClientTransport
{
    public async Task<Stream> ConnectAsync(string endpoint, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }
}
