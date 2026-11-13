// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Ipc.Messages;

namespace LinFan.Ipc;

/// <summary>
/// Abstraktion des IPC-Clients (GUI-/CLI-Seite), damit die Verbraucher (<c>IpcLiveMonitor</c>,
/// CLI) ohne Abhängigkeit auf die konkrete Transport-Implementierung testbar bleiben und der
/// Transport (Unix-Socket → Named Pipe auf Windows) lokal in <see cref="Transport"/> austauschbar
/// ist, ohne GUI/CLI anzufassen.
/// </summary>
public interface IIpcClient : IAsyncDisposable
{
    /// <summary>Der Endpunkt, mit dem zuletzt erfolgreich verbunden wurde (nach <see cref="ConnectAsync"/>).</summary>
    string? ConnectedPath { get; }

    /// <summary>Verbindet der Reihe nach mit den Kandidaten (erster erreichbarer gewinnt).</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads the snapshot stream until the connection ends or is cancelled. <c>null</c> stands for a snapshot that
    /// arrived but cannot be read here (not valid for this contract, over the line bound, or without a config this
    /// connection ever received): the daemon is there, but the last snapshot read is no longer current.
    /// </summary>
    IAsyncEnumerable<IpcSnapshot?> ReadSnapshotsAsync(CancellationToken ct = default);

    /// <summary>Sendet ein Kommando an den Daemon.</summary>
    Task SendCommandAsync(IpcCommand command, CancellationToken ct = default);
}
