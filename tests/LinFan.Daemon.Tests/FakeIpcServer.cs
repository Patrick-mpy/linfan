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
    private readonly object _gate = new();
    private readonly List<IpcSnapshot> _broadcasts = new();
    private volatile int _clientCount = 1;

    /// <summary>
    /// A copy of the snapshots broadcast so far: the service's loop thread keeps adding while a test reads, so a test
    /// must never enumerate the live list.
    /// </summary>
    public List<IpcSnapshot> Broadcasts
    {
        get
        {
            lock (_gate) return _broadcasts.ToList();
        }
    }

    public Action<IpcCommand>? CommandHandler { get; set; }

    public Action<int>? ClientsChanged { get; set; }

    /// <summary>One client by default: a test that sends commands stands for a connected GUI.</summary>
    public int ClientCount => _clientCount;

    public string Path => "(fake)";

    /// <summary>Thrown by <see cref="StartAsync"/>, synchronously like the real server's listen.</summary>
    public Exception? StartFailure { get; set; }

    public Task StartAsync(CancellationToken ct) => StartFailure is { } failure ? throw failure : Task.CompletedTask;

    /// <summary>Runs on the broadcasting thread for every snapshot, before it is recorded.</summary>
    public Action<IpcSnapshot>? OnBroadcast { get; set; }

    public Task BroadcastAsync(IpcSnapshot snapshot)
    {
        OnBroadcast?.Invoke(snapshot);
        lock (_gate) _broadcasts.Add(snapshot);
        return Task.CompletedTask;
    }

    /// <summary>Ruft den registrierten <see cref="CommandHandler"/> synchron mit dem angegebenen Kommando auf.</summary>
    public void Emit(IpcCommand c) => CommandHandler?.Invoke(c);

    /// <summary>Setzt die Client-Anzahl und ruft <see cref="ClientsChanged"/> synchron damit auf.</summary>
    public void EmitClients(int n)
    {
        _clientCount = n;
        ClientsChanged?.Invoke(n);
    }

    /// <summary>Sets the client count without the change event - a disconnect whose notification raced a command.</summary>
    public void SetClientCountSilently(int n) => _clientCount = n;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
