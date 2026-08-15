// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;

namespace LinFan.Ipc.Transport;

/// <summary>
/// Client-Transport über eine Windows-Named-Pipe; der Endpunkt ist der reine Pipe-Name (Server ist
/// stets der lokale Rechner <c>"."</c>). Schlägt nach einem kurzen Timeout fehl, wenn kein Daemon
/// lauscht - so kann die Kandidaten-/Reconnect-Logik greifen, statt unbegrenzt zu blockieren (Parität
/// zum Unix-Transport, der bei fehlendem Socket sofort wirft).
/// </summary>
internal sealed class NamedPipeClientTransport : IIpcClientTransport
{
    private const int ConnectTimeoutMs = 2000;

    public async Task<Stream> ConnectAsync(string endpoint, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(
            ".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(ConnectTimeoutMs, ct);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }
}
