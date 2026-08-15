// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LinFan.Ipc.Tests;

public class IpcTransportTests
{
    private static string TempSocket() =>
        Path.Combine(Path.GetTempPath(), $"linfan-ipc-{Guid.NewGuid():N}.sock");

    /// <summary>Verbindet, broadcastet wiederholt (kein Accept-Timing-Rennen) und liest genau einen Snapshot.</summary>
    private static async Task<IpcSnapshot> RoundTripAsync(IpcSnapshot toSend)
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);

        using var stop = new CancellationTokenSource();
        var broadcaster = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await server.BroadcastAsync(toSend);
                try { await Task.Delay(100, stop.Token); } catch { break; }
            }
        });

        try
        {
            await using IAsyncEnumerator<IpcSnapshot> e =
                client.ReadSnapshotsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            Assert.True(await e.MoveNextAsync());
            return e.Current;
        }
        finally
        {
            stop.Cancel();
            try { await broadcaster; } catch { /* egal */ }
        }
    }

    [Fact]
    public async Task Server_Broadcasts_Snapshot_Client_Receives()
    {
        var sent = new IpcSnapshot(DaemonStatus.DryRun, DryRun: true, 42.5,
            new[] { new IpcSensor("s1", "CPU", "Temperature", "°C", 42.5) },
            new[] { new IpcFan("f1", "Fan", 1500, 128, "Manual", true) });

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.Equal(DaemonStatus.DryRun, received.Status);
        Assert.True(received.DryRun);
        Assert.Equal(42.5, received.HottestTempC);
        Assert.Equal("CPU", Assert.Single(received.Sensors).Name);
        IpcFan fan = Assert.Single(received.Fans);
        Assert.Equal(128, fan.Pwm);
        Assert.Equal(1500, fan.Rpm);
    }

    [Fact]
    public async Task Snapshot_With_NaN_RoundTrips()
    {
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, double.NaN,
            new[] { new IpcSensor("s", "GPU", "Temperature", "°C", double.NaN) },
            Array.Empty<IpcFan>());

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.True(double.IsNaN(received.HottestTempC));
        Assert.True(double.IsNaN(Assert.Single(received.Sensors).Value));
    }

    [Fact]
    public async Task Client_SendsCommand_Server_Receives()
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.Reload), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("reload", command.Command);
    }

    [Fact]
    public async Task Client_Connect_Fails_When_NoServer()
    {
        await using var client = new IpcClient(TempSocket());
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Client_SendsManualPwmCommand_WithTargetAndValue()
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 200), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("setManualPwm", command.Command);
        Assert.Equal("hwmon7/pwm1", command.Target);
        Assert.Equal(200, command.Value);
    }

    [Fact]
    public async Task Client_SendsSetCurveEnabledCommand_WithTargetAndValue()
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.SetCurveEnabled, Target: "curve-1", Value: 0), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("setCurveEnabled", command.Command);
        Assert.Equal("curve-1", command.Target);
        Assert.Equal(0, command.Value);
    }

    [Fact]
    public async Task Client_SendsResetConfigCommand()
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.ResetConfig), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("resetConfig", command.Command);
        Assert.Null(command.Config);
    }

    [Fact]
    public async Task Client_SendsReplaceConfigCommand_WithConfigAndCalibration()
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        var fan = new IpcFanAssignment("f1", "CPU", 40, 220, "c1", Calibration: new IpcFanCalibration(96, 400, 1800));
        var cfg = new IpcConfig(Array.Empty<IpcCurve>(), new[] { fan },
            Array.Empty<IpcSensorName>(), Array.Empty<IpcProfile>(), null);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.ReplaceConfig, cfg), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("replaceConfig", command.Command);
        Assert.NotNull(command.Config);
        IpcFanCalibration? cal = Assert.Single(command.Config!.Fans).Calibration;
        Assert.NotNull(cal);
        Assert.Equal(96, cal!.StartPwm);
    }

    [Fact]
    public async Task Client_SendsSetFanTachometerCommand_WithTargetAndRpmSource()
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(
            new IpcCommand(IpcCommand.SetFanTachometer, Target: "hwmon7/pwm1", RpmSource: "hwmon7/fan3"), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("setFanTachometer", command.Command);
        Assert.Equal("hwmon7/pwm1", command.Target);
        Assert.Equal("hwmon7/fan3", command.RpmSource);
    }

    [Fact]
    public async Task Client_SendsStartTachMappingCommand_WithTarget()
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.StartTachMapping, Target: "hwmon7/pwm1"), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("startTachMapping", command.Command);
        Assert.Equal("hwmon7/pwm1", command.Target);
    }

    [Fact]
    public async Task Snapshot_TachMapping_RoundTrips()
    {
        var mapping = new IpcTachMapping("pwm1", TachMappingPhase.Matched, Running: false,
            MatchedTachId: "fan1", RiseRpm: 900);
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40,
            Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(), TachMapping: mapping);

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.NotNull(received.TachMapping);
        Assert.Equal(TachMappingPhase.Matched, received.TachMapping!.Phase);
        Assert.Equal("fan1", received.TachMapping.MatchedTachId);
        Assert.Equal(900, received.TachMapping.RiseRpm);
    }

    [Fact]
    public async Task Config_FanRpmSource_RoundTrips()
    {
        var fan = new IpcFanAssignment("f1", "CPU", 40, 220, "c1", RpmSource: "io/fan/2");
        var cfg = new IpcConfig(Array.Empty<IpcCurve>(), new[] { fan },
            Array.Empty<IpcSensorName>(), Array.Empty<IpcProfile>(), null);
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(), cfg);

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.Equal("io/fan/2", Assert.Single(received.Config!.Fans).RpmSource);
    }

    [Fact]
    public async Task Snapshot_CurveEnabledFlag_RoundTrips()
    {
        var disabled = new IpcCurve("c", "CPU", "t", 2.0, new[] { new IpcCurvePoint(30, 0) }, Enabled: false);
        var cfg = new IpcConfig(new[] { disabled }, Array.Empty<IpcFanAssignment>(),
            Array.Empty<IpcSensorName>(), Array.Empty<IpcProfile>(), null);
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(), cfg);

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.False(Assert.Single(received.Config!.Curves).Enabled);
    }

    [Fact]
    public async Task Snapshot_FanCalibration_RoundTrips()
    {
        var fan = new IpcFanAssignment("f1", "CPU", 40, 220, "c1", Calibration: new IpcFanCalibration(96, 400, 1800));
        var cfg = new IpcConfig(Array.Empty<IpcCurve>(), new[] { fan },
            Array.Empty<IpcSensorName>(), Array.Empty<IpcProfile>(), null);
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(), cfg);

        IpcSnapshot received = await RoundTripAsync(sent);

        IpcFanCalibration? cal = Assert.Single(received.Config!.Fans).Calibration;
        Assert.NotNull(cal);
        Assert.Equal(96, cal!.StartPwm);
        Assert.Equal(400, cal.MinRpm);
        Assert.Equal(1800, cal.MaxRpm);
    }

    [Fact]
    public async Task Snapshot_Identify_RoundTrips()
    {
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40,
            Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(),
            Identify: new IpcIdentify("f1", Running: true));

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.NotNull(received.Identify);
        Assert.Equal("f1", received.Identify!.FanId);
        Assert.True(received.Identify.Running);
        Assert.Null(received.Identify.FailReason);
    }

    [Fact]
    public async Task Snapshot_CalibrationFailReason_WithTemps_RoundTrips()
    {
        // Codifizierte Fehlerursache (Enum als Name) + die rohen Übertemp-Messwerte gehen verlustfrei durch.
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40,
            Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(),
            Calibration: new IpcCalibration("f1", CalibrationPhase.Failed, 0, 0, Running: false, Done: false,
                StartPwm: null, FailReason: CalibrationFailReason.OverTemperature, OverTempC: 95.5, OverLimitC: 90.0));

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.NotNull(received.Calibration);
        Assert.Equal(CalibrationPhase.Failed, received.Calibration!.Phase);
        Assert.Equal(CalibrationFailReason.OverTemperature, received.Calibration.FailReason);
        Assert.Equal(95.5, received.Calibration.OverTempC);
        Assert.Equal(90.0, received.Calibration.OverLimitC);
    }

    [Fact]
    public async Task Client_FallsBackToReachableCandidate()
    {
        string good = TempSocket();
        string bogus = TempSocket(); // kein Server hier - muss übersprungen werden
        await using var server = new IpcServer(good);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(new[] { bogus, good });
        await client.ConnectAsync(cts.Token);

        Assert.Equal(good, client.ConnectedPath);
    }

    [Fact]
    public async Task Server_SocketIsNotWorldAccessible()
    {
        // Zugriffskontrolle: der Socket darf nicht mehr world-rw sein (früher 0666). Egal ob der System-
        // (0660, Gruppe linfan) oder der User-Zweig (0600) greift - die Other-Bits müssen aus sein, sonst
        // könnte jeder lokale Account Steuerbefehle an den privilegierten Daemon senden.
        if (!OperatingSystem.IsLinux())
            return; // Unix-Dateirechte nur auf Linux geprüft (Named Pipe hat eine eigene DACL)

        string path = TempSocket();
        await using var server = new IpcServer(path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token); // Listen() setzt die Rechte synchron

        UnixFileMode mode = File.GetUnixFileMode(path);
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead), $"Socket ist world-readable: {mode}");
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite), $"Socket ist world-writable: {mode}");
    }

    [Fact]
    public async Task Server_DropsOversizedCommandLine_WithoutInvokingHandler()
    {
        // DoS-Schutz im privilegierten Prozess: eine Kommandozeile über der Byte-Obergrenze wird verworfen
        // und die Verbindung getrennt, statt unbegrenzt zu puffern. Hier per Test-Seam auf 64 Bytes gesenkt.
        string path = TempSocket();
        var log = new CapturingLogger();
        await using var server = new IpcServer(path, transport: null, log: log, maxCommandBytes: 64);

        var handled = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => handled.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);

        // Ziel-String allein ist schon > 64 Bytes → die JSON-Zeile überschreitet die Obergrenze.
        var oversized = new IpcCommand(IpcCommand.SetManualPwm, Target: new string('x', 512), Value: 1);
        await client.SendCommandAsync(oversized, cts.Token);

        // Der Handler darf für die überlange Zeile NIE feuern; stattdessen wird der Guard geloggt.
        await Assert.ThrowsAsync<TimeoutException>(() => handled.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("möglicher DoS"));
    }

    [Fact]
    public async Task Client_ConcurrentSends_AllArriveIntact()
    {
        const int n = 30;
        string path = TempSocket();
        await using var server = new IpcServer(path);

        var received = new ConcurrentBag<IpcCommand>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd =>
        {
            received.Add(cmd);
            if (received.Count >= n)
                done.TrySetResult();
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);

        // Viele gleichzeitige Sends: ohne Schreib-Serialisierung verschränken sich die NDJSON-Zeilen
        // (ungültige Zeilen werden still verworfen) bzw. NetworkStream wirft - dann käme nicht alles an.
        var sends = Enumerable.Range(0, n).Select(i =>
            client.SendCommandAsync(new IpcCommand(IpcCommand.SetManualPwm, Target: $"fan{i}", Value: i), cts.Token));
        await Task.WhenAll(sends);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var targets = received.Select(c => c.Target).ToHashSet();
        Assert.Equal(n, targets.Count); // jedes Kommando genau einmal, unverschränkt geparst
    }

    /// <summary>Sammelt Log-Einträge (Level + gerenderte Nachricht) für Assertions.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
