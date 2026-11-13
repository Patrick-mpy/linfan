// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// The devices behind the configured entries: recorded at start and on every change, reported in the snapshot when they
/// no longer match, and resolved only by the user's <c>acceptHardware</c> / <c>forgetHardware</c> - applied on the loop
/// thread against the config as it is then. None of it may cost the user's config or the tick.
/// </summary>
public partial class ControlLoopServiceTests
{
    private static readonly HardwareDevice OldGpu = new("amdgpu", "pci:1002:73bf:1849:5201");
    private static readonly HardwareDevice NewGpu = new("amdgpu", "pci:1002:744c:1da2:e471");
    private static readonly HardwareDevice Drive = new("nvme", "nvme:KIOXIA");

    private static readonly CurveConfig GpuCurve = new()
    {
        Id = "c",
        Name = "c",
        SourceSensorIds = ["t"],
        SmoothingSeconds = 0,
        Points = [new CurvePoint(30, 0), new CurvePoint(80, 100)],
    };

    /// <summary>A GPU (now <see cref="NewGpu"/>) with a fan, its tachometer and a temperature sensor, plus the rig's "t".</summary>
    private static FakeHardware GpuRig()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 60);                       // the curve's source: 60 % → pwm 153
        hw.AddTempSensor("amdgpu/temp1", 50, name: "edge");
        hw.AddFanSensor("amdgpu/fan1", 0);
        hw.AddFan("amdgpu/pwm1", tachId: "amdgpu/fan1");
        hw.TachId = "amdgpu/fan1";
        hw.RpmForPwm = pwm => pwm < 96 ? 0 : 300 + pwm * 4;
        for (int i = 1; i < hw.Sensors.Count; i++)
            hw.Sensors[i] = hw.Sensors[i] with { Device = NewGpu };
        hw.Fans[0] = hw.Fans[0] with { Device = NewGpu };
        return hw;
    }

    /// <summary>A config set up for <paramref name="recorded"/>: the GPU fan on a curve, its sensor with an own name, and
    /// a drive that is no longer there.</summary>
    private static FakeConfigStore GpuStore(HardwareDevice? recorded) => new()
    {
        Exists = true,
        Stored = AppConfig.Empty with
        {
            OnboardingCompleted = true,
            Curves = [GpuCurve],
            Fans = [new FanConfig { FanId = "amdgpu/pwm1", Name = "GPU", MinPwm = 60, AssignedCurveId = "c", Device = recorded }],
            Sensors =
            [
                new SensorConfig { SensorId = "amdgpu/temp1", Name = "Grafik", Device = recorded },
                new SensorConfig { SensorId = "nvme/temp1", Name = "SSD", Device = Drive },
            ],
            Profiles = [new Profile { Id = "p", Name = "P", Curves = [GpuCurve], Assignments = [new ProfileAssignment("amdgpu/pwm1", "c")] }],
            ActiveProfileId = "p",
        },
    };

    private static ControlLoopService HardwareService(
        FakeHardware hw, FakeConfigStore store, FakeIpcServer ipc, bool dryRun,
        Func<AppConfig, IReadOnlyList<HardwareChange>>? detect = null, ILogger<ControlLoopService>? log = null,
        Func<TimeSpan, CancellationToken, Task>? calibrationDelay = null,
        Func<TimeSpan, CancellationToken, Task>? identifyDelay = null,
        Func<TimeSpan, CancellationToken, Task>? couplingDelay = null) =>
        new(hw, hw, store, ipc, log ?? NullLogger<ControlLoopService>.Instance, FastTick,
            calibrationDelay is null ? null : (s, f) => new CalibrationService(s, f, calibrationDelay),
            identifyDelay ?? NoDelay,
            tachMappingFactory: (s, f) => new TachometerMappingService(s, f, couplingDelay ?? NoDelay),
            dryRunOverride: dryRun, detectHardwareChanges: detect);

    private static async Task<IpcHardwareChange> ReportedAsync(FakeIpcServer ipc, string kind)
    {
        await WaitUntilAsync(() => LastSnapshot(ipc)?.HardwareChanges?.Any(c => c.Kind == kind) == true);
        return LastSnapshot(ipc)!.HardwareChanges!.First(c => c.Kind == kind);
    }

    private static async Task TwoMoreTicksAsync(FakeIpcServer ipc)
    {
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count > seen + 1);
    }

    // --- Recording the devices ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_RecordsTheDevicesOfTheDetectedEntries_AndSavesThatOnce()
    {
        var hw = GpuRig();
        var store = GpuStore(recorded: null);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: true);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await TwoMoreTicksAsync(ipc);

        AppConfig saved = Assert.Single(store.Saves);
        Assert.Equal(NewGpu, saved.Fans.Single().Device);
        Assert.Equal(NewGpu, saved.Sensors.Single(s => s.SensorId == "amdgpu/temp1").Device);
        Assert.Equal(Drive, saved.Sensors.Single(s => s.SensorId == "nvme/temp1").Device); // not detected: left alone
        Assert.Equal(("GPU", (byte)60), (saved.Fans[0].Name, saved.Fans[0].MinPwm));
        Assert.Null(LastSnapshot(ipc)!.HardwareChanges);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Start_WhenRecordingTheDevicesThrows_KeepsTheUsersConfig()
    {
        // A descriptor without an id makes the stamp itself throw - an ArgumentNullException, which the load path's
        // generic catch would answer by setting the whole config aside.
        var hw = GpuRig();
        hw.Fans.Add(new FanDescriptor(new FanId(null!), "broken", CanControl: false, Tachometer: null, Source: "?"));
        var store = GpuStore(recorded: null);
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = HardwareService(hw, store, ipc, dryRun: true, log: log);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token); // handlers are set once the config is loaded
        await service.StopAsync(cts.Token);

        Assert.Null(store.SetAsideConfig);
        Assert.Empty(store.Saves);
        Assert.Contains(log.Snapshot(), e => e.Message.StartsWith("Geräte der Einträge nicht erfassbar", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Snapshot(), e => e.Message.Contains("unbrauchbar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Calibration_RecordsTheDeviceItMeasured_SoTheFanLeavesTheReplacement()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: false, calibrationDelay: NoDelay);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await ReportedAsync(ipc, "Replaced");

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "amdgpu/pwm1"));

        await WaitUntilAsync(() => store.Saves.Any(s => s.Fans.Single().Calibration is not null));
        Assert.Equal(NewGpu, store.Stored.Fans.Single().Device);
        await WaitUntilAsync(() => LastSnapshot(ipc)?.HardwareChanges?.FirstOrDefault(c => c.Kind == "Replaced") is { FanIds.Count: 0 });
        Assert.Equal(["amdgpu/temp1"], LastSnapshot(ipc)!.HardwareChanges!.First(c => c.Kind == "Replaced").SensorIds);

        await service.StopAsync(cts.Token);
    }

    // --- Reporting ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_ReportsAReplacedDevice_AndAMissingOne()
    {
        var hw = GpuRig();
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = HardwareService(hw, GpuStore(OldGpu), ipc, dryRun: false, log: log);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await ReportedAsync(ipc, "Missing");
        await TwoMoreTicksAsync(ipc);

        Assert.Collection(LastSnapshot(ipc)!.HardwareChanges!,
            replaced =>
            {
                Assert.Matches("^replaced-[0-9a-f]{16}$", replaced.Key);
                Assert.Equal((OldGpu.Name, OldGpu.Fingerprint, NewGpu.Name, NewGpu.Fingerprint),
                    (replaced.OldName, replaced.OldFingerprint, replaced.NewName, replaced.NewFingerprint));
                Assert.Equal(["amdgpu/pwm1"], replaced.FanIds);
                Assert.Equal(["amdgpu/temp1"], replaced.SensorIds);
            },
            missing =>
            {
                Assert.Equal(("Missing", "nvme", "nvme:KIOXIA", null, null),
                    (missing.Kind, missing.OldName, missing.OldFingerprint, missing.NewName, missing.NewFingerprint));
                Assert.Empty(missing.FanIds);
                Assert.Equal(["nvme/temp1"], missing.SensorIds);
            });
        // Each change is logged once, not every tick.
        Assert.Single(log.Snapshot(), e => e.Message.StartsWith("Gerät ersetzt: amdgpu", StringComparison.Ordinal));
        Assert.Single(log.Snapshot(), e => e.Message.StartsWith("Gerät nicht gefunden: nvme", StringComparison.Ordinal));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Snapshot_WhenDetectingTheChangesThrows_IsStillBroadcast_WithoutThem()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = HardwareService(hw, store, ipc, dryRun: false,
            detect: _ => throw new InvalidOperationException("detect failed (simulated)"), log: log);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => hw.Writes.ToArray().Contains(("amdgpu/pwm1", (byte)153))); // the curve runs

        ipc.Emit(new IpcCommand(IpcCommand.SaveConfig, IpcConfig.Empty with { CaseType = "Server" })); // detect again
        await WaitUntilAsync(() => store.Saves.Count > 0);
        await TwoMoreTicksAsync(ipc);

        Assert.All(ipc.Broadcasts, b => Assert.Null(b.HardwareChanges));
        Assert.Equal("Server", LastSnapshot(ipc)!.Config!.CaseType);
        Assert.Single(log.Snapshot(), e => e.Message.StartsWith("Hardware-Änderungen nicht bestimmbar", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Snapshot(), e => e.Message.StartsWith("Snapshot-Broadcast fehlgeschlagen", StringComparison.Ordinal));

        await service.StopAsync(cts.Token);
    }

    // --- acceptHardware ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task AcceptHardware_RecordsTheNewDevice_AndKeepsEverythingElse(int? value)
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: true);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");
        Assert.Single(LastSnapshot(ipc)!.HardwareChanges!); // a dry run reports nothing missing

        ipc.Emit(new IpcCommand(IpcCommand.AcceptHardware, Target: change.Key, Value: value));

        await WaitUntilAsync(() => store.Saves.Count > 0);
        AppConfig saved = Assert.Single(store.Saves);
        Assert.Equal(GpuStore(OldGpu).Stored.Fans[0] with { Device = NewGpu }, saved.Fans.Single());
        Assert.Equal(("Grafik", NewGpu), (saved.Sensors[0].Name, saved.Sensors[0].Device));
        Assert.Equal(("SSD", Drive), (saved.Sensors[1].Name, saved.Sensors[1].Device));
        await WaitUntilAsync(() => LastSnapshot(ipc)?.HardwareChanges is null);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task AcceptHardware_WithValue1_ClearsTheSensorsOwnNames_ButNotTheFans()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: true);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");

        ipc.Emit(new IpcCommand(IpcCommand.AcceptHardware, Target: change.Key, Value: 1));

        await WaitUntilAsync(() => store.Saves.Count > 0);
        AppConfig saved = Assert.Single(store.Saves);
        Assert.Equal("GPU", saved.Fans.Single().Name);
        Assert.Equal(("", NewGpu), (saved.Sensors[0].Name, saved.Sensors[0].Device));
        Assert.Equal("SSD", saved.Sensors[1].Name); // another device's sensor

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task AcceptHardware_ForAMissingDevice_SavesNothing()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: false);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange missing = await ReportedAsync(ipc, "Missing");

        ipc.Emit(new IpcCommand(IpcCommand.AcceptHardware, Target: missing.Key, Value: 1));
        await TwoMoreTicksAsync(ipc);

        Assert.Empty(store.Saves);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task HardwareCommands_ForAKeyNotReported_OrWithAValueOtherThan0Or1_AreRefused()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: true);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");

        ipc.Emit(new IpcCommand(IpcCommand.AcceptHardware, Target: "replaced-0000000000000000"));
        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: "missing-made-up"));
        ipc.Emit(new IpcCommand(IpcCommand.AcceptHardware, Target: change.Key, Value: 2));
        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key, Value: -1));
        await TwoMoreTicksAsync(ipc);

        Assert.Empty(store.Saves);

        await service.StopAsync(cts.Token);
    }

    // --- Resolved again when applied ---------------------------------------------------------------------------------

    [Fact]
    public async Task HardwareCommand_TakesTheEntriesTheChangeCoversWhenItIsApplied_NotWhenItWasSent()
    {
        var hw = TwoFanRig();
        var store = new FakeConfigStore
        {
            Exists = true,
            Stored = AppConfig.Empty with { OnboardingCompleted = true, Fans = [new FanConfig { FanId = "A" }, new FanConfig { FanId = "B" }] },
        };
        var ipc = new FakeIpcServer();
        IReadOnlyList<HardwareChange>[] detected = [[new HardwareChange("replaced-k", HardwareChangeKind.Replaced, OldGpu, NewGpu, ["A"], [])]];
        var service = HardwareService(hw, store, ipc, dryRun: true, detect: _ => Volatile.Read(ref detected[0]));
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await ReportedAsync(ipc, "Replaced");
        store.ClearSaves(); // the start seeded a profile

        Volatile.Write(ref detected[0], [new HardwareChange("replaced-k", HardwareChangeKind.Replaced, OldGpu, NewGpu, ["B"], [])]);
        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: "replaced-k"));

        await WaitUntilAsync(() => store.Saves.Count > 0);
        Assert.Equal(["A"], store.Saves[^1].Fans.Select(f => f.FanId));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task HardwareCommand_ForAChangeThatIsGoneWhenItIsApplied_SavesNothing()
    {
        var hw = TwoFanRig();
        var store = new FakeConfigStore
        {
            Exists = true,
            Stored = AppConfig.Empty with { OnboardingCompleted = true, Fans = [new FanConfig { FanId = "A" }] },
        };
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        IReadOnlyList<HardwareChange>[] detected = [[new HardwareChange("missing-k", HardwareChangeKind.Missing, Drive, null, ["A"], [])]];
        var service = HardwareService(hw, store, ipc, dryRun: true, detect: _ => Volatile.Read(ref detected[0]), log: log);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await ReportedAsync(ipc, "Missing");
        store.ClearSaves(); // the start seeded a profile

        Volatile.Write(ref detected[0], []);
        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: "missing-k"));
        await WaitUntilAsync(() => log.Snapshot().Any(e => e.Message.Contains("besteht nicht mehr", StringComparison.Ordinal)));
        await TwoMoreTicksAsync(ipc);

        Assert.Empty(store.Saves);

        await service.StopAsync(cts.Token);
    }

    // --- forgetHardware ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ForgetHardware_RemovesTheEntries_AndHandsTheFanBackToAuto()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: false);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => hw.Writes.ToArray().Contains(("amdgpu/pwm1", (byte)153))); // driven by its curve
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");
        int restores = hw.RestoreCount;

        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key));

        await WaitUntilAsync(() => store.Saves.Count > 0);
        AppConfig saved = Assert.Single(store.Saves);
        Assert.Empty(saved.Fans);
        Assert.Equal(["nvme/temp1"], saved.Sensors.Select(s => s.SensorId));
        Assert.Empty(saved.Profiles.Single().Assignments);
        Assert.Equal(["t"], saved.Curves.Single().SourceSensorIds);
        await WaitUntilAsync(() => LastSnapshot(ipc)?.Fans.Single().ControlState == nameof(FanControlState.HardwareAuto));
        int writes = hw.Writes.Count;
        await TwoMoreTicksAsync(ipc);

        Assert.Equal(FanMode.Auto, hw.ModeLog["amdgpu/pwm1"]);
        Assert.Equal(writes, hw.Writes.Count);       // left to the firmware, no write after the hand-back
        Assert.Equal(restores, hw.RestoreCount);     // one fan handed back, not every fan restored
        Assert.Equal(["Missing"], LastSnapshot(ipc)!.HardwareChanges!.Select(c => c.Kind));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ForgetHardware_HandsBackAFanTheLoopNeverDrove_ExactlyOnce()
    {
        // Every curve write fails, so the loop never counts the fan as driven and its orphan check would pass it over.
        var hw = GpuRig();
        hw.FailWrites = true;
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: false);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => LastSnapshot(ipc)?.Fans.Single().ControlState == nameof(FanControlState.Failed));
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");
        Assert.Empty(hw.ModeWrites.ToArray());

        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key));

        await WaitUntilAsync(() => store.Saves.Count > 0);
        await WaitUntilAsync(() => hw.ModeWrites.ToArray().Length > 0);
        await TwoMoreTicksAsync(ipc);
        Assert.Equal([("amdgpu/pwm1", FanMode.Auto)], hw.ModeWrites.ToArray());

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ForgetHardware_InADryRun_RemovesTheEntries_ButTouchesNoFan()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: true);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");

        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key));
        await WaitUntilAsync(() => store.Saves.Count > 0);
        await TwoMoreTicksAsync(ipc);
        await service.StopAsync(cts.Token);

        Assert.Empty(store.Saves[0].Fans);
        Assert.Empty(hw.Writes);
        Assert.Empty(hw.ModeLog);
        Assert.Equal(0, hw.RestoreCount);
    }

    [Fact]
    public async Task ForgetHardware_WhileACalibrationOfOneOfItsFansRuns_IsRefused()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // holds the calibration
        var service = HardwareService(hw, store, ipc, dryRun: false, calibrationDelay: (_, ct) => gate.Task.WaitAsync(ct));
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");
        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "amdgpu/pwm1"));
        await WaitUntilAsync(() => LastSnapshot(ipc)?.Calibration is { Running: true });

        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key));
        await TwoMoreTicksAsync(ipc);

        Assert.Empty(store.Saves);

        gate.SetResult();
        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ForgetHardware_WhileAnIdentificationOfAnotherFanRuns_IsRefused()
    {
        // An identification throttles every controllable fan, not only the one it pulses.
        var hw = GpuRig();
        hw.AddFan("nct/pwm2");
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // holds the pulse
        var service = HardwareService(hw, store, ipc, dryRun: false, identifyDelay: (_, ct) => gate.Task.WaitAsync(ct));
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");
        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "nct/pwm2"));
        await WaitUntilAsync(() => LastSnapshot(ipc)?.Identify is { Running: true });

        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key));
        await TwoMoreTicksAsync(ipc);

        Assert.Empty(store.Saves);

        gate.SetResult();
        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ForgetHardware_WhileACouplingOfAnotherFanRuns_IsRefused()
    {
        // A coupling throttles every controllable fan but the one it drives.
        var hw = GpuRig();
        hw.AddFan("nct/pwm2");
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // holds the coupling
        var service = HardwareService(hw, store, ipc, dryRun: false, couplingDelay: (_, ct) => gate.Task.WaitAsync(ct));
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");
        ipc.Emit(new IpcCommand(IpcCommand.StartTachMapping, Target: "nct/pwm2"));
        await WaitUntilAsync(() => LastSnapshot(ipc)?.TachMapping is { Running: true });

        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key));
        await TwoMoreTicksAsync(ipc);

        Assert.Empty(store.Saves);

        gate.SetResult();
        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ForgetHardware_WhileACalibrationResultWaitsToBeSaved_IsRefused()
    {
        // Saved, the result would put the fan's entry right back. The loop is held inside a snapshot so the result stays
        // pending after the calibration has ended.
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = HardwareService(hw, store, ipc, dryRun: false, log: log, calibrationDelay: NoDelay);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int armed = 1;
        hw.OnGetPwm = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                held.Set();
                release.Wait();
            }
        };

        try
        {
            await WaitUntilAsync(() => held.IsSet);
            ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "amdgpu/pwm1"));

            // Refused as "running" until the calibration ends, then for its unsaved result - never accepted.
            await WaitUntilAsync(() =>
            {
                ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key));
                return log.Snapshot().Any(e => e.Message.Contains("Kalibrierung wird gespeichert", StringComparison.Ordinal));
            }, timeoutMs: 5000);
        }
        finally
        {
            release.Set();
        }

        await WaitUntilAsync(() => store.Saves.Count > 0);
        await TwoMoreTicksAsync(ipc);
        AppConfig saved = Assert.Single(store.Saves); // the calibration, not a forget
        Assert.NotNull(saved.Fans.Single().Calibration);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ForgetHardware_IsNotDroppedByASaveThatFollows()
    {
        var hw = GpuRig();
        var store = GpuStore(OldGpu);
        var ipc = new FakeIpcServer();
        var service = HardwareService(hw, store, ipc, dryRun: true);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        IpcHardwareChange change = await ReportedAsync(ipc, "Replaced");

        ipc.Emit(new IpcCommand(IpcCommand.ForgetHardware, Target: change.Key));
        ipc.Emit(new IpcCommand(IpcCommand.SaveConfig, IpcConfig.Empty with { CaseType = "Server" }));

        await WaitUntilAsync(() => store.Saves.Count >= 2);
        AppConfig final = store.Stored;
        Assert.Equal(CaseType.Server, final.CaseType);
        Assert.Empty(final.Fans);
        Assert.DoesNotContain(final.Sensors, s => s.SensorId == "amdgpu/temp1");

        await service.StopAsync(cts.Token);
    }
}
