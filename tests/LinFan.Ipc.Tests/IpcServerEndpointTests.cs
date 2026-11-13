// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using System.Text;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LinFan.Ipc.Tests;

/// <summary>
/// Hardening from security audit run-2 (h1): a second daemon must not take a live daemon's socket over, and the log
/// must tie config changes and device actions to the connection that sent them.
/// </summary>
public class IpcServerEndpointTests
{
    private static string TempSocket() =>
        Path.Combine(Path.GetTempPath(), $"linfan-ipc-{Guid.NewGuid():N}.sock");

    /// <summary>The probe waits this long for a snapshot - short here, the daemon uses 10 s.</summary>
    private static readonly TimeSpan SilentAfter = TimeSpan.FromMilliseconds(500);

    private static IpcServer Server(string path, CapturingLogger? log = null) =>
        new(path, new UnixSocketServerTransport(log, SilentAfter), log);

    [Fact]
    public async Task Listen_WhereADaemonServes_Throws_AndTheFirstKeepsItsSocket()
    {
        if (!OperatingSystem.IsLinux())
            return; // Unix socket transport; the lock is Linux only

        string path = TempSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var first = Server(path);
        await first.StartAsync(cts.Token);
        var snapshot = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, [], []);
        using var stop = new CancellationTokenSource();
        Task ticking = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) // a snapshot per tick, like the daemon
            {
                await first.BroadcastAsync(snapshot);
                try { await Task.Delay(50, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });

        try
        {
            var second = Server(path);
            var ex = await Assert.ThrowsAsync<IpcEndpointInUseException>(() => second.StartAsync(cts.Token));
            Assert.Equal(path, ex.Endpoint);
            Assert.True(ex.Serving);
            await second.DisposeAsync(); // must not delete the first server's socket file

            Assert.True(File.Exists(path));
            await using var client = new IpcClient(path);
            await client.ConnectAsync(cts.Token);
            await using IAsyncEnumerator<IpcSnapshot?> e =
                client.ReadSnapshotsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            Assert.True(await e.MoveNextAsync());
            Assert.NotNull(e.Current);
            Assert.Equal(40, e.Current.HottestTempC);
        }
        finally
        {
            stop.Cancel();
            await ticking;
        }
    }

    [Fact]
    public async Task Listen_WhereADaemonHoldsTheEndpointButSendsNothing_Throws_NotServing()
    {
        // A daemon that is stopped (SIGTERM-less Ctrl+Z) or stuck: the kernel still completes a connect into its backlog.
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        using var stuck = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        stuck.Bind(new UnixDomainSocketEndPoint(path));
        stuck.Listen(4); // never accepts

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = Server(path);
        var ex = await Assert.ThrowsAsync<IpcEndpointInUseException>(() => server.StartAsync(cts.Token));
        Assert.False(ex.Serving);
    }

    [Fact]
    public async Task Listen_WhileAnotherDaemonHoldsTheLock_Throws_EvenWithoutItsSocketFile()
    {
        // Two daemons starting at the same moment: the one that took the lock may not have bound yet.
        if (!OperatingSystem.IsLinux())
            return;

        string path = TempSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var first = Server(path);
        await first.StartAsync(cts.Token);
        File.Delete(path);

        await using var second = Server(path);
        var ex = await Assert.ThrowsAsync<IpcEndpointInUseException>(() => second.StartAsync(cts.Token));
        Assert.False(ex.Serving);
    }

    [Fact]
    public async Task TheLockFile_IsGoneWithTheServer_AndOwnerOnly()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string path = TempSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Server(path);
        await server.StartAsync(cts.Token);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + ".lock"));

        await server.DisposeAsync();
        Assert.False(File.Exists(path + ".lock"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Listen_OverTheSocketFileOfADeadServer_TakesItOver()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        string bound = TempSocket();
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(bound));
            dead.Listen(1);
            File.Move(bound, path); // .NET unlinks the path it bound on dispose; a killed daemon leaves its file
        }
        Assert.True(File.Exists(path));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new IpcServer(path);
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
    }

    [Fact]
    public void IsEndpointHeld_ByAHolderThatSendsNothing()
    {
        // The CLI's check before a fan write: a stopped or stuck daemon holds the endpoint as much as a serving one.
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        using var stuck = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        stuck.Bind(new UnixDomainSocketEndPoint(path));
        stuck.Listen(4); // never accepts, never sends

        Assert.True(IpcTransportFactory.IsEndpointHeld(path));
    }

    [Fact]
    public void IsEndpointHeld_NotByTheSocketFileOfADeadServer_NorWithoutAFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        Assert.False(IpcTransportFactory.IsEndpointHeld(path));

        string bound = TempSocket();
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(bound));
            dead.Listen(1);
            File.Move(bound, path); // .NET unlinks the path it bound on dispose; a killed daemon leaves its file
        }
        try
        {
            Assert.True(File.Exists(path));
            Assert.False(IpcTransportFactory.IsEndpointHeld(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Socket_OfAUserDaemon_IsCreatedOwnerOnly()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            return; // root takes the group branch (0660, group linfan)

        string path = TempSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new IpcServer(path);
        await server.StartAsync(cts.Token);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task AConfigChange_IsLoggedWithTheConnectionAndItsPeer_AndConfigOnChangeIsNoDaemonCommand()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        var log = new CapturingLogger();
        await using var server = new IpcServer(path, transport: null, log: log);
        var handled = new List<string>();
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd =>
        {
            lock (handled)
                handled.Add(cmd.Command);
            if (cmd.Command == IpcCommand.ResetConfig)
                reset.TrySetResult();
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);
        await using var client = new IpcClient(path); // asks for configOnChange on connect
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.SetManualPwm, Target: "fan", Value: 1), cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.SetManualPwm, Target: "fan", Value: 2), cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.ForgetHardware, Target: "missing-0"), cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.ResetConfig), cts.Token);
        await reset.Task.WaitAsync(TimeSpan.FromSeconds(15));

        lock (handled)
            Assert.Equal([IpcCommand.SetManualPwm, IpcCommand.SetManualPwm, IpcCommand.ForgetHardware, IpcCommand.ResetConfig], handled);

        string peer = OperatingSystem.IsLinux() ? $"uid={UnixUid()}" : "Peer unbekannt";
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("Verbindung #1 angenommen") && e.Message.Contains(peer));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("resetConfig von Verbindung #1") && e.Message.Contains(peer));
        Assert.Contains(log.Entries, e => e.Message.Contains("forgetHardware von Verbindung #1")); // removes entries
        // A slider sends manual PWM many times a second - only the first one names who took a fan over by hand.
        Assert.Single(log.Entries, e => e.Message.Contains("setManualPwm von Verbindung #1"));
    }

    [Fact]
    public async Task AnUnknownCommand_IsNotLoggedByName()
    {
        // The command text comes from the peer: only names from the audited set may reach the log.
        if (OperatingSystem.IsWindows())
            return;

        string path = TempSocket();
        var log = new CapturingLogger();
        await using var server = new IpcServer(path, transport: null, log: log);
        var handled = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => handled.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);
        using var raw = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await raw.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token);
        await raw.SendAsync(Encoding.UTF8.GetBytes("{\"command\":\"forged\\r\\nline\"}\n"), cts.Token);

        await handled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("forged"));
    }

    private static string UnixUid() => File.ReadLines("/proc/self/status")
        .First(l => l.StartsWith("Uid:", StringComparison.Ordinal))
        .Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
}
