// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace LinFan.Ipc.Transport;

/// <summary>
/// Client-Transport über eine Windows-Named-Pipe; der Endpunkt ist der reine Pipe-Name (Server ist
/// stets der lokale Rechner <c>"."</c>). Schlägt nach einem kurzen Timeout fehl, wenn kein Daemon
/// lauscht - so kann die Kandidaten-/Reconnect-Logik greifen, statt unbegrenzt zu blockieren (Parität
/// zum Unix-Transport, der bei fehlendem Socket sofort wirft).
/// <para>
/// <b>Server identity:</b> while the service is stopped or restarting, any local account can create a pipe of the
/// same name. The client therefore lets the server only <i>identify</i> it, never impersonate it, and after
/// connecting checks who owns the pipe: SYSTEM, the administrators or the current user (a dry-run daemon started
/// from the same account). Anything else is treated like "no daemon" - the GUI keeps retrying and neither shows
/// that server's data nor sends it a config.
/// </para>
/// </summary>
internal sealed class NamedPipeClientTransport : IIpcClientTransport
{
    private const int ConnectTimeoutMs = 2000;

    public async Task<Stream> ConnectAsync(string endpoint, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(
            ".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await client.ConnectAsync(ConnectTimeoutMs, ct);
            if (OperatingSystem.IsWindows())
                VerifyServerOwner(client, endpoint);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyServerOwner(NamedPipeClientStream client, string endpoint)
    {
        SecurityIdentifier? owner;
        try
        {
            owner = PipeOwner.Of(client);
        }
        catch (UnauthorizedAccessException ex)
        {
            // A pipe that hides its owner is not the daemon's (that one grants its clients READ_CONTROL).
            throw new IOException($"Pipe '{endpoint}' does not reveal its owner - refusing to talk to it.", ex);
        }
        if (!PipeOwner.IsTrusted(owner))
        {
            // IOException, not UnauthorizedAccessException: the GUI reads the latter as "you are not in the group".
            throw new IOException(
                $"Pipe '{endpoint}' is owned by {owner?.Value ?? "nobody"}, not by SYSTEM or the administrators - "
                + "refusing to talk to it.");
        }
    }
}
