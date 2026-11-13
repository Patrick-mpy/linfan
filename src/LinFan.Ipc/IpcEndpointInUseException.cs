// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc;

/// <summary>
/// Another daemon holds the endpoint. The caller must not take it over: the fans are that daemon's. Whether it still
/// serves (<see cref="Serving"/>) or holds the endpoint without sending anything - stopped, stuck, still starting -
/// decides whether the fans may stay as it left them.
/// </summary>
public sealed class IpcEndpointInUseException : IOException
{
    public IpcEndpointInUseException(string endpoint, bool serving)
        : base(serving
            ? $"Am IPC-Endpunkt {endpoint} läuft bereits ein Daemon."
            : $"Den IPC-Endpunkt {endpoint} hält ein Prozess, der nichts sendet.")
    {
        Endpoint = endpoint;
        Serving = serving;
    }

    public string Endpoint { get; }

    /// <summary>The other daemon sent its snapshot (or closed the probe at its client cap): it runs.</summary>
    public bool Serving { get; }
}
