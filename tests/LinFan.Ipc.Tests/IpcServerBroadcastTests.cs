// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;
using Xunit;

namespace LinFan.Ipc.Tests;

/// <summary>
/// Regression for security audit run-2 #1: broadcasting runs on the daemon's control loop, and a client
/// that stopped reading used to block it in WriteAsync - freezing the loop and the over-temperature
/// watchdog for as long as the connection was held.
/// </summary>
public class IpcServerBroadcastTests
{
    private static readonly IpcSnapshot Snapshot = new(DaemonStatus.Active, DryRun: false, HottestTempC: 42.0, [], []);

    [Fact]
    public async Task Broadcast_ToClientThatNeverReads_NeverBlocks_AndDropsTheClient()
    {
        var stalled = new FakeClientStream(blockWrites: true);
        var transport = new QueuedServerTransport(stalled);
        await using var server = new IpcServer("test-broadcast-stall", transport, log: null, maxCommandBytes: 1024);
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientsChanged = count =>
        {
            if (count == 0)
                dropped.TrySetResult();
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);
        await transport.AllAccepted.WaitAsync(TimeSpan.FromSeconds(15));

        for (int i = 0; i < 20; i++)
            Assert.True(server.BroadcastAsync(Snapshot).IsCompleted, $"Broadcast {i} waited on a stalled client.");

        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(stalled.Disposed);
        cts.Cancel();
    }

    [Fact]
    public async Task Broadcast_WriteExceedingTimeout_DropsTheClient()
    {
        var stalled = new FakeClientStream(blockWrites: true);
        var transport = new QueuedServerTransport(stalled);
        await using var server = new IpcServer("test-broadcast-timeout", transport, log: null, maxCommandBytes: 1024,
                                               writeTimeout: TimeSpan.FromMilliseconds(100));
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientsChanged = count =>
        {
            if (count == 0)
                dropped.TrySetResult();
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);
        await transport.AllAccepted.WaitAsync(TimeSpan.FromSeconds(15));

        await server.BroadcastAsync(Snapshot).WaitAsync(TimeSpan.FromSeconds(5)); // one snapshot, far below the outbox capacity

        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(stalled.Disposed);
        cts.Cancel();
    }

    [Fact]
    public async Task Broadcast_StalledClient_DoesNotStarveAHealthyClient()
    {
        var stalled = new FakeClientStream(blockWrites: true);
        var healthy = new FakeClientStream(blockWrites: false);
        var transport = new QueuedServerTransport(stalled, healthy);
        await using var server = new IpcServer("test-broadcast-mixed", transport, log: null, maxCommandBytes: 1024);

        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);
        await transport.AllAccepted.WaitAsync(TimeSpan.FromSeconds(15));

        const int broadcasts = 20;
        for (int i = 0; i < broadcasts; i++)
        {
            await server.BroadcastAsync(Snapshot).WaitAsync(TimeSpan.FromSeconds(5));
            await healthy.WaitForWritesAsync(i + 1).WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.True(stalled.Disposed);
        Assert.False(healthy.Disposed);
        cts.Cancel();
    }

    /// <summary>Hands out the given streams one by one, then blocks like a listener with no pending clients.</summary>
    private sealed class QueuedServerTransport : IIpcServerTransport
    {
        private readonly ConcurrentQueue<Stream> _streams;
        private readonly TaskCompletionSource _allAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QueuedServerTransport(params Stream[] streams) => _streams = new ConcurrentQueue<Stream>(streams);

        public Task AllAccepted => _allAccepted.Task;

        public void Listen(string endpoint) { }

        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            if (_streams.TryDequeue(out Stream? stream))
                return stream;

            _allAccepted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// A client connection that never sends a command. With <c>blockWrites</c> it also never accepts data,
    /// like a peer whose socket buffer is full; pending operations end only when the stream is disposed.
    /// </summary>
    private sealed class FakeClientStream : Stream
    {
        private readonly bool _blockWrites;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        private TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeClientStream(bool blockWrites) => _blockWrites = blockWrites;

        public bool Disposed => _closed.Task.IsCompleted;

        public async Task WaitForWritesAsync(int count)
        {
            while (true)
            {
                // Grab the signal before checking the count, so a write in between cannot be missed.
                Task signal = Volatile.Read(ref _written).Task;
                if (Volatile.Read(ref _writes) >= count)
                    return;
                await signal;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _closed.Task.WaitAsync(ct);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_blockWrites)
            {
                await _closed.Task;
                throw new ObjectDisposedException(nameof(FakeClientStream));
            }

            Interlocked.Increment(ref _writes);
            Interlocked.Exchange(ref _written, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
