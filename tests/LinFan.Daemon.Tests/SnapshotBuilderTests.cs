// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>The control state the snapshot carries per fan and globally - taken from the tick, as names.</summary>
public class SnapshotBuilderTests
{
    private static FakeHardware Rig()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 50);
        hw.AddFan("driven");
        hw.AddFan("unconfigured");
        hw.AddFan("readonly", canControl: false, readOnlyReason: FanReadOnlyReason.DriverLocked);
        return hw;
    }

    private static IpcSnapshot Build(FakeHardware hw, ControlTick tick) => Build(hw, hw, tick, AppConfig.Empty);

    private static IpcSnapshot Build(FakeHardware hw, IFanController fans, ControlTick tick, AppConfig config) =>
        SnapshotBuilder.Build(hw, fans, DaemonStatus.Active, dryRun: false, tick, config, ConfigMapper.ToIpc(config),
            configRevision: 1, new HashSet<string>(), calibration: null);

    private static IpcFan FanOf(IpcSnapshot snapshot, string id) => Assert.Single(snapshot.Fans, f => f.Id == id);

    [Fact]
    public void StateNames_AreTheWireContract()
    {
        // Wire contract - only add, never rename: the snapshot carries these member names, and a GUI that does not
        // know a name shows the state as unknown.
        Assert.Equal(
            new[]
            {
                "Curve", "CurveHolding", "Manual", "HardwareAuto", "Calibrating", "Identifying", "IdentifyThrottled",
                "Coupling", "CouplingThrottled", "FailSafe", "Failed", "NotControllable",
            },
            Enum.GetNames<FanControlState>());
        Assert.Equal(new[] { "None", "OverTemperature", "Blind", "Error" }, Enum.GetNames<FailSafeState>());
        Assert.Equal(new[] { "NoWriteAccess", "DriverLocked", "NoControlInterface" }, Enum.GetNames<FanReadOnlyReason>());
        Assert.Equal(new[] { "NotLoaded", "SetAside" }, Enum.GetNames<ConfigLoadProblem>());
        Assert.Equal(new[] { "Stalls", "Slower", "Faster", "OtherSensor" }, Enum.GetNames<CalibrationMismatch>());
    }

    [Fact]
    public void TheSnapshot_CarriesTheDaemonVersion()
    {
        IpcSnapshot snapshot = Build(Rig(), new ControlTick(false, 50, Array.Empty<FanAction>()));

        Assert.Equal(BuildVersion.Current, snapshot.DaemonVersion);
        Assert.NotEqual("0.0.0", BuildVersion.Current); // read from the assembly, not the fallback
    }

    [Fact]
    public void ASensor_CarriesItsHardwareLabel_AlsoWithAnOwnName()
    {
        var config = AppConfig.Empty with { Sensors = [new SensorConfig { SensorId = "t", Name = "CPU" }] };

        var hw = Rig();
        IpcSnapshot snapshot = Build(hw, hw, new ControlTick(false, 50, Array.Empty<FanAction>()), config);

        IpcSensor sensor = Assert.Single(snapshot.Sensors, s => s.Id == "t");
        Assert.Equal(("CPU", "temp"), (sensor.Name, sensor.Label)); // the label is what resetting the name brings back
    }

    [Fact]
    public void ReadOnlyFan_CarriesItsReason_ControllableOnesNone()
    {
        IpcSnapshot snapshot = Build(Rig(), new ControlTick(false, 50, Array.Empty<FanAction>()));

        Assert.Equal("DriverLocked", FanOf(snapshot, "readonly").ReadOnlyReason);
        Assert.Null(FanOf(snapshot, "driven").ReadOnlyReason);
    }

    [Fact]
    public void RegularTick_ReportsEachFansState_AndNoFailSafe()
    {
        var hw = Rig();
        var tick = new ControlTick(false, 50, new[] { FanAction.Applied("driven", "c", 50, 128) });

        IpcSnapshot snapshot = Build(hw, tick);

        Assert.Equal(("Curve", "c"), (FanOf(snapshot, "driven").ControlState, FanOf(snapshot, "driven").ControlCurveId));
        Assert.Equal("HardwareAuto", FanOf(snapshot, "unconfigured").ControlState);
        Assert.Null(FanOf(snapshot, "unconfigured").ControlCurveId);
        Assert.Equal("NotControllable", FanOf(snapshot, "readonly").ControlState);
        Assert.Equal("None", snapshot.FailSafe);
        Assert.Equal(50, snapshot.HottestTempC);
    }

    [Fact]
    public void FailSafeTick_ReportsTheCause_AndFailSafeForEveryControllableFan()
    {
        var hw = Rig();
        var tick = new ControlTick(true, 95, Array.Empty<FanAction>(), "hot", FailSafeState.OverTemperature);

        IpcSnapshot snapshot = Build(hw, tick);

        Assert.Equal("OverTemperature", snapshot.FailSafe);
        Assert.Equal("FailSafe", FanOf(snapshot, "driven").ControlState);
        Assert.Equal("FailSafe", FanOf(snapshot, "unconfigured").ControlState);
        Assert.Equal("NotControllable", FanOf(snapshot, "readonly").ControlState);
    }

    /// <summary>
    /// While a write is wedged the fan controller gives up reading after a bounded wait. That fan's values are unknown
    /// for this snapshot - the snapshot itself still goes out, with every other fan and the tick's states.
    /// </summary>
    [Fact]
    public void AFanWhoseReadsTimeOut_DoesNotCostTheSnapshot()
    {
        var hw = Rig();
        hw.Pwm["unconfigured"] = 99;
        var fans = new TimingOutReads(hw, "driven");
        var tick = new ControlTick(false, 50, new[] { FanAction.Applied("driven", "c", 50, 128) });

        IpcSnapshot snapshot = Build(hw, fans, tick, AppConfig.Empty);

        IpcFan driven = FanOf(snapshot, "driven");
        Assert.Equal((0, SnapshotBuilder.UnknownMode, "Curve"), (driven.Pwm, driven.Mode, driven.ControlState));
        Assert.Equal(99, FanOf(snapshot, "unconfigured").Pwm);
    }

    [Fact]
    public void AFanWhoseModeCannotBeRead_ShowsItAsUnknown_NotAsAuto()
    {
        var hw = Rig();
        hw.Pwm["driven"] = 128;
        var fans = new UnreadableMode(hw, "driven");
        var tick = new ControlTick(false, 50, new[] { FanAction.Applied("driven", "c", 50, 128) });

        IpcSnapshot snapshot = Build(hw, fans, tick, AppConfig.Empty);

        IpcFan driven = FanOf(snapshot, "driven");
        Assert.Equal((128, SnapshotBuilder.UnknownMode), (driven.Pwm, driven.Mode)); // the PWM it could read stays
    }

    private sealed class UnreadableMode(FakeHardware inner, string fanId) : IFanController
    {
        public IReadOnlyList<FanDescriptor> DiscoverFans() => inner.DiscoverFans();
        public bool CanControl(FanId id) => inner.CanControl(id);
        public FanMode GetMode(FanId id) => id.Value == fanId ? throw new IOException("EIO") : inner.GetMode(id);
        public byte GetPwm(FanId id) => inner.GetPwm(id);
        public void SetMode(FanId id, FanMode mode) => inner.SetMode(id, mode);
        public void SetPwm(FanId id, byte value) => inner.SetPwm(id, value);
        public void RestoreDefaults() => inner.RestoreDefaults();
        public void Dispose() { }
    }

    private sealed class TimingOutReads(FakeHardware inner, string fanId) : IFanController
    {
        public IReadOnlyList<FanDescriptor> DiscoverFans() => inner.DiscoverFans();
        public bool CanControl(FanId id) => inner.CanControl(id);
        public FanMode GetMode(FanId id) => id.Value == fanId ? throw new TimeoutException() : inner.GetMode(id);
        public byte GetPwm(FanId id) => id.Value == fanId ? throw new TimeoutException() : inner.GetPwm(id);
        public void SetMode(FanId id, FanMode mode) => inner.SetMode(id, mode);
        public void SetPwm(FanId id, byte value) => inner.SetPwm(id, value);
        public void RestoreDefaults() => inner.RestoreDefaults();
        public void Dispose() { }
    }

    [Fact]
    public void AFan_CarriesItsSpeedSensor_AndNotAnotherFansCoupledOne()
    {
        // pwm2's backend pairing is fan1, but pwm1 is coupled to fan1 explicitly: one sensor, one fan.
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 50);
        hw.AddFanSensor("hwmon7/fan1", 1200);
        hw.AddFanSensor("hwmon7/fan2", 800);
        hw.AddFan("hwmon7/pwm1", tachId: null);
        hw.AddFan("hwmon7/pwm2", tachId: "hwmon7/fan1");
        hw.AddFan("hwmon7/pwm3", tachId: "hwmon7/fan2");
        var config = AppConfig.Empty with { Fans = [new FanConfig { FanId = "hwmon7/pwm1", RpmSource = "hwmon7/fan1" }] };

        IpcSnapshot snapshot = Build(hw, hw, new ControlTick(false, 50, Array.Empty<FanAction>()), config);

        Assert.Equal(("hwmon7/fan1", (double?)1200), (FanOf(snapshot, "hwmon7/pwm1").Tachometer, FanOf(snapshot, "hwmon7/pwm1").Rpm));
        Assert.Equal((null, (double?)null), (FanOf(snapshot, "hwmon7/pwm2").Tachometer, FanOf(snapshot, "hwmon7/pwm2").Rpm));
        Assert.Equal("hwmon7/fan2", FanOf(snapshot, "hwmon7/pwm3").Tachometer); // the pairing still counts
    }

    // --- The calibration check ----------------------------------------------------------------------------------------

    /// <summary>A fan on its curve at PWM 128, turning at 600 RPM where its calibration saw 1200.</summary>
    private static (FakeHardware Hw, AppConfig Config, ControlTick Tick) SlowFan(string? calibratedWith = "hwmon7/fan1")
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 50);
        hw.AddFanSensor("hwmon7/fan1", 600);
        hw.AddFan("hwmon7/pwm1", tachId: "hwmon7/fan1");
        hw.Pwm["hwmon7/pwm1"] = 128;
        var cal = new FanCalibration
        {
            StartPwm = 64,
            MinRpm = 800,
            MaxRpm = 1200,
            Tachometer = calibratedWith,
            Samples = [new(0, 0), new(32, 0), new(64, 800), new(96, 1000), new(128, 1200)],
        };
        var config = AppConfig.Empty with { Fans = [new FanConfig { FanId = "hwmon7/pwm1", MinPwm = 64, Calibration = cal }] };
        return (hw, config, new ControlTick(false, 50, [FanAction.Applied("hwmon7/pwm1", "c", 50, 128)]));
    }

    /// <summary>Builds a snapshot per tick, 5 s apart, for as long as a calibration mismatch takes, and checks each.</summary>
    private static IpcSnapshot CheckForAMinuteAndMore(
        FakeHardware hw, IFanController fans, ControlTick tick, AppConfig config, bool dryRun = false)
    {
        var check = new CalibrationPlausibility(TimeSpan.FromSeconds(5), time: new SteppingClock(TimeSpan.FromSeconds(5)));
        IpcSnapshot snapshot = null!;
        for (int i = 0; i < 16; i++)
            snapshot = SnapshotBuilder.WithCalibrationMismatches(Build(hw, fans, tick, config), check, tick, config, dryRun);
        return snapshot;
    }

    [Fact]
    public void ASlowFan_CarriesItsCalibrationMismatch_FromWhatTheSnapshotRead()
    {
        (FakeHardware hw, AppConfig config, ControlTick tick) = SlowFan();
        int pwmReads = hw.GetPwmCalls, valueReads = hw.ReadValueCalls;

        IpcSnapshot snapshot = CheckForAMinuteAndMore(hw, hw, tick, config);

        Assert.Equal("Slower", FanOf(snapshot, "hwmon7/pwm1").CalibrationMismatch);
        // Sixteen snapshots: one PWM read for the one fan and one value read per sensor each, nothing for the check.
        Assert.Equal((16, 16 * 2), (hw.GetPwmCalls - pwmReads, hw.ReadValueCalls - valueReads));
    }

    [Fact]
    public void ACalibrationMeasuredWithAnotherSensor_IsReportedWithTheFirstSnapshot()
    {
        (FakeHardware hw, AppConfig config, ControlTick tick) = SlowFan(calibratedWith: "hwmon7/fan2");
        var check = new CalibrationPlausibility(TimeSpan.FromSeconds(1));

        IpcSnapshot snapshot = SnapshotBuilder.WithCalibrationMismatches(Build(hw, hw, tick, config), check, tick, config, dryRun: false);

        Assert.Equal("OtherSensor", FanOf(snapshot, "hwmon7/pwm1").CalibrationMismatch);
    }

    [Fact]
    public void TheCheck_TakesTheStateFromTheTick_TheModeFromTheSnapshot_AndTheDryRun()
    {
        (FakeHardware hw, AppConfig config, ControlTick tick) = SlowFan();
        var handedBack = new ControlTick(false, 50, []);                                   // hardware auto
        // Kept while no temperature is readable - here with the fan still listed on its curve, so only the flag tells.
        var failSafe = new ControlTick(false, double.NaN, tick.Actions, FailSafe: FailSafeState.Blind);

        Assert.Null(FanOf(CheckForAMinuteAndMore(hw, hw, handedBack, config), "hwmon7/pwm1").CalibrationMismatch);
        Assert.Null(FanOf(CheckForAMinuteAndMore(hw, hw, failSafe, config), "hwmon7/pwm1").CalibrationMismatch);
        Assert.Null(FanOf(CheckForAMinuteAndMore(hw, new UnreadableMode(hw, "hwmon7/pwm1"), tick, config), "hwmon7/pwm1")
            .CalibrationMismatch);
        Assert.Null(FanOf(CheckForAMinuteAndMore(hw, hw, tick, config, dryRun: true), "hwmon7/pwm1").CalibrationMismatch);
    }

    [Fact]
    public void WithoutAFinding_TheSnapshotIsTheSame()
    {
        (FakeHardware hw, AppConfig config, ControlTick tick) = SlowFan();
        IpcSnapshot snapshot = Build(hw, hw, tick, config);

        Assert.Same(snapshot, SnapshotBuilder.WithCalibrationMismatches(
            snapshot, new CalibrationPlausibility(TimeSpan.FromSeconds(1)), tick, config, dryRun: false));
    }
}
