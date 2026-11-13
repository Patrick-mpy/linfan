// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using LinFan.Ipc;
using LinFan.Ipc.Transport;
using Xunit;

namespace LinFan.Ipc.Tests;

/// <summary>
/// Run-2 lead L1: every connection may hold up to the command-line limit in the root daemon. The number of connections
/// is bounded, and a line that has begun must end in time.
/// </summary>
public class IpcServerLimitsTests
{
    [Fact]
    public async Task ConnectionsBeyondTheCap_AreClosedRightAway()
    {
        var first = new ScriptedClientStream();
        var second = new ScriptedClientStream();
        var third = new ScriptedClientStream();
        var transport = new QueuedServerTransport(first, second, third);
        await using var server = new IpcServer("test-cap", transport, log: null, maxCommandBytes: 1024, maxClients: 2);

        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);
        await transport.AllAccepted.WaitAsync(TimeSpan.FromSeconds(15));
        await third.Closed.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, server.ClientCount);
        Assert.False(first.Closed.IsCompleted);
        Assert.False(second.Closed.IsCompleted);
        cts.Cancel();
    }

    [Fact]
    public async Task ALineThatIsNeverFinished_DropsTheClient()
    {
        var client = new ScriptedClientStream("{\"command\":\"rel"u8.ToArray());
        var transport = new QueuedServerTransport(client);
        await using var server = new IpcServer("test-partial", transport, log: null, maxCommandBytes: 1024,
                                               partialLineTimeout: TimeSpan.FromMilliseconds(100));
        var commands = new ConcurrentQueue<string>();
        server.CommandHandler = c => commands.Enqueue(c.Command);

        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);

        await client.Closed.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(commands);
        Assert.Equal(0, server.ClientCount);
        cts.Cancel();
    }

    [Fact]
    public async Task CompleteLines_AreNotSubjectToTheTimeout_WhileTheClientIdles()
    {
        var client = new ScriptedClientStream(Encoding.UTF8.GetBytes("{\"command\":\"reload\"}\n"));
        var transport = new QueuedServerTransport(client);
        await using var server = new IpcServer("test-idle", transport, log: null, maxCommandBytes: 1024,
                                               partialLineTimeout: TimeSpan.FromMilliseconds(50));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = _ => received.TrySetResult();

        using var cts = new CancellationTokenSource();
        await server.StartAsync(cts.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(300); // a GUI sends nothing for hours - that is no partial line

        Assert.False(client.Closed.IsCompleted);
        Assert.Equal(1, server.ClientCount);
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

    /// <summary>A client that sends the given bytes, then nothing until it is closed; it accepts every write.</summary>
    private sealed class ScriptedClientStream : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _toSend;

        public ScriptedClientStream(byte[]? toSend = null) => _toSend = toSend;

        public Task Closed => _closed.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _toSend, null) is { } bytes)
            {
                bytes.CopyTo(buffer);
                return bytes.Length;
            }
            await _closed.Task.WaitAsync(ct);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

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
