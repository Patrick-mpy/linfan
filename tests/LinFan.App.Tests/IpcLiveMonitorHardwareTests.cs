// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// What the GUI reads of the daemon's hardware report and calibration check, what it sends to resolve a change, and the
/// calibration's measuring sensor and series, which only travel so that a backup keeps them.
/// </summary>
public sealed class IpcLiveMonitorHardwareTests
{
    private static IpcSnapshot Snapshot(IReadOnlyList<IpcHardwareChange>? changes = null, params IpcFan[] fans) =>
        new(DaemonStatus.Active, DryRun: false, 40, Array.Empty<IpcSensor>(), fans, HardwareChanges: changes);

    // --- hardware changes ---------------------------------------------------------------------------

    [Fact]
    public void HardwareChanges_AreRead_ShownTextStripped_KeyAndIdsAsSent()
    {
        var change = new IpcHardwareChange("Replaced:1a2b", "Replaced", "amd‮gpu", "pci:1002:73bf\u0007", "amdgpu",
            "pci:1002:744c", new[] { "amdgpu/pwm1" }, new[] { "amdgpu/temp1", "amdgpu/temp2" });

        HardwareChangeReading read = Assert.Single(IpcLiveMonitor.Convert(Snapshot(new[] { change })).HardwareChanges!);

        Assert.Equal("Replaced:1a2b", read.Key);
        Assert.Equal(HardwareChangeKind.Replaced, read.Kind);
        Assert.Equal("amdgpu", read.OldName);              // a bidi override would reorder the dialog's text
        Assert.Equal("pci:1002:73bf", read.OldFingerprint);
        Assert.Equal(("amdgpu", "pci:1002:744c"), (read.NewName, read.NewFingerprint));
        Assert.Equal(new[] { "amdgpu/pwm1" }, read.FanIds);
        Assert.Equal(new[] { "amdgpu/temp1", "amdgpu/temp2" }, read.SensorIds);
    }

    [Fact]
    public void HardwareChanges_NoneOrFromAnOlderDaemon_ReadAsNull()
    {
        Assert.Null(IpcLiveMonitor.Convert(Snapshot(changes: null)).HardwareChanges);
        Assert.Null(IpcLiveMonitor.Convert(Snapshot(Array.Empty<IpcHardwareChange>())).HardwareChanges);
    }

    /// <summary>
    /// A kind this GUI does not know (a newer daemon) reads as <c>null</c> rather than failing the snapshot; a change
    /// without a key cannot be resolved and is left out; missing lists read as empty, a missing old device stays unknown.
    /// </summary>
    [Fact]
    public void HardwareChanges_UnknownKind_NoKey_OrMissingLists_AreReadDefensively()
    {
        var changes = new[]
        {
            new IpcHardwareChange("k1", "Renamed", null, null, null, null, new[] { "f" }, Array.Empty<string>()),
            new IpcHardwareChange("", "Missing", null, null, null, null, Array.Empty<string>(), new[] { "s" }),
            new IpcHardwareChange("k3", "Missing", null, null, null, null, null!, null!),
        };

        IReadOnlyList<HardwareChangeReading> read = IpcLiveMonitor.Convert(Snapshot(changes)).HardwareChanges!;

        Assert.Equal(new[] { "k1", "k3" }, read.Select(c => c.Key));
        Assert.Null(read[0].Kind);
        Assert.Equal(HardwareChangeKind.Missing, read[1].Kind);
        Assert.Null(read[1].OldName);
        Assert.Empty(read[1].FanIds);
        Assert.Empty(read[1].SensorIds);
    }

    // --- calibration check --------------------------------------------------------------------------

    [Theory]
    [InlineData("Stalls", CalibrationMismatch.Stalls)]
    [InlineData("Slower", CalibrationMismatch.Slower)]
    [InlineData("Faster", CalibrationMismatch.Faster)]
    [InlineData("OtherSensor", CalibrationMismatch.OtherSensor)]
    [InlineData(null, null)]       // fits, cannot be judged, or an older daemon
    [InlineData("Wobbles", null)]  // a finding of a newer daemon
    [InlineData("slower", null)]   // names only, exact case
    public void CalibrationMismatch_IsReadByName(string? sent, CalibrationMismatch? read)
    {
        var fan = new IpcFan("f1", "CPU", 900, 128, "Manual", CanControl: true, CalibrationMismatch: sent);

        Assert.Equal(read, Assert.Single(IpcLiveMonitor.Convert(Snapshot(null, fan)).Fans).CalibrationMismatch);
    }

    // --- calibration: measuring sensor and series ---------------------------------------------------

    private static readonly FanCalibration Measured = new()
    {
        StartPwm = 64,
        MinRpm = 310,
        MaxRpm = 1800,
        Tachometer = "hwmon2/fan1",
        Samples = new[] { new CalibrationSample(0, 0), new CalibrationSample(64, 310), new CalibrationSample(255, 1800) },
    };

    private static AppConfig ConfigWith(FanCalibration calibration) => new()
    {
        Fans = new[] { new FanConfig { FanId = "hwmon2/pwm1", Calibration = calibration } },
    };

