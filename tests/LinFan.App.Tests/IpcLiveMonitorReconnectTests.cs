// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using LinFan.App.Services;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// The connection state the GUI reads from <see cref="IpcLiveMonitor"/>, and "Reconnect": with a retry delay of an
/// hour, a test only passes if the request really cuts the wait short.
/// </summary>
public sealed class IpcLiveMonitorReconnectTests
{
    private static readonly TimeSpan NoRetry = TimeSpan.FromHours(1);

    /// <summary>One connection attempt: <paramref name="connect"/> decides its outcome; once connected it streams one snapshot.</summary>
    private sealed class FakeClient(Func<CancellationToken, Task> connect) : IIpcClient
    {
        public string? ConnectedPath => null;

        public Task ConnectAsync(CancellationToken ct = default) => connect(ct);

        public async IAsyncEnumerable<IpcSnapshot?> ReadSnapshotsAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new IpcSnapshot(DaemonStatus.Active, false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>());
            await Task.Delay(Timeout.Infinite, ct); // stays connected
        }

        public Task SendCommandAsync(IpcCommand command, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Hands out one client per attempt, in order, and counts the attempts.</summary>
    private sealed class Attempts(params Func<CancellationToken, Task>[] outcomes)
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IIpcClient Next()
        {
            int i = Interlocked.Increment(ref _count) - 1;
            return new FakeClient(outcomes[Math.Min(i, outcomes.Length - 1)]);
        }
    }

    private static Task Fail(Exception ex) => Task.FromException(ex);
    private static Task Succeed(CancellationToken _) => Task.CompletedTask;
    private static Task Hang(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task BeforeTheFirstOutcome_ItIsConnecting_NotAFailure()
    {
        var attempts = new Attempts(Hang);
        await using var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);

        MonitorSnapshot snap = monitor.Read();

        Assert.False(snap.Connected);
        Assert.Equal(ConnectionProblem.Connecting, snap.Problem);
    }

    public static TheoryData<Exception, ConnectionProblem> Failures => new()
    {
        { new SocketException((int)SocketError.ConnectionRefused), ConnectionProblem.Unreachable },
        { new IOException("no daemon"), ConnectionProblem.Unreachable },
        { new SocketException((int)SocketError.AccessDenied), ConnectionProblem.AccessDenied },
        { new UnauthorizedAccessException(), ConnectionProblem.AccessDenied },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedAttempt_ReportsWhy(Exception failure, ConnectionProblem expected)
    {
        var attempts = new Attempts(_ => Fail(failure));
        await using var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);

        await WaitUntilAsync(() => monitor.Read().Problem != ConnectionProblem.Connecting);

        Assert.False(monitor.Read().Connected);
        Assert.Equal(expected, monitor.Read().Problem);
    }

    [Fact]
    public async Task RequestReconnect_CutsTheRetryDelayShort_AndSettlesOnceTheSnapshotShowsTheOutcome()
    {
        var attempts = new Attempts(_ => Fail(new IOException("no daemon")), Succeed);
        await using var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);
        await WaitUntilAsync(() => monitor.Read().Problem == ConnectionProblem.Unreachable);

        bool connected = await monitor.RequestReconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(connected);
        Assert.Equal(2, attempts.Count);
        Assert.True(monitor.Read().Connected);
        Assert.Equal(ConnectionProblem.None, monitor.Read().Problem);
    }

    [Fact]
    public async Task RequestReconnect_ThatFailsAgain_SettlesWithTheFailure()
    {
        var attempts = new Attempts(_ => Fail(new IOException("no daemon")));
        await using var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);
        await WaitUntilAsync(() => monitor.Read().Problem == ConnectionProblem.Unreachable);

        bool connected = await monitor.RequestReconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(connected);
        Assert.Equal(2, attempts.Count); // one more attempt, then the hour-long delay again - no loop
        Assert.Equal(ConnectionProblem.Unreachable, monitor.Read().Problem);
    }

    [Fact]
    public async Task RequestReconnect_DuringTheFirstAttempt_WaitsForIt_AndLeavesTheRetryDelayIntact()
    {
        var gate = new TaskCompletionSource();
        var attempts = new Attempts(_ => gate.Task, _ => Fail(new IOException("no daemon")));
        await using var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);

        Task<bool> request = monitor.RequestReconnectAsync(); // still Connecting
        gate.SetException(new IOException("no daemon"));
        Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(200); // room for a wrongly woken retry

        Assert.Equal(1, attempts.Count);
        Assert.Equal(ConnectionProblem.Unreachable, monitor.Read().Problem);
    }

    [Fact]
    public async Task RequestReconnect_Twice_StartsOneAttempt()
    {
        var gate = new TaskCompletionSource();
        var attempts = new Attempts(_ => Fail(new IOException("no daemon")), _ => gate.Task, _ => Fail(new IOException("no daemon")));
        await using var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);
        await WaitUntilAsync(() => monitor.Read().Problem == ConnectionProblem.Unreachable);

        Task<bool> first = monitor.RequestReconnectAsync();
        await WaitUntilAsync(() => attempts.Count == 2);
        Task<bool> second = monitor.RequestReconnectAsync(); // a double click while that attempt runs
        gate.SetException(new IOException("no daemon"));
        bool[] outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);

        Assert.Equal(2, attempts.Count);
        Assert.Equal(new[] { false, false }, outcomes); // both learn the one attempt's outcome
    }

    [Fact]
    public async Task RequestReconnect_WhileConnected_CompletesAtOnce_WithoutAnAttempt()
    {
        var attempts = new Attempts(Succeed);
        await using var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);
        await WaitUntilAsync(() => monitor.Read().Connected);

        Task<bool> request = monitor.RequestReconnectAsync();

        Assert.True(request.IsCompleted);
        Assert.True(await request);
        Assert.Equal(1, attempts.Count);
    }

    [Fact]
    public async Task Dispose_ReleasesARequestWhoseAttemptNeverEnds()
    {
        var attempts = new Attempts(_ => Fail(new IOException("no daemon")), Hang);
        var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);
        await WaitUntilAsync(() => monitor.Read().Problem == ConnectionProblem.Unreachable);
        Task<bool> request = monitor.RequestReconnectAsync();
        await WaitUntilAsync(() => attempts.Count == 2);

        await monitor.DisposeAsync();

        Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(10))); // a closing window must not wait forever
    }

    [Fact]
    public async Task RequestReconnect_AfterDispose_ReportsNotConnected()
    {
        var attempts = new Attempts(Succeed);
        var monitor = new IpcLiveMonitor(attempts.Next, NoRetry);
        await WaitUntilAsync(() => monitor.Read().Connected);

        await monitor.DisposeAsync();

        // The last snapshot still says connected, but once the loop has stopped no connection is left to speak of.
        await WaitUntilAsync(() => monitor.RequestReconnectAsync() is { IsCompleted: true, Result: false });
        Assert.True(monitor.Read().Connected);
    }
}
