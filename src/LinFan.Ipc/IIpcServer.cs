// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Ipc.Messages;

namespace LinFan.Ipc;

/// <summary>
/// Abstraktion des IPC-Servers, damit <c>ControlLoopService</c> ohne Abhängigkeit auf die
/// konkrete Socket-Implementierung testbar ist.
/// </summary>
public interface IIpcServer : IAsyncDisposable
{
    /// <summary>Wird für jedes empfangene Kommando aufgerufen (z. B. Config-Reload).</summary>
    Action<IpcCommand>? CommandHandler { get; set; }

    /// <summary>Wird bei jeder Verbindungs-Änderung mit der aktuellen Client-Anzahl aufgerufen.</summary>
    Action<int>? ClientsChanged { get; set; }

    /// <summary>Endpunkt, an dem gelauscht wird — Socket-Pfad bzw. Pipe-Name (für Logging).</summary>
    string Path { get; }

    /// <summary>Startet den Server und beginnt, Verbindungen zu akzeptieren.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Sendet einen Snapshot an alle verbundenen Clients.</summary>
    Task BroadcastAsync(IpcSnapshot snapshot);
}
