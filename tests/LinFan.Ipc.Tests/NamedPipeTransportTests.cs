// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Ipc;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;
using Xunit;
using Xunit.Sdk;

namespace LinFan.Ipc.Tests;

/// <summary>
/// End-to-End über den Windows-Named-Pipe-Transport, durch die echte Protokoll-Schicht
/// (<see cref="IpcServer"/>/<see cref="IpcClient"/>). .NET emuliert Named Pipes auf Linux/macOS über
/// Unix-Domain-Sockets, daher läuft dieser Test überall - Connect/Accept/Duplex/NDJSON werden hier
/// verifiziert. Die Windows-spezifische DACL ist nicht testbar und wird am MVP-Gate (Stage 4) auf
/// echter Hardware geprüft. Der Transport wird explizit injiziert (Factory-Default wäre hier Unix).
/// </summary>
public class NamedPipeTransportTests
{
    // Upper bound per test, not a performance statement: it is only there so a hung connect fails the
    // run instead of hanging it. Generous on purpose - `dotnet test` runs the test projects of the
    // solution in parallel, and under that load connect/accept plus the first snapshot occasionally
    // needed more than the 15 s this used to allow, which failed the run without anything being wrong.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static string PipeName() => $"linfan-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task NamedPipe_Server_Broadcasts_Client_Receives()
    {
        string name = PipeName();
        await using var server = new IpcServer(name, new NamedPipeServerTransport());
        using var cts = new CancellationTokenSource(Timeout);
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(name, new NamedPipeClientTransport());
        await client.ConnectAsync(cts.Token);

        var sent = new IpcSnapshot(DaemonStatus.DryRun, DryRun: true, 55.0,
            new[] { new IpcSensor("s1", "CPU", "Temperature", "°C", 55.0) },
            new[] { new IpcFan("f1", "Fan", 1200, 128, "Manual", true) });

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
            IpcSnapshot received = e.Current;
            Assert.Equal(DaemonStatus.DryRun, received.Status);
            Assert.Equal(128, Assert.Single(received.Fans).Pwm);
            Assert.Equal("CPU", Assert.Single(received.Sensors).Name);
        }
        finally
        {
            stop.Cancel();
            try { await broadcaster; } catch { /* egal */ }
        }
    }

    [Fact]
    public async Task NamedPipe_Client_SendsCommand_Server_Receives()
    {
        string name = PipeName();
        await using var server = new IpcServer(name, new NamedPipeServerTransport());
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(Timeout);
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(name, new NamedPipeClientTransport());
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.SetManualPwm, Target: "fan1", Value: 200), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(Timeout);
        Assert.Equal("setManualPwm", command.Command);
        Assert.Equal("fan1", command.Target);
        Assert.Equal(200, command.Value);
    }

    [Fact]
    public async Task NamedPipe_Client_Connect_Fails_When_NoServer()
    {
        await using var client = new IpcClient(PipeName(), new NamedPipeClientTransport());
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task NamedPipe_MultipleClients_ConnectConcurrently()
    {
        string name = PipeName();
        await using var server = new IpcServer(name, new NamedPipeServerTransport());
        int accepted = 0;
        server.ClientsChanged += n => accepted = n;
        using var cts = new CancellationTokenSource(Timeout);
        await server.StartAsync(cts.Token);

        // Zwei Clients gleichzeitig - pro Verbindung erzeugt der Transport eine eigene Pipe-Instanz.
        await using var c1 = new IpcClient(name, new NamedPipeClientTransport());
        await using var c2 = new IpcClient(name, new NamedPipeClientTransport());
        await c1.ConnectAsync(cts.Token);
        await c2.ConnectAsync(cts.Token);

        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40.0,
            Array.Empty<IpcSensor>(), Array.Empty<IpcFan>());

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
            int index = 0;
            foreach (IpcClient c in new[] { c1, c2 })
            {
                index++;
                await using IAsyncEnumerator<IpcSnapshot> e =
                    c.ReadSnapshotsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
                try
                {
                    Assert.True(await e.MoveNextAsync());
                }
                catch (OperationCanceledException)
                {
                    // This test has been seen to hang exactly here when the whole solution's test
                    // projects run in parallel (never when this project runs alone). A bare cancellation
                    // says nothing about the cause, so name the client and how many connections the
                    // server had actually accepted: that separates "the server never took the second
                    // connection" from "it did, but the broadcast never reached it".
                    throw new XunitException(
                        $"Client {index} bekam keinen Snapshot; der Server hatte {accepted} Verbindung(en) angenommen.");
                }
                Assert.Equal(DaemonStatus.Active, e.Current.Status);
            }
        }
        finally
        {
            stop.Cancel();
            try { await broadcaster; } catch { /* egal */ }
        }
    }
}
