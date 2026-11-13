// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LinFan.Ipc.Tests;

public class IpcTransportTests
{
    [Fact]
    public void FanAssignment_WithoutMaxPwm_GetsTheCoreDefault_NotZero()
    {
        // A peer that omits maxPwm (older or third-party client) must not clamp the fan's curve to a stop.
        var fan = JsonSerializer.Deserialize<IpcFanAssignment>("""{ "fanId": "f1", "name": "F", "minPwm": 40 }""",
            IpcJson.Options)!;

        Assert.Equal(255, fan.MaxPwm);
        Assert.Equal(40, fan.MinPwm);
        Assert.Null(fan.AssignedCurveId);
    }

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
            await using IAsyncEnumerator<IpcSnapshot?> e =
                client.ReadSnapshotsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            Assert.True(await e.MoveNextAsync());
            Assert.NotNull(e.Current);
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
    public async Task Client_SendsSaveConfigCommand_WithTheBaseMinPwm()
    {
        string path = TempSocket();
        await using var server = new IpcServer(path);
        var received = new TaskCompletionSource<IpcCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.CommandHandler = cmd => received.TrySetResult(cmd);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        var cfg = IpcConfig.Empty with
        {
            Fans = new[]
            {
                new IpcFanAssignment("f1", "CPU", 40, 220, "c1", BaseMinPwm: 40),
                new IpcFanAssignment("f2", "Back", 30, 220, null), // no base (older GUI)
            },
        };

        await using var client = new IpcClient(path);
        await client.ConnectAsync(cts.Token);
        await client.SendCommandAsync(new IpcCommand(IpcCommand.SaveConfig, cfg), cts.Token);

        IpcCommand command = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(40, command.Config!.Fans[0].BaseMinPwm);
        Assert.Null(command.Config.Fans[1].BaseMinPwm);
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
    public void Sensor_FromADaemonWithoutTheLabel_HasNone()
    {
        IpcSnapshot older = JsonSerializer.Deserialize<IpcSnapshot>(
            """{"status":"Active","dryRun":false,"hottestTempC":40,"sensors":[{"id":"t","name":"CPU","kind":"Temperature","unit":"°C","value":40}],"fans":[]}""",
            IpcJson.Options)!;

        Assert.Null(Assert.Single(older.Sensors).Label); // the GUI then saves a name as it is shown, as before
    }

    [Fact]
    public async Task Sensor_Label_RoundTrips()
    {
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40,
            new[] { new IpcSensor("k10temp/temp1", "CPU", "Temperature", "°C", 55, Label: "k10temp Tctl") },
            Array.Empty<IpcFan>(), IpcConfig.Empty);

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.Equal("k10temp Tctl", Assert.Single(received.Sensors).Label);
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

    public static TheoryData<string[]> GroupLists => new()
    {
        new[] { "GPU", "Leer", "CPU" },
        Array.Empty<string>(), // every group deleted - must not turn into null ("older peer")
    };

    [Theory]
    [MemberData(nameof(GroupLists))]
    public async Task Config_SensorGroups_RoundTrip_InOrder(string[] groups)
    {
        var cfg = IpcConfig.Empty with { SensorGroups = groups };
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(), cfg);

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.NotNull(received.Config!.SensorGroups);
        Assert.Equal(groups, received.Config.SensorGroups);
    }

    [Fact]
    public void Config_SensorGroups_WireName_AndAPeerWithoutTheField()
    {
        string line = Encoding.UTF8.GetString(
            IpcJson.SerializeLine(IpcConfig.Empty with { SensorGroups = new[] { "CPU" } }));
        IpcConfig older = JsonSerializer.Deserialize<IpcConfig>(
            """{"curves":[],"fans":[],"sensors":[],"profiles":[],"activeProfileId":null}""", IpcJson.Options)!;

        Assert.Contains("\"sensorGroups\":[\"CPU\"]", line);
        Assert.Null(older.SensorGroups); // the daemon keeps its list on a merge
    }

    [Fact]
    public void Snapshot_DaemonVersion_WireName_AndAnOlderDaemonWithoutIt()
    {
        string line = Encoding.UTF8.GetString(IpcJson.SerializeLine(
            new IpcSnapshot(DaemonStatus.Active, false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(),
                DaemonVersion: "0.5.0+abc")));
        IpcSnapshot older = JsonSerializer.Deserialize<IpcSnapshot>(
            """{"status":"Active","dryRun":false,"hottestTempC":40,"sensors":[],"fans":[]}""", IpcJson.Options)!;

        Assert.Contains("\"daemonVersion\":", line); // the encoder escapes '+', so the value is checked parsed
        Assert.Equal("0.5.0+abc", JsonSerializer.Deserialize<IpcSnapshot>(line, IpcJson.Options)!.DaemonVersion);
        Assert.Null(older.DaemonVersion); // the GUI reads it as an older service
    }

