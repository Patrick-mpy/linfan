// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using LinFan.App.Services;
using LinFan.Ipc;
using LinFan.Ipc.Messages;

namespace LinFan.App.Tests;

/// <summary>
/// A client that records what is sent and streams one snapshot; <paramref name="reachable"/> = false: the daemon is not
/// there.
/// </summary>
internal sealed class RecordingIpcClient(bool reachable = true) : IIpcClient
{
    public List<IpcCommand> Sent { get; } = new();

    public string? ConnectedPath => null;

    /// <summary>An <see cref="IpcLiveMonitor"/> on <paramref name="client"/>, once it is connected.</summary>
    public static async Task<IpcLiveMonitor> ConnectedMonitorAsync(RecordingIpcClient client)
    {
        var monitor = new IpcLiveMonitor(() => client, TimeSpan.FromHours(1));
        var sw = Stopwatch.StartNew();
        while (!monitor.Read().Connected)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
            {
                await monitor.DisposeAsync();
                throw new TimeoutException("not connected");
            }
            await Task.Delay(10);
        }
        return monitor;
    }

    public Task ConnectAsync(CancellationToken ct = default) =>
        reachable ? Task.CompletedTask : Task.FromException(new IOException("no daemon"));

    public async IAsyncEnumerable<IpcSnapshot?> ReadSnapshotsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>());
        await Task.Delay(Timeout.Infinite, ct);
    }

    public Task SendCommandAsync(IpcCommand command, CancellationToken ct = default)
    {
        lock (Sent)
            Sent.Add(command);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
