// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Ipc;
using LinFan.Ipc.Messages;

namespace LinFan.Daemon.Tests;

/// <summary>
/// In-Memory-Implementierung von <see cref="IIpcServer"/> für Unit-Tests. Erfasst alle
/// Broadcasts und ermöglicht das synchrone Feuern von Commands und Client-Änderungs-Events.
/// </summary>
internal sealed class FakeIpcServer : IIpcServer
{
    public List<IpcSnapshot> Broadcasts { get; } = new();

    public Action<IpcCommand>? CommandHandler { get; set; }

    public Action<int>? ClientsChanged { get; set; }

    public string Path => "(fake)";

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task BroadcastAsync(IpcSnapshot snapshot)
    {
        Broadcasts.Add(snapshot);
        return Task.CompletedTask;
    }

    /// <summary>Ruft den registrierten <see cref="CommandHandler"/> synchron mit dem angegebenen Kommando auf.</summary>
    public void Emit(IpcCommand c) => CommandHandler?.Invoke(c);

    /// <summary>Ruft <see cref="ClientsChanged"/> synchron mit der angegebenen Anzahl verbundener Clients auf.</summary>
    public void EmitClients(int n) => ClientsChanged?.Invoke(n);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