    /// <summary>An export carries what the daemon compares the fan with, so its import does not lose the check.</summary>
    [Fact]
    public void Replace_CarriesTheCalibrationsSensorAndSeries()
    {
        IpcFanCalibration sent = Assert.Single(IpcLiveMonitor.ToIpcConfig(ConfigWith(Measured), withDaemonOwned: true).Fans).Calibration!;

        Assert.Equal((64, 310, 1800), (sent.StartPwm, sent.MinRpm, sent.MaxRpm));
        Assert.Equal("hwmon2/fan1", sent.Tachometer);
        Assert.Equal(new[] { (0, 0), (64, 310), (255, 1800) }, sent.Samples!.Select(s => (s.Pwm, s.Rpm)));
    }

    [Fact]
    public void Save_StillSendsNoCalibration()
    {
        Assert.Null(Assert.Single(IpcLiveMonitor.ToIpcConfig(ConfigWith(Measured)).Fans).Calibration);
    }

    [Fact]
    public void TheDaemonsCalibration_IsReadWithSensorAndSeries_AndGoesBackUnchanged()
    {
        IpcConfig wire = IpcLiveMonitor.ToIpcConfig(ConfigWith(Measured), withDaemonOwned: true);

        FanCalibration read = Assert.Single(IpcLiveMonitor.Convert(Snapshot() with { Config = wire }).Config.Fans).Calibration!;

        Assert.Equal("hwmon2/fan1", read.Tachometer);
        Assert.Equal(Measured.Samples, read.Samples);
        Assert.Equal(MainController_Signature(ConfigWith(Measured)), MainController_Signature(ConfigWith(read)));
    }

    [Fact]
    public void ACalibrationFromAnOlderDaemon_HasNoSensorAndNoSeries()
    {
        var wire = new IpcConfig(Array.Empty<IpcCurve>(),
            new[] { new IpcFanAssignment("f1", "", 0, 255, null, Calibration: new IpcFanCalibration(64, 310, 1800)) },
            Array.Empty<IpcSensorName>(), Array.Empty<IpcProfile>(), ActiveProfileId: null);

        FanCalibration read = Assert.Single(IpcLiveMonitor.Convert(Snapshot() with { Config = wire }).Config.Fans).Calibration!;

        Assert.Null(read.Tachometer);
        Assert.Empty(read.Samples);
        Assert.Equal(64, read.StartPwm);
    }

    /// <summary>
    /// A sample whose PWM is no raw value is dropped, as the daemon drops it on import: clamped, it would claim a speed at
    /// a PWM it was not measured at - and a backup exported from the GUI's config would carry that claim.
    /// </summary>
    [Fact]
    public void ASampleOutsideTheRawPwmRange_IsDropped_NotClamped()
    {
        IpcCalibrationSample[] samples =
            [new(-1, 0), new(0, 0), new(64, 310), new(255, 1800), new(256, 2400), new(int.MaxValue, 9000)];
        var wire = new IpcConfig(Array.Empty<IpcCurve>(),
            new[] { new IpcFanAssignment("f1", "", 0, 255, null, Calibration: new IpcFanCalibration(64, 310, 1800, null, samples)) },
            Array.Empty<IpcSensorName>(), Array.Empty<IpcProfile>(), ActiveProfileId: null);

        FanCalibration read = Assert.Single(IpcLiveMonitor.Convert(Snapshot() with { Config = wire }).Config.Fans).Calibration!;

        Assert.Equal([new(0, 0), new(64, 310), new(255, 1800)], read.Samples);
    }

    private static string MainController_Signature(AppConfig config) => LinFan.App.Controllers.MainController.ConfigSignature(config);

    // --- commands -----------------------------------------------------------------------------------

    [Fact]
    public async Task AcceptAndForget_GoOutWithTheKey_AndTheNamesFlagAsValue()
    {
        var client = new RecordingIpcClient();
        await using IpcLiveMonitor monitor = await RecordingIpcClient.ConnectedMonitorAsync(client);

        Assert.True(await monitor.SendAcceptHardwareAsync("Replaced:1a2b", resetSensorNames: true));
        Assert.True(await monitor.SendAcceptHardwareAsync("Replaced:1a2b", resetSensorNames: false));
        Assert.True(await monitor.SendForgetHardwareAsync("Missing:3c4d"));

        Assert.Equal(new[]
        {
            (IpcCommand.AcceptHardware, "Replaced:1a2b", (int?)1),
            (IpcCommand.AcceptHardware, "Replaced:1a2b", (int?)0),
            (IpcCommand.ForgetHardware, "Missing:3c4d", (int?)null),
        }, client.Sent.Select(c => (c.Command, c.Target!, c.Value)));
    }

    [Fact]
    public async Task WithoutAConnection_NothingGoesOut()
    {
        var client = new RecordingIpcClient(reachable: false);
        await using var monitor = new IpcLiveMonitor(() => client, TimeSpan.FromHours(1));

        Assert.False(await monitor.SendAcceptHardwareAsync("k", resetSensorNames: false));
        Assert.False(await monitor.SendForgetHardwareAsync("k"));
        Assert.Empty(client.Sent);
    }
}
