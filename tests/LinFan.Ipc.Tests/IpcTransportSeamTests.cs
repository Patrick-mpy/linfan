// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;
using Xunit;

namespace LinFan.Ipc.Tests;

/// <summary>
/// Beweist, dass die Protokoll-Schicht (<see cref="IpcServer"/>/<see cref="IpcClient"/>) transport-
/// neutral ist: über einen injizierten Transport, der einen <b>TCP-Loopback-Stream</b> (kein
/// Unix-Socket) liefert, gehen Snapshot- und Kommando-Strom durch. Genau diese Naht nutzt auch der
/// Windows-Named-Pipe-Transport — er muss nur einen <see cref="Stream"/> liefern, ohne
/// <c>IpcServer</c>/<c>IpcClient</c> oder die GUI anzufassen.
/// </summary>
public class IpcTransportSeamTests
{
    [Fact]
    public void Factory_OnThisPlatform_ReturnsTransports()
    {
        // Linux/macOS → Unix-Domain-Socket, Windows → Named Pipe. Auf jeder unterstützten Plattform
        // liefert die Factory beide Seiten ungleich null (kein Phase-2-Loch mehr).
        Assert.NotNull(IpcTransportFactory.CreateClient());
        Assert.NotNull(IpcTransportFactory.CreateServer());
    }

    [Fact]
    public async Task ServerAndClient_RoundTrip_OverInjectedNonUnixTransport()
    {
        (Stream serverStream, Stream clientStream) = await TcpLoopbackPairAsync();

        await using var server = new IpcServer("tcp-loopback", new FixedServerTransport(serverStream));
        var receivedCommand = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => receivedCommand.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient("tcp-loopback", new FixedClientTransport(clientStream));
        await client.ConnectAsync(cts.Token);

        // Kommando-Richtung: Client → Server.
        await client.SendCommandAsync(new IpcCommand(IpcCommand.Reload), cts.Token);
        IpcCommand command = await receivedCommand.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("reload", command.Command);

        // Snapshot-Richtung: Server → Client. Wiederholt broadcasten (Accept-Timing), bis einer ankommt.
        var sent = new IpcSnapshot(DaemonStatus.DryRun, DryRun: true, 55.0,
            new[] { new IpcSensor("s1", "CPU", "Temperature", "°C", 55.0) },
            Array.Empty<IpcFan>());

        using var stop = new CancellationTokenSource();
        var broadcaster = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await server.BroadcastAsync(sent);
                try { await Task.Delay(100, stop.Token); } catch { break; }
            }
        });

        try
        {
            await using IAsyncEnumerator<IpcSnapshot> e =
                client.ReadSnapshotsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            Assert.True(await e.MoveNextAsync());
            Assert.Equal(DaemonStatus.DryRun, e.Current.Status);
            Assert.Equal(55.0, e.Current.HottestTempC);
            Assert.Equal("CPU", Assert.Single(e.Current.Sensors).Name);
        }
        finally
        {
            stop.Cancel();
            try { await broadcaster; } catch { /* egal */ }
        }
    }

    /// <summary>Zwei verbundene Loopback-TCP-Streams — bewusst kein Unix-Socket.</summary>
    private static async Task<(Stream Server, Stream Client)> TcpLoopbackPairAsync()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;

        var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task connect = clientSocket.ConnectAsync(endpoint);
        Socket serverSocket = await listener.AcceptAsync();
        await connect;

        return (new NetworkStream(serverSocket, ownsSocket: true), new NetworkStream(clientSocket, ownsSocket: true));
    }

    /// <summary>Server-Transport, der genau eine vorgefertigte Verbindung herausgibt.</summary>
    private sealed class FixedServerTransport : IIpcServerTransport
    {
        private readonly Stream _stream;
        private int _accepted;
        public FixedServerTransport(Stream stream) => _stream = stream;
        public void Listen(string endpoint) { }
        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _accepted, 1) == 0)
                return _stream;
            await Task.Delay(Timeout.Infinite, ct); // nur ein Client; danach bis Cancel blockieren
            throw new OperationCanceledException(ct);
        }
        public void Dispose() { }
    }

    /// <summary>Client-Transport, der eine vorgefertigte Verbindung liefert.</summary>
    private sealed class FixedClientTransport : IIpcClientTransport
    {
        private readonly Stream _stream;
        public FixedClientTransport(Stream stream) => _stream = stream;
        public Task<Stream> ConnectAsync(string endpoint, CancellationToken ct) => Task.FromResult(_stream);
    }
}
