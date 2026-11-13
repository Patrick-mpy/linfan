// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LinFan.App.Services;
using LinFan.Ipc;
using LinFan.Ipc.Messages;

namespace LinFan.App.Tests;

/// <summary>
/// A snapshot the GUI cannot read is no lost connection: <see cref="IpcLiveMonitor"/> keeps the connection, reports
/// <see cref="ConnectionProblem.UnreadableData"/> without the last values, logs the series once, and the next readable
/// snapshot ends it.
/// </summary>
public sealed class IpcLiveMonitorUnreadableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"linfan-unreadable-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Stays connected and streams what the test writes into <see cref="Snapshots"/>.</summary>
    private sealed class StreamingClient : IIpcClient
    {
        public Channel<IpcSnapshot?> Snapshots { get; } = Channel.CreateUnbounded<IpcSnapshot?>();

        public int Connects { get; private set; }

        public bool Disposed { get; private set; }

        public string? ConnectedPath => null;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            Connects++;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IpcSnapshot?> ReadSnapshotsAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (IpcSnapshot? snapshot in Snapshots.Reader.ReadAllAsync(ct))
                yield return snapshot;
        }

        public Task SendCommandAsync(IpcCommand command, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static IpcSnapshot Readable(double temp = 40) => new(DaemonStatus.Active, false, temp,
        new[] { new IpcSensor("cpu", "CPU", "Temperature", "°C", temp) }, Array.Empty<IpcFan>(), DaemonVersion: "9.9.9");

    /// <summary>A snapshot this GUI cannot convert - here a list the daemon left out; in practice another version's shape.</summary>
    private static IpcSnapshot Unreadable() => Readable() with { Sensors = null! };

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
    public async Task AnUnreadableSnapshot_KeepsTheConnection_AndReportsItsOwnProblem()
    {
        var client = new StreamingClient();
        var log = new GuiErrorLog(Path.Combine(_dir, "gui.log"));
        await using var monitor = new IpcLiveMonitor(() => client, TimeSpan.FromHours(1), log);

        client.Snapshots.Writer.TryWrite(Readable());
        await WaitUntilAsync(() => monitor.Read().Connected);
        client.Snapshots.Writer.TryWrite(Unreadable());
        await WaitUntilAsync(() => !monitor.Read().Connected);

        MonitorSnapshot read = monitor.Read();
        Assert.Equal(ConnectionProblem.UnreadableData, read.Problem); // not "unreachable" - the service is there
        Assert.Empty(read.Sensors); // the last values are not current any more
        Assert.Equal(1, client.Connects);
        Assert.False(client.Disposed); // no reconnect
    }

    [Fact]
    public async Task ALineTheClientCouldNotRead_EndsTheLastValues_LikeAnUnreadableSnapshot()
    {
        // IpcClient yields null for a line it cannot parse, one over its bound, or a config it never received - showing
        // the last snapshot on as current would hide that nothing current arrives.
        var client = new StreamingClient();
        var log = new GuiErrorLog(Path.Combine(_dir, "gui.log"));
        await using var monitor = new IpcLiveMonitor(() => client, TimeSpan.FromHours(1), log);

        client.Snapshots.Writer.TryWrite(Readable());
        await WaitUntilAsync(() => monitor.Read().Connected);
        client.Snapshots.Writer.TryWrite(null);
        await WaitUntilAsync(() => !monitor.Read().Connected);

        MonitorSnapshot read = monitor.Read();
        Assert.Equal(ConnectionProblem.UnreadableData, read.Problem);
        Assert.Empty(read.Sensors);
        Assert.Null(read.DaemonVersion); // the line named none the client could read
        Assert.Contains("[ERR] Snapshot: A snapshot from the service cannot be read (not valid for this GUI", Assert.Single(Records(log)));
        Assert.False(client.Disposed); // no reconnect

        client.Snapshots.Writer.TryWrite(Readable(55));
        await WaitUntilAsync(() => monitor.Read().Connected);
        Assert.Equal(55, Assert.Single(monitor.Read().Sensors).Value);
    }

    private static string[] Records(GuiErrorLog log) => File.ReadAllLines(log.FilePath)
        .Where(l => !l.StartsWith(' ') && l.Contains(" [ERR] ", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public async Task ASeriesOfUnreadableSnapshots_IsLoggedOnce_AndTheNextReadableOneEndsIt()
    {
        var client = new StreamingClient();
        var clock = new ManualClock();
        var log = new GuiErrorLog(Path.Combine(_dir, "gui.log"), clock);
        await using var monitor = new IpcLiveMonitor(() => client, TimeSpan.FromHours(1), log);

        client.Snapshots.Writer.TryWrite(Unreadable());
        await WaitUntilAsync(() => monitor.Read().Problem == ConnectionProblem.UnreadableData);
        // Past the log's own repeat window: only the series keeps the second one out.
        clock.Advance(GuiErrorLog.RepeatWindow + TimeSpan.FromSeconds(1));
        MonitorSnapshot first = monitor.Read();
        client.Snapshots.Writer.TryWrite(Unreadable());
        await WaitUntilAsync(() => !ReferenceEquals(monitor.Read(), first));

        string record = Assert.Single(Records(log));
        Assert.Contains("[ERR] Snapshot: A snapshot from the service cannot be read", record);
        Assert.Contains("service 9.9.9", record); // the versions point at the likely cause
        Assert.Equal("9.9.9", monitor.Read().DaemonVersion); // the banner names it too

        client.Snapshots.Writer.TryWrite(Readable(55));
        await WaitUntilAsync(() => monitor.Read().Connected);

        Assert.Equal(ConnectionProblem.None, monitor.Read().Problem);
        Assert.Equal(55, Assert.Single(monitor.Read().Sensors).Value);
        Assert.Equal(1, client.Connects);

        // A readable snapshot ended the series: the next unreadable one starts a new one, and is logged again.
        clock.Advance(GuiErrorLog.RepeatWindow + TimeSpan.FromSeconds(1));
        client.Snapshots.Writer.TryWrite(Unreadable());
        await WaitUntilAsync(() => !monitor.Read().Connected);

        Assert.Equal(2, Records(log).Length);
    }
}
