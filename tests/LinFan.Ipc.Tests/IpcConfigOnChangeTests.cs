// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;
using Xunit;

namespace LinFan.Ipc.Tests;

/// <summary>
/// The daemon's config travels only when it changed (<see cref="IpcCommand.ConfigOnChange"/>): it used to go out with
/// every snapshot - up to 4 MiB, serialized on the control loop each tick. <see cref="IpcClient"/> puts it back in.
/// </summary>
public class IpcConfigOnChangeTests
{
    private static readonly IpcConfig ConfigA = new([], [new IpcFanAssignment("fan", "Front", 0)], [], [], null);
    private static readonly IpcConfig ConfigB = new([], [new IpcFanAssignment("fan", "Rear", 0)], [], [], null);

    private static IpcSnapshot Snapshot(IpcConfig? config, long? revision, double temp = 40) =>
        new(DaemonStatus.Active, DryRun: false, temp, [], [], config, ConfigRevision: revision);

    private static string TempSocket() =>
        Path.Combine(Path.GetTempPath(), $"linfan-ipc-{Guid.NewGuid():N}.sock");

    [Fact]
    public async Task AClientThatAskedForIt_GetsTheConfigOnlyWithANewRevision()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new IpcServer(path);
        await server.StartAsync(cts.Token);
        using var raw = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await raw.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token);
        await AcceptedAsync(server, cts.Token);
        var reader = new LineReader(raw);
        await raw.SendAsync(IpcJson.SerializeLine(new IpcCommand(IpcCommand.ConfigOnChange)), cts.Token);

        // Until the server has read the request, every snapshot still carries the config.
        JsonElement line;
        do
        {
            await server.BroadcastAsync(Snapshot(ConfigA, 1));
            line = await reader.NextAsync(cts.Token);
        }
        while (HasConfig(line));
        Assert.Equal(1, line.GetProperty("configRevision").GetInt64());

        await server.BroadcastAsync(Snapshot(ConfigA, 1));
        Assert.False(HasConfig(await reader.NextAsync(cts.Token)));

        await server.BroadcastAsync(Snapshot(ConfigB, 2));
        JsonElement changed = await reader.NextAsync(cts.Token);
        Assert.True(HasConfig(changed));
        Assert.Equal(2, changed.GetProperty("configRevision").GetInt64());

        await server.BroadcastAsync(Snapshot(ConfigB, 2));
        Assert.False(HasConfig(await reader.NextAsync(cts.Token)));
    }

    [Fact]
    public async Task AClientThatDidNotAsk_GetsTheConfigWithEverySnapshot()
    {
        // An older GUI still running after the daemon was upgraded would read a missing config as an empty one.
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new IpcServer(path);
        await server.StartAsync(cts.Token);
        using var raw = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await raw.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token);
        await AcceptedAsync(server, cts.Token);
        var reader = new LineReader(raw);

        for (int i = 0; i < 5; i++)
        {
            await server.BroadcastAsync(Snapshot(ConfigA, 1));
            Assert.True(HasConfig(await reader.NextAsync(cts.Token)));
        }
    }

    [Fact]
    public async Task IpcClient_SeesTheConfigInEverySnapshot()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new IpcServer(path);
        await server.StartAsync(cts.Token);
        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await AcceptedAsync(server, cts.Token); // otherwise the first broadcasts reach nobody

        using var stop = new CancellationTokenSource();
        var broadcaster = Task.Run(async () =>
        {
            for (int tick = 0; !stop.IsCancellationRequested; tick++)
            {
                await server.BroadcastAsync(tick < 10 ? Snapshot(ConfigA, 1, tick) : Snapshot(ConfigB, 2, tick));
                try { await Task.Delay(20, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });

        var revisions = new HashSet<long?>();
        try
        {
            await foreach (IpcSnapshot? snapshot in client.ReadSnapshotsAsync(cts.Token))
            {
                Assert.NotNull(snapshot); // a healthy stream has no line the client cannot read
                Assert.NotNull(snapshot.Config);
                Assert.Equal(snapshot.ConfigRevision == 1 ? "Front" : "Rear", Assert.Single(snapshot.Config.Fans).Name);
                revisions.Add(snapshot.ConfigRevision);
                if (snapshot.HottestTempC >= 14)
                    break;
            }
        }
        finally
        {
            stop.Cancel();
            await broadcaster;
        }
        Assert.Equal([1L, 2L], revisions.Order());
    }

    [Fact]
    public async Task IpcClient_FillsInTheLastConfig_OnlyForTheSameRevision()
    {
        var wire = ScriptedStream.Of(
            Snapshot(ConfigA, 1, 1), Snapshot(null, 1, 2), Snapshot(null, 2, 3), Snapshot(ConfigB, 2, 4),
            Snapshot(null, 2, 5), Snapshot(null, null, 6));
        await using var client = new IpcClient("test", new FixedClientTransport(wire));
        await client.ConnectAsync();

        var received = new List<IpcSnapshot?>();
        await foreach (IpcSnapshot? snapshot in client.ReadSnapshotsAsync())
            received.Add(snapshot);

        // Revision 2 without its config is no snapshot to show: readers would take the missing config for an empty one.
        Assert.Equal([1.0, 2, null, 4, 5, 6], received.Select(s => s?.HottestTempC));
        string?[] expected = ["Front", "Front", null, "Rear", "Rear", null];
        Assert.Equal(expected, received.Select(s => s?.Config?.Fans.Single().Name));
        Assert.Contains("configOnChange", wire.Written);
    }

    [Fact]
    public async Task IpcClient_WhoseFirstConfigLineWasTooLong_ReportsEverySnapshotAsUnreadable()
    {
        // The server then sends the revision without its config - the client must not pass that on as an empty one, nor
        // stay silent: its reader would keep showing an older snapshot as current.
        var huge = new IpcConfig([], [new IpcFanAssignment("fan", new string('x', 4096), 0)], [], [], null);
        var wire = ScriptedStream.Of(Snapshot(huge, 1, 1), Snapshot(null, 1, 2), Snapshot(null, 1, 3));
        await using var client = new IpcClient("test", new FixedClientTransport(wire), maxSnapshotBytes: 1024);
        await client.ConnectAsync();

        var received = new List<IpcSnapshot?>();
        await foreach (IpcSnapshot? snapshot in client.ReadSnapshotsAsync())
            received.Add(snapshot);

        Assert.Equal([null, null, null], received);
    }

    /// <summary>A connect completes in the listen backlog; broadcasts reach the client only once it is accepted.</summary>
    private static async Task AcceptedAsync(IpcServer server, CancellationToken ct)
    {
        while (server.ClientCount == 0)
            await Task.Delay(10, ct);
    }

    private static bool HasConfig(JsonElement snapshot) =>
        snapshot.TryGetProperty("config", out JsonElement config) && config.ValueKind != JsonValueKind.Null;

    /// <summary>Reads NDJSON lines from a raw socket.</summary>
    private sealed class LineReader(Socket socket)
    {
        private readonly List<byte> _pending = new();
        private readonly byte[] _buffer = new byte[64 * 1024];

        public async Task<JsonElement> NextAsync(CancellationToken ct)
        {
            while (true)
            {
                int end = _pending.IndexOf((byte)'\n');
                if (end >= 0)
                {
                    byte[] line = _pending.GetRange(0, end).ToArray();
                    _pending.RemoveRange(0, end + 1);
                    return JsonDocument.Parse(line).RootElement.Clone();
                }
                int read = await socket.ReceiveAsync(_buffer, ct);
                if (read == 0)
                    throw new EndOfStreamException();
                _pending.AddRange(_buffer.AsSpan(0, read).ToArray());
            }
        }
    }
}
