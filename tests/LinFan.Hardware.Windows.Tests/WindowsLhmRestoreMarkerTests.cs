// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Hardware.Windows.Lhm;

namespace LinFan.Hardware.Windows.Tests;

/// <summary>
/// Per-process crash markers of <see cref="WindowsLhmBackend"/> (<see cref="LhmRestoreMarker"/>): LHM's
/// SetDefault() and Computer.Close() only restore what LHM saved before its first write in the same process, so
/// after a crashed predecessor in the same boot the backend must force 100 % and keep LHM from writing the saved
/// state back. Boot id and process liveness are injected, so every case is deterministic.
/// </summary>
public sealed class WindowsLhmRestoreMarkerTests : IDisposable
{
    private const long Boot = 2174;
    private const int OwnPid = 4242;
    private const long OwnStart = 777;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"linfan-lhm-marker-{Guid.NewGuid():N}");
    private readonly HashSet<(int, long)> _alive = new();
    private long? _bootId = Boot;

    private string OwnMarker => Path.Combine(_dir, $"lhm-restore.{OwnPid}.{OwnStart}.marker");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private LhmRestoreMarker Marker(string? directory = null, int pid = OwnPid, long start = OwnStart) =>
        new(directory ?? _dir, () => _bootId, pid, start, (p, s) => _alive.Contains((p, s)));

    private static (FakeLhmComputer Lhm, FakeLhmControl Control) OneFan(FakeLhmControl? control = null)
    {
        control ??= new FakeLhmControl();
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", control));
        return (lhm, control);
    }

    private string WriteForeignMarker(int pid, long start, string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, $"lhm-restore.{pid}.{start}.marker");
        File.WriteAllText(path, content);
        return path;
    }

    private static string? Warning(WindowsLhmBackend backend) => ((IBackendDiagnostics)backend).StartupWarning;

    private static FanId OnlyFan(WindowsLhmBackend backend) => backend.DiscoverFans().Single().Id;

    // --- Own marker -------------------------------------------------------------

    [Fact]
    public void FirstManualWrite_CreatesOwnMarker_WithBootId_EvenWhenTheDirectoryIsMissing()
    {
        var (lhm, _) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, Marker());
        Assert.False(Directory.Exists(_dir)); // nothing written yet → no marker

        backend.SetPwm(OnlyFan(backend), 100);

        Assert.Equal("2174", File.ReadAllText(OwnMarker));
    }

    [Fact]
    public void SetModeManual_CreatesOwnMarker()
    {
        var (lhm, _) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, Marker());

        backend.SetMode(OnlyFan(backend), FanMode.Manual);

        Assert.True(File.Exists(OwnMarker));
    }

    [Fact]
    public void MarkerWriteFailure_BlocksTheManualWrite_AndLeavesTheFanOnFirmwareAuto()
    {
        // The marker folder is a regular file → it cannot be created, on every OS.
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "not-a-dir");
        File.WriteAllText(blocker, "");
        var (lhm, control) = OneFan(new FakeLhmControl(LhmControlMode.Default, initialValue: 35f));
        using var backend = new WindowsLhmBackend(lhm, Marker(blocker));
        var id = OnlyFan(backend);

        Assert.Throws<IOException>(() => backend.SetPwm(id, 10));
        Assert.Throws<IOException>(() => backend.SetMode(id, FanMode.Manual));

        Assert.Equal(0, control.SetSoftwareCalls);
        Assert.Equal(LhmControlMode.Default, control.Mode);
        Assert.False(control.RegisterManual);
    }

    // --- Judging other processes' markers at start ---------------------------------

    [Fact]
    public void DeadProcessMarkerFromThisBoot_MakesEveryRestoreFullSpeed_WithoutSetDefault_AndWarns()
    {
        string foreign = WriteForeignMarker(100, 1, "2174");
        var (lhm, control) = OneFan(FakeLhmControl.LeftManualByCrashedPredecessor(20f));
        lhm.Add(FakeLhmSensor.ReadOnlyControl("io/control/2", "Fan Control #2", "SuperIO"));
        using var backend = new WindowsLhmBackend(lhm, Marker());

        backend.RestoreDefaults();

        Assert.Equal(0, control.SetDefaultCalls);
        Assert.Equal(100f, control.RegisterPercent);
        Assert.True(control.RegisterManual);
        Assert.Contains("Schnellstart", Warning(backend));
        Assert.True(File.Exists(foreign)); // dead-process markers stay until the next boot
    }

    [Fact]
    public void LiveProcessMarkerFromThisBoot_IsNoCrashWarning_ButUntrustsOnceThisProcessTouchesTheFans()
    {
        // A live second writer may hold a fan in manual right now; LHM would save that as our "default".
        _alive.Add((100, 1));
        string foreign = WriteForeignMarker(100, 1, "2174");
        var (lhm, control) = OneFan(new FakeLhmControl(LhmControlMode.Default, 30f));
        var backend = new WindowsLhmBackend(lhm, Marker());
        Assert.Null(Warning(backend)); // nothing crashed

        backend.SetPwm(OnlyFan(backend), 50);
        Assert.NotNull(Warning(backend));
        backend.Dispose();

        Assert.Equal(0, control.SetDefaultCalls);
        Assert.Equal(100f, control.RegisterPercent);
        Assert.True(File.Exists(foreign));   // never deletes another process's marker
        Assert.True(File.Exists(OwnMarker)); // untrusted → kept
    }

    [Fact]
    public void ReadOnlyProcess_NeverTouchesTheFans_NorTheMarkers()
    {
        string foreign = WriteForeignMarker(100, 1, "2174"); // a crashed predecessor
        string stale = WriteForeignMarker(101, 1, "2100");
        var (lhm, control) = OneFan(FakeLhmControl.LeftManualByCrashedPredecessor(20f));
        var backend = new WindowsLhmBackend(lhm, Marker());
        _ = backend.GetPwm(OnlyFan(backend));
        _ = backend.GetMode(OnlyFan(backend));

        backend.Dispose();

        Assert.NotNull(Warning(backend)); // still names the crash (e.g. for `list`)
        Assert.Equal(0, control.SetDefaultCalls);
        Assert.Equal(0, control.SetSoftwareCalls);
        Assert.Equal(20f, control.RegisterPercent);
        Assert.True(lhm.Disposed); // a no-op on the chip: nothing was saved
        Assert.False(File.Exists(OwnMarker));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(stale)); // even stale files are only cleaned by a process that touches the fans
    }

    [Fact]
    public void SteadyWrites_DoNotScan_ButTheFirstWriteAfterARestore_SeesANewForeignMarker()
    {
        var marker = Marker();
        var (lhm, control) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, marker);
        var id = OnlyFan(backend);
        backend.SetPwm(id, 50);  // LHM saves here → scanned
        int scans = 0;
        marker.OnScan = () => scans++;

        _alive.Add((100, 1));
        WriteForeignMarker(100, 1, "2174"); // e.g. an elevated CLI started next to the service
        for (byte pwm = 60; pwm < 70; pwm++)
            backend.SetPwm(id, pwm); // no save, so no scan: the saved default predates the CLI's writes
        Assert.Equal(0, scans);

        backend.RestoreDefaults();             // every restore checks afterwards (outside the lock) → sees the CLI
        Assert.Equal(1, control.SetDefaultCalls); // the trusted pass first, then the 100 % pass
        Assert.Equal(100f, control.RegisterPercent);
        Assert.NotNull(Warning(backend));
    }

    [Fact]
    public void Restore_WritesBeforeItLooksAtTheFileSystem()
    {
        // A hung %ProgramData% must not delay the fail-safe: the hand-back is on the chip before any scan starts.
        var marker = Marker();
        var control = new FakeLhmControl(LhmControlMode.Default, 40f);
        var (lhm, _) = OneFan(control);
        using var backend = new WindowsLhmBackend(lhm, marker);
        var id = OnlyFan(backend);
        backend.SetPwm(id, 0);                 // arms (scan) and writes 0 %

        _alive.Add((100, 1));
        WriteForeignMarker(100, 1, "2174");
        var registerAtScan = new List<(bool Manual, float Percent)>();
        marker.OnScan = () => registerAtScan.Add((control.RegisterManual, control.RegisterPercent));

        backend.RestoreDefaults();

        var atScan = Assert.Single(registerAtScan);
        Assert.False(atScan.Manual);           // already back on firmware auto when the scan began
        Assert.Equal(100f, control.RegisterPercent); // and the foreign marker then forced 100 %
    }

    [Fact]
    public void SwitchToUntrusted_RaisesTheRuntimeWarningOnce()
    {
        var marker = Marker();
        var (lhm, _) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, marker);
        var warnings = new List<string>();
        ((IBackendDiagnostics)backend).RuntimeWarning += warnings.Add;

        _alive.Add((100, 1));
        WriteForeignMarker(100, 1, "2174");
        backend.RestoreDefaults(); // start-restore sees the live writer
        backend.SetPwm(OnlyFan(backend), 50);
        backend.RestoreDefaults();

        var warning = Assert.Single(warnings);
        Assert.Contains("100 %", warning);
    }

    [Fact]
    public void RestoreFallback_KeepsTheChannelOnFullSpeed_UntilTheNextWrite()
    {
        // SetDefault was ineffective, and LHM saved its "default" at the 100 % fallback: not a verified state.
        var control = new FakeLhmControl(LhmControlMode.Software, 20f) { IgnoreSetDefault = true };
        var (lhm, _) = OneFan(control);
        using var backend = new WindowsLhmBackend(lhm, Marker());

        backend.RestoreDefaults();             // SetDefault ignored → fallback 100 %
        int setDefaults = control.SetDefaultCalls;
        backend.RestoreDefaults();             // unverified save → 100 % again, SetDefault not trusted

        Assert.Equal(setDefaults, control.SetDefaultCalls);
        Assert.Equal(100f, control.RegisterPercent);

        backend.SetPwm(OnlyFan(backend), 50);  // the next write makes SetDefault() trusted again
        backend.RestoreDefaults();
        Assert.Equal(setDefaults + 1, control.SetDefaultCalls);
    }

    [Fact]
    public void SetPwm_AfterDispose_IsRefused()
    {
        var (lhm, _) = OneFan();
        var backend = new WindowsLhmBackend(lhm, Marker());
        var id = OnlyFan(backend);
        backend.SetPwm(id, 50);
        backend.Dispose();

        Assert.Throws<ObjectDisposedException>(() => backend.SetPwm(id, 50));
    }

    [Fact]
    public void ThrowingRuntimeWarningHandler_IsSwallowed()
    {
        WriteForeignMarker(100, 1, "2174");
        var (lhm, control) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, Marker());
        ((IBackendDiagnostics)backend).RuntimeWarning += _ => throw new InvalidOperationException("log sink down");

        Assert.Null(Record.Exception(() => backend.RestoreDefaults()));
        Assert.Equal(100f, control.RegisterPercent);
    }

    [Fact]
    public void FailSafeRestore_OfAnArmedService_SeesAKilledWritersMarker_OnAFanTheServiceNeverWrote()
    {
        // Audit V1: the service only hands fanX to auto (SetDefault, a no-op in LHM - nothing saved); an elevated
        // CLI set it to 0 % and was killed. The next restore (over-temperature, blind, stop) must not leave it at 0 %.
        var register = new FakeFanRegister { Manual = false, Percent = 40f };
        var serviceControl = new FakeLhmControl(register);
        var cliControl = new FakeLhmControl(register);
        var serviceLhm = new FakeLhmComputer();
        serviceLhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", serviceControl));
        var cliLhm = new FakeLhmComputer();
        cliLhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", cliControl));
        _alive.Add((2, 20));

        var service = new WindowsLhmBackend(serviceLhm, Marker(pid: 2, start: 20));
        service.RestoreDefaults();                               // start-restore: trusted, armed
        var warnings = new List<string>();
        ((IBackendDiagnostics)service).RuntimeWarning += warnings.Add;

        var cli = new WindowsLhmBackend(cliLhm, Marker(pid: 1, start: 10));
        cli.SetPwm(OnlyFan(cli), 0);                             // (1, 10) is not alive: hard-killed afterwards
        Assert.Equal(0f, register.Percent);

        service.RestoreDefaults();

        Assert.True(register.Manual);
        Assert.Equal(100f, register.Percent);
        Assert.Single(warnings);

        service.Dispose();
        Assert.Equal(100f, register.Percent);
        Assert.False(serviceLhm.Disposed);
    }

    [Fact]
    public void ServiceStop_SeesAKilledWritersMarker_Too()
    {
        var register = new FakeFanRegister { Manual = false, Percent = 40f };
        var serviceControl = new FakeLhmControl(register);
        var cliControl = new FakeLhmControl(register);
        var serviceLhm = new FakeLhmComputer();
        serviceLhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", serviceControl));
        var cliLhm = new FakeLhmComputer();
        cliLhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", cliControl));

        var service = new WindowsLhmBackend(serviceLhm, Marker(pid: 2, start: 20));
        service.RestoreDefaults();
        var cli = new WindowsLhmBackend(cliLhm, Marker(pid: 1, start: 10));
        cli.SetPwm(OnlyFan(cli), 0);

        service.Dispose();

        Assert.True(register.Manual);
        Assert.Equal(100f, register.Percent);
    }

    [Fact]
    public void SetModeAuto_OnAChannelWithAnUncheckedSave_GoesToFullSpeed_WithoutSetDefault()
    {
        var control = new FakeLhmControl(LhmControlMode.Software, 20f) { IgnoreSetDefault = true };
        var (lhm, _) = OneFan(control);
        using var backend = new WindowsLhmBackend(lhm, Marker());
        backend.RestoreDefaults();             // SetDefault ignored → 100 % fallback, unverified save
        int setDefaults = control.SetDefaultCalls;

        Assert.Throws<NotSupportedException>(() => backend.SetMode(OnlyFan(backend), FanMode.Auto));

        Assert.Equal(setDefaults, control.SetDefaultCalls);
        Assert.Equal(100f, control.RegisterPercent);
    }

    [Fact]
    public void RestoreWithoutAWritableMarker_TrustsNoDefault()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "not-a-dir");
        File.WriteAllText(blocker, "");
        var (lhm, control) = OneFan(new FakeLhmControl(LhmControlMode.Software, 20f));
        using var backend = new WindowsLhmBackend(lhm, Marker(blocker));

        backend.RestoreDefaults();

        Assert.Equal(0, control.SetDefaultCalls);
        Assert.Equal(100f, control.RegisterPercent);
    }

    [Fact]
    public void LiveSecondWriter_CannotPoisonTheServicesSavedDefault()
    {
        // Audit H1: `linfan set fan1 0` holds the fan, then the service starts and writes curves; the CLI exits,
        // then the service stops. Without the fix the service's SetDefault wrote "manual 0 %" back.
        var register = new FakeFanRegister { Manual = false, Percent = 40f }; // firmware auto
        var cliControl = new FakeLhmControl(register);
        var serviceControl = new FakeLhmControl(register);
        var cliLhm = new FakeLhmComputer();
        cliLhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", cliControl));
        var serviceLhm = new FakeLhmComputer();
        serviceLhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", serviceControl));
        _alive.Add((1, 10));
        _alive.Add((2, 20));

        var cli = new WindowsLhmBackend(cliLhm, Marker(pid: 1, start: 10));
        cli.SetPwm(OnlyFan(cli), 0);
        Assert.Equal(0f, register.Percent);

        var service = new WindowsLhmBackend(serviceLhm, Marker(pid: 2, start: 20));
        service.RestoreDefaults();                // start-restore sees the live CLI marker
        service.SetPwm(OnlyFan(service), 76);     // a curve write: LHM saves the register state here

        cli.Dispose();
        _alive.Remove((1, 10));
        service.SetPwm(OnlyFan(service), 76);
        service.Dispose();

        Assert.True(register.Manual);
        Assert.Equal(100f, register.Percent);     // not 0 %
        Assert.Equal(0, serviceControl.SetDefaultCalls);
        Assert.True(File.Exists(Path.Combine(_dir, "lhm-restore.2.20.marker")));
    }

    [Fact]
    public void LiveSecondWriter_StartedNextToTheService_LeavesNoFanLow()
    {
        var register = new FakeFanRegister { Manual = false, Percent = 40f };
        var serviceControl = new FakeLhmControl(register);
        var cliControl = new FakeLhmControl(register);
        var serviceLhm = new FakeLhmComputer();
        serviceLhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", serviceControl));
        var cliLhm = new FakeLhmComputer();
        cliLhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", cliControl));
        _alive.Add((1, 10));
        _alive.Add((2, 20));

        var service = new WindowsLhmBackend(serviceLhm, Marker(pid: 2, start: 20));
        service.RestoreDefaults();
        service.SetPwm(OnlyFan(service), 76);

        var cli = new WindowsLhmBackend(cliLhm, Marker(pid: 1, start: 10));
        cli.SetPwm(OnlyFan(cli), 0);              // the CLI sees the service marker → untrusted
        service.SetPwm(OnlyFan(service), 76);

        // The stop scans too: the CLI may hold a fan the service never wrote, so the service ends on 100 %.
        service.Dispose();
        Assert.True(register.Manual);
        Assert.Equal(100f, register.Percent);
        cli.Dispose();                            // the untrusted CLI ends on 100 % as well

        Assert.True(register.Manual);
        Assert.Equal(100f, register.Percent);
    }

    [Fact]
    public void ReusedPidWithAnotherStartTime_CountsAsDead()
    {
        _alive.Add((100, 999)); // the pid runs again, but it is a different process
        WriteForeignMarker(100, 1, "2174");
        var (lhm, _) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, Marker());

        Assert.NotNull(Warning(backend));
    }

    [Fact]
    public void MarkerFromAnotherBoot_IsDeletedOnFirstTouch_AndDefaultsAreTrusted()
    {
        string stale = WriteForeignMarker(100, 1, "2173");
        var (lhm, control) = OneFan(new FakeLhmControl(LhmControlMode.Software, 20f));
        using var backend = new WindowsLhmBackend(lhm, Marker());
        Assert.Null(Warning(backend));

        backend.RestoreDefaults();

        Assert.False(File.Exists(stale));
        Assert.True(control.SetDefaultCalls >= 1);
        Assert.Null(Warning(backend));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("-5")]
    public void UnreadableMarker_IsTreatedAsACrash_EvenIfItsProcessLives(string content)
    {
        _alive.Add((100, 1));
        WriteForeignMarker(100, 1, content);
        var (lhm, control) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, Marker());
        Assert.NotNull(Warning(backend)); // the read-only start check already names it

        backend.RestoreDefaults();

        Assert.Equal(0, control.SetDefaultCalls);
        Assert.Equal(100f, control.SoftwareValue);
    }

    [Fact]
    public void UnreadableBootId_TreatsADeadMarkerAsSameBoot_AndKeepsIt()
    {
        _bootId = null;
        string foreign = WriteForeignMarker(100, 1, "2173");
        var (lhm, _) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, Marker());

        Assert.NotNull(Warning(backend));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void UnrelatedFilesInTheFolder_AreIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "lhm-restore.marker"), "1");
        File.WriteAllText(Path.Combine(_dir, "lhm-restore.x.y.marker"), "1");
        var (lhm, _) = OneFan();
        using var backend = new WindowsLhmBackend(lhm, Marker());

        Assert.Null(Warning(backend));
    }

    // --- Dispose: marker and LHM Close() -------------------------------------------

    [Fact]
    public void CleanDispose_DeletesOwnMarker_AndClosesLhm_WhenEveryChannelVerified()
    {
        var (lhm, control) = OneFan();
        var backend = new WindowsLhmBackend(lhm, Marker());
        backend.SetPwm(OnlyFan(backend), 50);
        Assert.True(File.Exists(OwnMarker));

        backend.Dispose();

        Assert.False(control.RegisterManual);
        Assert.False(File.Exists(OwnMarker));
        Assert.True(lhm.Disposed);
    }

    [Fact]
    public void Dispose_KeepsMarker_AndLhmOpen_WhenAChannelFellBackToFullSpeed()
    {
        var (lhm, healthy) = OneFan();
        var ignoring = new FakeLhmControl { IgnoreSetDefault = true };
        lhm.Add(FakeLhmSensor.Controllable("io/control/2", "Fan Control #2", "SuperIO", ignoring));
        var backend = new WindowsLhmBackend(lhm, Marker());
        foreach (var fan in backend.DiscoverFans())
            backend.SetPwm(fan.Id, 50); // the board ignores SetDefault on #2, so it stays in software mode

        backend.Dispose();

        Assert.Equal(LhmControlMode.Default, healthy.Mode);
        Assert.Equal(100f, ignoring.RegisterPercent);
        Assert.True(File.Exists(OwnMarker)); // not verified → the next start in this boot stays on 100 %
        Assert.False(lhm.Disposed);          // Close() must not write LHM's saved state over the 100 %
    }

    [Fact]
    public void UntrustedMode_CleanDispose_EndsAtFullSpeed_AndKeepsTheMarkers()
    {
        // Regression for the audit finding: Computer.Close() would write the predecessor's 20 % back.
        string foreign = WriteForeignMarker(100, 1, "2174");
        var (lhm, control) = OneFan(FakeLhmControl.LeftManualByCrashedPredecessor(20f));
        var backend = new WindowsLhmBackend(lhm, Marker());
        backend.SetPwm(OnlyFan(backend), 50);

        backend.Dispose();

        Assert.True(control.RegisterManual);
        Assert.Equal(100f, control.RegisterPercent);
        Assert.False(lhm.Disposed);
        Assert.True(File.Exists(OwnMarker));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void WithoutMarker_RestoreAfterAWrite_ReturnsToThePredecessorsManualValue()
    {
        // The W3 bug the marker exists for: LHM saved the predecessor's 20 % as the "default", and the verification passes.
        var (lhm, control) = OneFan(FakeLhmControl.LeftManualByCrashedPredecessor(20f));
        var backend = new WindowsLhmBackend(lhm);
        backend.SetPwm(OnlyFan(backend), 50);

        backend.Dispose();

        Assert.True(control.RegisterManual);
        Assert.Equal(20f, control.RegisterPercent);
    }

    // --- SetMode(Auto) in untrusted mode -----------------------------------------

    [Fact]
    public void UntrustedMode_SetModeAuto_WritesFullSpeedOnce_AndReportsNoAutoMode()
    {
        WriteForeignMarker(100, 1, "2174");
        var (lhm, control) = OneFan(FakeLhmControl.LeftManualByCrashedPredecessor(20f));
        using var backend = new WindowsLhmBackend(lhm, Marker());
        var id = OnlyFan(backend);

        Assert.Throws<NotSupportedException>(() => backend.SetMode(id, FanMode.Auto));
        Assert.Equal(1, control.SetSoftwareCalls);
        Assert.Equal(100f, control.RegisterPercent);

        // The loop asks every tick: no further write while the fan is already at our 100 %.
        Assert.Throws<NotSupportedException>(() => backend.SetMode(id, FanMode.Auto));
        Assert.Equal(1, control.SetSoftwareCalls);

        // After a curve drove it lower, the hand-back writes 100 % again.
        backend.SetPwm(id, 60);
        Assert.Throws<NotSupportedException>(() => backend.SetMode(id, FanMode.Auto));
        Assert.Equal(100f, control.RegisterPercent);
        Assert.Equal(0, control.SetDefaultCalls);
    }

    // --- Feature off & diagnostics ---------------------------------------------------

    [Fact]
    public void NoMarker_KeepsTheOldBehavior()
    {
        var (lhm, control) = OneFan(new FakeLhmControl(LhmControlMode.Software, initialValue: 20f));
        var backend = new WindowsLhmBackend(lhm);
        backend.SetPwm(OnlyFan(backend), 50);

        backend.Dispose();

        Assert.True(control.SetDefaultCalls >= 1);
        Assert.Equal(LhmControlMode.Default, control.Mode);
        Assert.Null(Warning(backend));
        Assert.True(lhm.Disposed);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void UntrustedWarning_IsCombinedWithTheGpuOnlyWarning()
    {
        WriteForeignMarker(100, 1, "2174");
        var lhm = new FakeLhmComputer();
        lhm.Add(new FakeLhmSensor
        {
            Identifier = "gpu-nvidia/0/temperature/0",
            Name = "GPU Core",
            HardwareName = "NVIDIA GeForce",
            Type = LhmSensorType.Temperature,
            Value = 50f,
            HardwareType = LhmHardwareType.GpuNvidia,
        });
        using var gpuOnly = new WindowsLhmBackend(lhm);
        string gpuWarning = Warning(gpuOnly)!;

        using var backend = new WindowsLhmBackend(lhm, Marker());

        string combined = Warning(backend)!;
        Assert.StartsWith(gpuWarning, combined);
        Assert.Contains(_dir, combined);
        Assert.Contains("alle LinFan-Prozesse beenden", combined); // deleting a live process marker is unsafe
    }

    [Theory]
    [InlineData("lhm-restore.12.345.marker", true)]
    [InlineData("lhm-restore.marker", false)]
    [InlineData("lhm-restore.12.marker", false)]
    [InlineData("lhm-restore.-1.5.marker", false)]
    [InlineData("other.12.345.marker", false)]
    public void MarkerNames_AreParsedStrictly(string name, bool valid)
    {
        Assert.Equal(valid, LhmRestoreMarker.TryParseName(name, out _, out _));
    }
}