    [Fact]
    public async Task Snapshot_ControlState_AndFailSafe_RoundTrip()
    {
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(),
            new[]
            {
                new IpcFan("f1", "Fan", 1500, 128, "Manual", true, ControlState: "CurveHolding", ControlCurveId: "c1",
                    Tachometer: "hwmon7/fan1"),
                new IpcFan("f2", "EC", 2100, 0, "Auto", false, ControlState: "NotControllable", ReadOnlyReason: "DriverLocked"),
            },
            FailSafe: "Blind", ConfigProblem: "SetAside", ConfigSetAsidePath: "/etc/linfan/config.json.invalid");

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.Equal(("SetAside", "/etc/linfan/config.json.invalid"), (received.ConfigProblem, received.ConfigSetAsidePath));
        IpcFan fan = received.Fans[0];
        Assert.Equal("CurveHolding", fan.ControlState);
        Assert.Equal("c1", fan.ControlCurveId);
        Assert.Null(fan.ReadOnlyReason);
        Assert.Equal("DriverLocked", received.Fans[1].ReadOnlyReason);
        Assert.Equal(("hwmon7/fan1", null), (fan.Tachometer, received.Fans[1].Tachometer));
        Assert.Equal("Blind", received.FailSafe);
    }

    [Fact]
    public async Task Snapshot_HardwareChanges_RoundTrip()
    {
        var replaced = new IpcHardwareChange("replaced-1562558ef31dea92", "Replaced", "amdgpu", "pci:1002:73bf:1849:5201",
            "amdgpu", "pci:1002:744c:1da2:e471", ["amdgpu/pwm1"], ["amdgpu/temp1", "amdgpu/temp2"]);
        var missing = new IpcHardwareChange("missing-fd061d3c349808a6", "Missing", null, null, null, null, [], ["usb/temp1"]);
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(),
            HardwareChanges: [replaced, missing]);

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.NotNull(received.HardwareChanges);
        Assert.Equal(2, received.HardwareChanges!.Count);
        IpcHardwareChange first = received.HardwareChanges[0];
        Assert.Equal((replaced.Key, replaced.Kind, replaced.OldName, replaced.OldFingerprint, replaced.NewName, replaced.NewFingerprint),
            (first.Key, first.Kind, first.OldName, first.OldFingerprint, first.NewName, first.NewFingerprint));
        Assert.Equal(replaced.FanIds, first.FanIds);
        Assert.Equal(replaced.SensorIds, first.SensorIds);
        IpcHardwareChange second = received.HardwareChanges[1];
        Assert.Equal(("Missing", null, null), (second.Kind, second.OldName, second.NewFingerprint));
        Assert.Empty(second.FanIds);
        Assert.Equal(["usb/temp1"], second.SensorIds);
    }

    [Fact]
    public void HardwareChanges_WireName_AndADaemonWithoutThem()
    {
        string line = Encoding.UTF8.GetString(IpcJson.SerializeLine(new IpcSnapshot(
            DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(),
            HardwareChanges: [new IpcHardwareChange("missing-0", "Missing", "nvme", null, null, null, [], ["nvme/temp1"])])));
        IpcSnapshot older = JsonSerializer.Deserialize<IpcSnapshot>(
            """{"status":"Active","dryRun":false,"hottestTempC":40,"sensors":[],"fans":[]}""", IpcJson.Options)!;

        Assert.Contains("\"hardwareChanges\":[{\"key\":\"missing-0\",\"kind\":\"Missing\",\"oldName\":\"nvme\"", line);
        Assert.Contains("\"sensorIds\":[\"nvme/temp1\"]", line);
        Assert.Null(older.HardwareChanges); // the GUI reads it as an older daemon
    }

    [Fact]
    public void HardwareCommands_WireNames()
    {
        Assert.Equal("acceptHardware", IpcCommand.AcceptHardware);
        Assert.Equal("forgetHardware", IpcCommand.ForgetHardware);
        IpcCommand command = JsonSerializer.Deserialize<IpcCommand>(
            """{"command":"acceptHardware","target":"replaced-1562558ef31dea92","value":1}""", IpcJson.Options)!;
        Assert.Equal((IpcCommand.AcceptHardware, "replaced-1562558ef31dea92", 1), (command.Command, command.Target, command.Value));
    }

    [Fact]
    public void ControlState_WireNames_AndADaemonWithoutTheFields()
    {
        string line = Encoding.UTF8.GetString(IpcJson.SerializeLine(new IpcSnapshot(
            DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(),
            new[] { new IpcFan("f1", "Fan", null, 0, "Auto", true, ControlState: "Curve", ControlCurveId: "c1") },
            FailSafe: "None")));
        IpcSnapshot older = JsonSerializer.Deserialize<IpcSnapshot>(
            """{"status":"Active","dryRun":false,"hottestTempC":40,"sensors":[],"fans":[{"id":"f1","name":"Fan","rpm":null,"pwm":0,"mode":"Auto","canControl":true,"manualOverride":false}]}""",
            IpcJson.Options)!;

        Assert.Contains("\"controlState\":\"Curve\"", line);
        Assert.Contains("\"controlCurveId\":\"c1\"", line);
        Assert.Contains("\"failSafe\":\"None\"", line);
        Assert.Null(older.FailSafe);                      // the GUI reads these as "unknown"
        Assert.Null(older.ConfigProblem);
        Assert.Null(Assert.Single(older.Fans).ControlState);
        Assert.Null(older.Fans[0].ControlCurveId);
        Assert.Null(older.Fans[0].ReadOnlyReason);
        Assert.Null(older.Fans[0].Tachometer);           // the GUI keeps saying "automatic (hardware)"
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
        var fan = new IpcFanAssignment("f1", "CPU", 40, 220, "c1", Calibration: new IpcFanCalibration(96, 400, 1800,
            "hwmon7/fan1", [new IpcCalibrationSample(0, 1500), new IpcCalibrationSample(32, 0), new IpcCalibrationSample(96, 400)]));
        var cfg = new IpcConfig(Array.Empty<IpcCurve>(), new[] { fan },
            Array.Empty<IpcSensorName>(), Array.Empty<IpcProfile>(), null);
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), Array.Empty<IpcFan>(), cfg);

        IpcSnapshot received = await RoundTripAsync(sent);

        IpcFanCalibration? cal = Assert.Single(received.Config!.Fans).Calibration;
        Assert.NotNull(cal);
        Assert.Equal(96, cal!.StartPwm);
        Assert.Equal(400, cal.MinRpm);
        Assert.Equal(1800, cal.MaxRpm);
        Assert.Equal("hwmon7/fan1", cal.Tachometer);
        Assert.Equal([new(0, 1500), new(32, 0), new(96, 400)], cal.Samples!);
    }

    [Fact]
    public void FanCalibration_WireNames_AndAPeerWithoutTheSensorAndSeries()
    {
        string line = Encoding.UTF8.GetString(IpcJson.SerializeLine(new IpcCommand(IpcCommand.ReplaceConfig,
            IpcConfig.Empty with
            {
                Fans = [new IpcFanAssignment("f1", "F", 96, Calibration: new IpcFanCalibration(96, 400, 1800, "hwmon7/fan1", [new(96, 400)]))],
            })));
        IpcFanCalibration older = JsonSerializer.Deserialize<IpcFanCalibration>(
            """{"startPwm":96,"minRpm":400,"maxRpm":1800}""", IpcJson.Options)!;

        Assert.Contains("\"tachometer\":\"hwmon7/fan1\",\"samples\":[{\"pwm\":96,\"rpm\":400}]", line);
        Assert.Null(older.Tachometer);
        Assert.Null(older.Samples);
    }

    [Fact]
    public async Task Snapshot_CalibrationMismatch_RoundTrips()
    {
        var sent = new IpcSnapshot(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(),
            new[]
            {
                new IpcFan("f1", "Fan", 600, 128, "Manual", true, ControlState: "Curve", CalibrationMismatch: "Slower"),
                new IpcFan("f2", "Fan", 900, 128, "Manual", true, ControlState: "Curve"),
            });

        IpcSnapshot received = await RoundTripAsync(sent);

        Assert.Equal(("Slower", null), (received.Fans[0].CalibrationMismatch, received.Fans[1].CalibrationMismatch));
    }

    [Fact]
    public void CalibrationMismatch_WireName_AndADaemonWithoutIt()
    {
        string line = Encoding.UTF8.GetString(IpcJson.SerializeLine(new IpcSnapshot(
            DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(),
            new[] { new IpcFan("f1", "Fan", 0, 64, "Manual", true, CalibrationMismatch: "Stalls") })));
        IpcSnapshot older = JsonSerializer.Deserialize<IpcSnapshot>(
            """{"status":"Active","dryRun":false,"hottestTempC":40,"sensors":[],"fans":[{"id":"f1","name":"Fan","rpm":null,"pwm":0,"mode":"Auto","canControl":true}]}""",
            IpcJson.Options)!;

        Assert.Contains("\"calibrationMismatch\":\"Stalls\"", line);
        Assert.Null(Assert.Single(older.Fans).CalibrationMismatch); // the GUI reads it as "fits or unknown"
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
        try
        {
            await client.SendCommandAsync(oversized, cts.Token);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // A named pipe reports the server's disconnect on the write itself (a Unix socket buffers it).
        }

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
}
