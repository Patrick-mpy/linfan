// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Tests für den Daemon-Orchestrator <see cref="ControlLoopService"/>. Der Dienst wird mit Fakes
/// konstruiert und über <see cref="BackgroundService.StartAsync"/> gestartet; danach hält der
/// <see cref="FakeIpcServer"/> die <c>OnCommand</c>-Delegate, sodass Kommandos ohne Tick-Wartezeit
/// per <see cref="FakeIpcServer.Emit"/> ausgelöst werden. Effekte, die erst im nächsten Tick
/// persistiert werden (Pending-Config), werden per Polling mit Timeout abgewartet (keine festen
/// Delays). Jeder Test stoppt den Dienst sauber (StopAsync), damit kein Hintergrund-Loop leckt.
/// <para>Hinweis Test-Seams (verhaltensneutral, Default unverändert; analog zu den schon vorhandenen
/// Seams in <c>CalibrationCoordinator</c>): <c>ControlLoopService</c> hat zwei optionale Ctor-Parameter
/// - <c>TimeSpan? tickInterval</c> (null = Config-Intervall; hier kurz für schnelle Ticks) und eine
/// <c>CalibrationService</c>-Factory (null = echte Delays; hier Null-Delay für die Kalibrier-Tests).</para>
/// <para>Tests laufen ohne Root → der ControlLoop arbeitet im Dry-Run und schreibt KEINE PWM auf die
/// (Fake-)Hardware. Manuelle Steuerung wird daher über das <c>ManualOverride</c>-Flag des Snapshots
/// beobachtet, nicht über <c>FakeHardware.Writes</c>.</para>
/// </summary>
public class ControlLoopServiceTests
{
    private static readonly TimeSpan FastTick = TimeSpan.FromMilliseconds(15);

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Bedingung nicht innerhalb des Timeouts erfüllt.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    /// <summary>Letzter Snapshot oder <c>null</c>, solange noch nichts gebroadcastet wurde (race-sicher).</summary>
    private static IpcSnapshot? LastSnapshot(FakeIpcServer ipc)
    {
        List<IpcSnapshot> b = ipc.Broadcasts;
        return b.Count == 0 ? null : b[^1];
    }

    private static bool FanIsManual(FakeIpcServer ipc, string fanId) =>
        LastSnapshot(ipc) is { } s && s.Fans.Any(f => f.Id == fanId && f.ManualOverride);

    /// <summary>Standard-Rig: ein steuerbarer Lüfter mit Tacho + ein Temperatursensor.</summary>
    private static FakeHardware FanRig(bool canControl = true)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 40);
        hw.AddFanSensor("hwmon7/fan1", 0);
        hw.AddFan("hwmon7/pwm1", canControl, tachId: "hwmon7/fan1");
        hw.TachId = "hwmon7/fan1";
        hw.RpmForPwm = pwm => pwm < 96 ? 0 : 300 + pwm * 4;
        return hw;
    }

    private static ControlLoopService NewService(
        FakeHardware hw, FakeConfigStore store, FakeIpcServer ipc, bool fastCalibration = false,
        Func<TimeSpan, CancellationToken, Task>? identifyDelay = null,
        Func<TimeSpan, CancellationToken, Task>? calibrationDelay = null,
        TimeSpan? coordinatorStopTimeout = null, ILogger<ControlLoopService>? log = null,
        bool fastTachMapping = false) =>
        // dryRunOverride: true - die Tests laufen konzeptionell ohne Root (Dry-Run, keine echten PWM-Writes).
        // Explizit erzwungen, damit der Test unabhängig von der euid des Runners ist (CI-Container = Root).
        new(hw, hw, store, ipc, log ?? NullLogger<ControlLoopService>.Instance, FastTick,
            calibrationDelay is not null ? (s, f) => new CalibrationService(s, f, calibrationDelay)
                : fastCalibration ? (s, f) => new CalibrationService(s, f, NoDelay) : null,
            identifyDelay, coordinatorStopTimeout,
            tachMappingFactory: fastTachMapping ? (s, f) => new TachometerMappingService(s, f, NoDelay) : null,
            dryRunOverride: true);

    /// <summary>Startet den Dienst und wartet, bis ExecuteAsync seine IPC-Handler gesetzt hat.</summary>
    private static async Task StartAndWaitReadyAsync(ControlLoopService service, FakeIpcServer ipc, CancellationToken ct)
    {
        await service.StartAsync(ct).ConfigureAwait(false);
        await WaitUntilAsync(() => ipc.CommandHandler is not null && ipc.ClientsChanged is not null).ConfigureAwait(false);
    }

    private static IpcConfig IpcWith(params IpcFanAssignment[] fans) => IpcConfig.Empty with { Fans = fans };

    // --- ExecuteAsync: Setup, Handler, Broadcast pro Tick ----------------------------------------

    [Fact]
    public async Task ExecuteAsync_SetsHandlers_StartsIpc_AndBroadcastsPerTick()
    {
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        // Handler sind gesetzt (so kann die GUI Kommandos und Disconnects auslösen).
        Assert.NotNull(ipc.CommandHandler);
        Assert.NotNull(ipc.ClientsChanged);

        // Pro Tick wird genau ein Snapshot gebroadcastet → die Liste wächst über die Zeit.
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2);
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count > seen);

        IpcSnapshot snapshot = ipc.Broadcasts[0];
        Assert.Equal(DaemonStatus.DryRun, snapshot.Status); // Tests laufen ohne Root
        Assert.True(snapshot.DryRun);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Stop_RestoresDefaults_AndHaltsTicks()
    {
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 1);

        await service.StopAsync(cts.Token);

        // Fail-Safe beim Beenden: RestoreDefaults ausgeführt …
        Assert.True(hw.RestoreCount >= 1);

        // … und es kommen keine weiteren Broadcasts mehr (Loop steht).
        int after = ipc.Broadcasts.Count;
        await Task.Delay(60);
        Assert.Equal(after, ipc.Broadcasts.Count);
    }

    // --- LoadAndMigrate: persistiert bei Migration -----------------------------------------------

    [Fact]
    public async Task LoadAndMigrate_SeedsDefaultProfile_AndSaves()
    {
        var hw = FanRig();
        // Altbestand ohne Profile → LoadAndMigrate muss ein Default-Profil anlegen UND speichern.
        var store = new FakeConfigStore { Stored = AppConfig.Empty with { Profiles = Array.Empty<Profile>() } };
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        await WaitUntilAsync(() => store.Saves.Count >= 1); // Migration persistiert
        AppConfig saved = store.Saves[0];
        Assert.NotEmpty(saved.Profiles);
        Assert.NotNull(saved.ActiveProfileId);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task LoadAndMigrate_AlreadyMigrated_DoesNotSaveOnStartup()
    {
        var hw = FanRig();
        // Bereits vollständig migrierte Config (ein Profil mit Kurven, gültiges aktives Profil).
        var profile = new Profile
        {
            Id = "default",
            Name = "Standard",
            Curves = new[]
            {
                new CurveConfig { Id = "c1", Name = "C", SourceSensorIds = new[] { "t" }, Points = new[] { new CurvePoint(40, 50) } },
            },
            Assignments = Array.Empty<ProfileAssignment>(),
        };
        var store = new FakeConfigStore
        {
            // Vollständig migrierte Config: Profil + gültiges aktives Profil + Onboarding-Flag bereits gesetzt,
            // und die Datei existiert bereits (Exists=true) - es gibt also nichts mehr zu initialisieren.
            Stored = AppConfig.Empty with
            {
                Profiles = new[] { profile },
                ActiveProfileId = "default",
                OnboardingCompleted = true,
            },
            Exists = true,
        };
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2); // mehrere Ticks gelaufen

        Assert.Empty(store.Saves); // keine Migration → kein Save (nur Ticks ohne Pending-Änderung)

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task LoadAndMigrate_FreshInstall_SetsOnboardingCompletedFalse()
    {
        var hw = FanRig();
        // Frische Installation: keine Config-Datei (Exists=false) → Assistent soll laufen (false).
        var store = new FakeConfigStore { Stored = AppConfig.Empty, Exists = false };
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        await WaitUntilAsync(() => store.Saves.Count >= 1); // Erstinitialisierung wird persistiert
        Assert.False(store.Saves[0].OnboardingCompleted); // First-Run-Signal

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task LoadAndMigrate_ExistingConfigWithoutFlag_SetsOnboardingCompletedTrue()
    {
        var hw = FanRig();
        // Altbestand: Datei existiert (Exists=true), aber ohne Profile und ohne Onboarding-Flag (null).
        // Bestandsnutzer sollen NICHT genervt werden → Flag wird auf true gesetzt.
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with { Profiles = Array.Empty<Profile>() },
            Exists = true,
        };
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        await WaitUntilAsync(() => store.Saves.Count >= 1);
        Assert.True(store.Saves[0].OnboardingCompleted);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task LoadAndMigrate_RewritesLegacyHwmonIds_AndPersists()
    {
        var hw = FanRig();
        hw.LegacyIds["hwmon7/pwm1"] = "nct6797/pwm1"; // Backend bietet die stabile Zuordnung an
        // Altbestand (Schema 2) mit instabiler hwmonN-Id in einer Lüfter-Zuordnung.
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with
            {
                SchemaVersion = 2,
                Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU" } },
                Profiles = new[]
                {
                    new Profile
                    {
                        Id = "default", Name = "Standard",
                        Curves = new[] { new CurveConfig { Id = "c1", Name = "C", Points = new[] { new CurvePoint(40, 50) } } },
                        Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", null) },
                    },
                },
                ActiveProfileId = "default",
                OnboardingCompleted = true,
            },
            Exists = true,
        };
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        await WaitUntilAsync(() => store.Saves.Count >= 1); // Migration persistiert die stabilen Ids
        AppConfig saved = store.Saves[0];
        Assert.Equal(3, saved.SchemaVersion);
        Assert.Equal("nct6797/pwm1", saved.Fans[0].FanId);
        Assert.Equal("nct6797/pwm1", saved.Profiles[0].Assignments[0].FanId);

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: SaveConfig → ApplyPendingConfigChanges persistiert ----------------------------

    [Fact]
    public async Task OnCommand_SaveConfig_MergesAndPersists()
    {
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.Saves.Clear(); // evtl. Startup-Migrationsspeicherung ignorieren

        var incoming = IpcWith(new IpcFanAssignment("hwmon7/pwm1", "CPU", 30, 220, "c1"));
        ipc.Emit(new IpcCommand(IpcCommand.SaveConfig, Config: incoming));

        // Der nächste Tick übernimmt die Pending-Config und persistiert sie.
        await WaitUntilAsync(() => store.Saves.Any(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1" && f.Name == "CPU")));
        AppConfig saved = store.Saves.Last(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1"));
        FanConfig fan = saved.Fans.Single(f => f.FanId == "hwmon7/pwm1");
        Assert.Equal("CPU", fan.Name);
        Assert.Equal((byte)30, fan.MinPwm);
        Assert.Equal((byte)220, fan.MaxPwm);

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: SetFanTachometer → RpmSource-Override persistiert -----------------------------

    [Fact]
    public async Task OnCommand_SetFanTachometer_PersistsRpmSource()
    {
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.Saves.Clear();

        ipc.Emit(new IpcCommand(IpcCommand.SetFanTachometer, Target: "hwmon7/pwm1", RpmSource: "hwmon7/fan9"));

        await WaitUntilAsync(() => store.Saves.Any(s =>
            s.Fans.Any(f => f.FanId == "hwmon7/pwm1" && f.RpmSource == "hwmon7/fan9")));
        AppConfig saved = store.Saves.Last(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1"));
        Assert.Equal("hwmon7/fan9", saved.Fans.Single(f => f.FanId == "hwmon7/pwm1").RpmSource);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_SetFanTachometer_Empty_ClearsRpmSource()
    {
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        ipc.Emit(new IpcCommand(IpcCommand.SetFanTachometer, Target: "hwmon7/pwm1", RpmSource: "hwmon7/fan9"));
        await WaitUntilAsync(() => store.Saves.Any(s =>
            s.Fans.Any(f => f.FanId == "hwmon7/pwm1" && f.RpmSource == "hwmon7/fan9")));
        store.Saves.Clear();

        ipc.Emit(new IpcCommand(IpcCommand.SetFanTachometer, Target: "hwmon7/pwm1", RpmSource: null));

        await WaitUntilAsync(() => store.Saves.Any(s =>
            s.Fans.Any(f => f.FanId == "hwmon7/pwm1" && f.RpmSource == null)));
        AppConfig saved = store.Saves.Last(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1"));
        Assert.Null(saved.Fans.Single(f => f.FanId == "hwmon7/pwm1").RpmSource);

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: StartTachMapping → gekoppeltes RpmSource persistiert --------------------------

    [Fact]
    public async Task OnCommand_StartTachMapping_PersistsMatchedRpmSource()
    {
        var hw = FanRig();          // hwmon7/pwm1 mit Tacho hwmon7/fan1, dessen RPM mit PWM steigt
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc, fastTachMapping: true);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.Saves.Clear();

        ipc.Emit(new IpcCommand(IpcCommand.StartTachMapping, Target: "hwmon7/pwm1"));

        // Coordinator koppelt (NoDelay) und queued das Ergebnis; der nächste Tick persistiert das Override.
        await WaitUntilAsync(() => store.Saves.Any(s =>
            s.Fans.Any(f => f.FanId == "hwmon7/pwm1" && f.RpmSource == "hwmon7/fan1")));
        AppConfig saved = store.Saves.Last(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1"));
        Assert.Equal("hwmon7/fan1", saved.Fans.Single(f => f.FanId == "hwmon7/pwm1").RpmSource);

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: ReplaceConfig / ResetConfig ---------------------------------------------------

    [Fact]
    public async Task OnCommand_ReplaceConfig_ReplacesWholesale_WithIncomingCalibration()
    {
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.Saves.Clear();

        int restoresBefore = hw.RestoreCount;
        var incoming = IpcWith(new IpcFanAssignment("hwmon7/pwm1", "Neu", 30, 220, "c1",
            Calibration: new IpcFanCalibration(96, 400, 1800)));
        ipc.Emit(new IpcCommand(IpcCommand.ReplaceConfig, Config: incoming));

        await WaitUntilAsync(() => store.Saves.Any(s => s.Fans.Any(f => f.Calibration is not null)));
        AppConfig saved = store.Saves.Last(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1"));
        FanConfig fan = Assert.Single(saved.Fans); // Voll-Ersetzen: nur der eingehende Lüfter
        Assert.Equal("Neu", fan.Name);
        Assert.NotNull(fan.Calibration);
        Assert.Equal((byte)96, fan.Calibration!.StartPwm); // eingehende Kalibrierung übernommen
        // Fail-Safe: abgehängte Lüfter kommen über RestoreDefaults zurück auf Firmware-Auto (nicht im Manual hängen).
        Assert.True(hw.RestoreCount > restoresBefore);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_ResetConfig_ClearsToFactory_AndPersists()
    {
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        // Erst eine nicht-leere Config setzen, damit der Reset etwas zu leeren hat.
        ipc.Emit(new IpcCommand(IpcCommand.ReplaceConfig,
            Config: IpcWith(new IpcFanAssignment("hwmon7/pwm1", "CPU", 30, 220, "c1"))));
        await WaitUntilAsync(() => store.Saves.Any(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1")));
        store.Saves.Clear();
        int restoresBefore = hw.RestoreCount;

        ipc.Emit(new IpcCommand(IpcCommand.ResetConfig));

        await WaitUntilAsync(() => store.Saves.Any(s => s.OnboardingCompleted == true && s.Fans.Count == 0));
        AppConfig reset = store.Saves.Last(s => s.OnboardingCompleted == true);
        Assert.Empty(reset.Fans);       // Lüfter-Overrides weg (rohe Hardware-Namen)
        Assert.Empty(reset.Sensors);    // Sensor-Overrides weg
        Assert.True(reset.OnboardingCompleted); // kein erzwungenes Onboarding
        // Fail-Safe: der Werksreset stellt alle Lüfter auf Firmware-Auto zurück (sonst blieben sie im Manual hängen).
        Assert.True(hw.RestoreCount > restoresBefore);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_ResetConfig_MidCalibrationRamp_CancelsAndLeavesFanOnAuto()
    {
        // Deckt die vom Fail-Safe-Audit empfohlene Race ab: Reset trifft eine Kalibrierung MITTEN in der Rampe.
        // Der Terminal-Zustand des Ziel-Lüfters muss Firmware-Auto sein (nicht im Manual bei fixem PWM hängen).
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();

        // Kalibrier-Delay, das den Start des ersten Rampenschritts meldet und dann hängt - bis der Coordinator
        // (via Reset → Cancel) das Token abbricht. So steht der Lüfter garantiert mitten in der Rampe in Manual.
        var rampStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<TimeSpan, CancellationToken, Task> gatingDelay = async (_, ct) =>
        {
            rampStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // blockiert bis Cancel → OperationCanceledException
        };
        var service = NewService(hw, store, ipc, calibrationDelay: gatingDelay);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.Saves.Clear();

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "hwmon7/pwm1"));
        await rampStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(FanMode.Manual, hw.ModeLog["hwmon7/pwm1"]); // mitten in der Rampe: Manual

        int restoresBefore = hw.RestoreCount;
        ipc.Emit(new IpcCommand(IpcCommand.ResetConfig));

        // Reset bricht die Kalibrierung ab und stellt (über RestoreDefaults) alle Lüfter auf Auto.
        await WaitUntilAsync(() => store.Saves.Any(s => s.OnboardingCompleted == true) && hw.RestoreCount > restoresBefore);
        await WaitUntilAsync(() => hw.ModeLog.TryGetValue("hwmon7/pwm1", out FanMode m) && m == FanMode.Auto);
        Assert.Equal(FanMode.Auto, hw.ModeLog["hwmon7/pwm1"]); // Terminal-Zustand: Firmware-Auto, nicht Manual
        Assert.NotNull(ipc.CommandHandler); // Dienst läuft weiter (kein Absturz durch den Abbruch)

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task StopAsync_DoesNotHang_WhenCoordinatorStopIsWedged()
    {
        // Fail-Safe: hängt ein Coordinator-Stop (z. B. hinter einem im Kernel festhängenden Write), darf
        // der Shutdown nicht ewig darauf warten - sonst käme das abschließende finally-RestoreDefaults nie
        // dran und systemd SIGKILLt ohne Fail-Safe. StopAsync wartet begrenzt und fährt fort.
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Identify-Delay, das den Cancellation-Token bewusst IGNORIERT und hängt → der Identify-Stop läuft
        // nie von selbst zu Ende (simuliert einen wedged Hardware-Write, der den Stop blockiert).
        Func<TimeSpan, CancellationToken, Task> wedgedDelay = async (_, __) =>
        {
            entered.TrySetResult();
            await unblock.Task;
        };
        var service = NewService(hw, store, ipc, identifyDelay: wedgedDelay,
            coordinatorStopTimeout: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "hwmon7/pwm1"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); // Identify läuft und hängt im Delay

        int restoresBefore = hw.RestoreCount;

        // Ohne die begrenzte Wartezeit hinge dieser Await ewig (der Stop wartet auf den wedged Delay). Die
        // äußere WaitAsync-Grenze lässt den Test bei einer Regression scheitern statt die Suite zu blockieren.
        await service.StopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(hw.RestoreCount > restoresBefore,
            "Das finally-RestoreDefaults muss trotz hängendem Coordinator-Stop gelaufen sein.");

        unblock.TrySetResult(); // den hängenden Delay auflösen, damit der Coordinator-Task sauber endet
    }

    // --- OnCommand: SetManualPwm / SetFanAuto -----------------------------------------------------

    [Fact]
    public async Task OnCommand_SetManualPwm_OnControllableFan_MarksFanManual()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 128));

        // Der manuelle Override greift im nächsten Tick → der Lüfter ist im Snapshot als manuell markiert
        // (im Dry-Run wird keine PWM auf die Hardware geschrieben, daher das Flag statt hw.Writes).
        await WaitUntilAsync(() => FanIsManual(ipc, "hwmon7/pwm1"));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_SetManualPwm_OnUncontrollableFan_DoesNotApply()
    {
        var hw = FanRig(canControl: false);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 128));

        // Mehrere Ticks vergehen lassen, dann sicherstellen: nie als manuell markiert (abgelehnt) und
        // kein Schreibversuch.
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        Assert.False(FanIsManual(ipc, "hwmon7/pwm1"));
        Assert.DoesNotContain(hw.Writes, w => w.Fan == "hwmon7/pwm1");

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_SetFanAuto_ClearsManualOverride()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 100));
        await WaitUntilAsync(() => FanIsManual(ipc, "hwmon7/pwm1")); // erst manuell

        ipc.Emit(new IpcCommand(IpcCommand.SetFanAuto, Target: "hwmon7/pwm1"));

        // Nach Auto darf der Lüfter nicht mehr als manuell markiert sein.
        await WaitUntilAsync(() => !FanIsManual(ipc, "hwmon7/pwm1"));

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: Calibration -------------------------------------------------------------------

    [Fact]
    public async Task OnCommand_StartCalibration_SetsCalibrationStatus()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "hwmon7/pwm1"));

        // Die Kalibrierung wird gestartet → erscheint im Snapshot (Calibration != null, korrekte Fan-Id).
        await WaitUntilAsync(() => ipc.Broadcasts.Any(b => b.Calibration is { } c && c.FanId == "hwmon7/pwm1"));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_StartCalibration_PersistsResult_AfterCompletion()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc, fastCalibration: true); // Null-Delay → Rampe sofort fertig
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.Saves.Clear();

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "hwmon7/pwm1"));

        // Nach Abschluss der Rampe wird das Ergebnis im Tick-Loop persistiert: eine Config mit
        // Kalibrierung für den Lüfter (ApplyCalibration-Pfad in ApplyPendingConfigChanges).
        await WaitUntilAsync(
            () => store.Saves.Any(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1" && f.Calibration is not null)));

        FanConfig fan = store.Saves
            .Last(s => s.Fans.Any(f => f.FanId == "hwmon7/pwm1" && f.Calibration is not null))
            .Fans.Single(f => f.FanId == "hwmon7/pwm1");
        Assert.NotNull(fan.Calibration);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_StartCalibration_OnUncontrollableFan_DoesNotStart()
    {
        var hw = FanRig(canControl: false);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "hwmon7/pwm1"));

        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        Assert.All(ipc.Broadcasts, b => Assert.Null(b.Calibration)); // nie gestartet

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_CancelCalibration_StopsRunning()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "hwmon7/pwm1"));
        await WaitUntilAsync(() => ipc.Broadcasts.Any(b => b.Calibration is { } c && c.FanId == "hwmon7/pwm1"));

        ipc.Emit(new IpcCommand(IpcCommand.CancelCalibration));

        // Nach dem Abbruch ist die Kalibrierung im Snapshot nicht mehr "Running".
        await WaitUntilAsync(() => LastSnapshot(ipc) is { Calibration: var cal } && (cal is null || !cal.Running));

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: Identify ----------------------------------------------------------------------
    [Fact]
    public async Task OnCommand_Identify_OnControllableFan_DrivesTargetTo255()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc, identifyDelay: NoDelay); // Puls läuft instantan
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "hwmon7/pwm1"));

        await WaitUntilAsync(() => hw.Writes.Any(w => w == ("hwmon7/pwm1", (byte)255)));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_Identify_OnUncontrollableFan_DoesNotDrive()
    {
        var hw = FanRig(canControl: false);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc, identifyDelay: NoDelay);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "hwmon7/pwm1"));

        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        Assert.DoesNotContain(hw.Writes, w => w.Pwm == 255); // nicht steuerbar → abgelehnt

        await service.StopAsync(cts.Token);
    }

    /// <summary>Zwei-Lüfter-Rig: A hat einen Tacho (kalibrierbar), B nicht.</summary>
    private static FakeHardware TwoFanRig()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 40);
        hw.AddFanSensor("hwmon7/fan1", 0);
        hw.AddFan("A", canControl: true, tachId: "hwmon7/fan1");
        hw.AddFan("B", canControl: true);
        hw.TachId = "hwmon7/fan1";
        hw.RpmForPwm = pwm => pwm < 96 ? 0 : 300 + pwm * 4;
        return hw;
    }

    [Fact]
    public async Task OnCommand_Identify_RejectedWhileCalibrating()
    {
        var hw = TwoFanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // hält die Kalibrier-Rampe „laufend"
        var service = new ControlLoopService(
            hw, hw, store, ipc, NullLogger<ControlLoopService>.Instance, FastTick,
            (s, f) => new CalibrationService(s, f, (_, ct) => gate.Task.WaitAsync(ct)), NoDelay,
            dryRunOverride: true);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "A"));
        await WaitUntilAsync(() => ipc.Broadcasts.Any(b => b.Calibration is { Running: true, FanId: "A" }));

        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "B")); // muss abgelehnt werden
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);

        Assert.DoesNotContain(hw.Writes, w => w.Fan == "B"); // B nie angefasst → Identify abgelehnt
        Assert.All(ipc.Broadcasts, b => Assert.Null(b.Identify));

        gate.SetResult();
        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_Calibration_RejectedWhileIdentifying()
    {
        var hw = TwoFanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var gate = new TaskCompletionSource(); // hält den Identify-Puls „laufend"
        var service = new ControlLoopService(
            hw, hw, store, ipc, NullLogger<ControlLoopService>.Instance, FastTick,
            null, (_, ct) => gate.Task.WaitAsync(ct), dryRunOverride: true);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.Identify, Target: "B"));
        await WaitUntilAsync(() => ipc.Broadcasts.Any(b => b.Identify is { Running: true, FanId: "B" }));

        ipc.Emit(new IpcCommand(IpcCommand.StartCalibration, Target: "A")); // muss abgelehnt werden
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);

        Assert.All(ipc.Broadcasts, b => Assert.Null(b.Calibration)); // nie gestartet

        gate.SetResult();
        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: SetActiveProfile --------------------------------------------------------------

    [Fact]
    public async Task OnCommand_SetActiveProfile_AppliesAndPersists()
    {
        var hw = FanRig(canControl: true);
        var curveA = new CurveConfig { Id = "ca", Name = "A", SourceSensorIds = new[] { "t" }, Points = new[] { new CurvePoint(40, 30) } };
        var curveB = new CurveConfig { Id = "cb", Name = "B", SourceSensorIds = new[] { "t" }, Points = new[] { new CurvePoint(40, 80) } };
        var profA = new Profile
        {
            Id = "pa",
            Name = "A",
            Curves = new[] { curveA },
            Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "ca") },
        };
        var profB = new Profile
        {
            Id = "pb",
            Name = "B",
            Curves = new[] { curveB },
            Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "cb") },
        };
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with
            {
                Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "F", AssignedCurveId = "ca" } },
                Profiles = new[] { profA, profB },
                ActiveProfileId = "pa",
            },
        };
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.Saves.Clear();

        ipc.Emit(new IpcCommand(IpcCommand.SetActiveProfile, Target: "pb"));

        // Profilwechsel wird im nächsten Tick angewendet & persistiert: aktives Profil + Zuordnung.
        await WaitUntilAsync(() => store.Saves.Any(s => s.ActiveProfileId == "pb"));
        AppConfig saved = store.Saves.Last(s => s.ActiveProfileId == "pb");
        Assert.Equal("pb", saved.ActiveProfileId);
        Assert.Equal("cb", saved.Fans.Single(f => f.FanId == "hwmon7/pwm1").AssignedCurveId);

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: SetCurveEnabled ---------------------------------------------------------------

    [Fact]
    public void ApplyCurveToggles_Disables_InActiveCurvesAndActiveProfileOnly()
    {
        var curve = new CurveConfig { Id = "c", Name = "C", Enabled = true };
        var sameIdElsewhere = new CurveConfig { Id = "c", Name = "C", Enabled = true }; // eigenes Set im anderen Profil
        var config = AppConfig.Empty with
        {
            Curves = new[] { curve },
            Profiles = new[]
            {
                new Profile { Id = "pa", Name = "A", Curves = new[] { curve } },
                new Profile { Id = "pb", Name = "B", Curves = new[] { sameIdElsewhere } },
            },
            ActiveProfileId = "pa",
        };

        AppConfig result = ControlLoopService.ApplyCurveToggles(config, new[] { ("c", false) });

        Assert.False(result.Curves.Single().Enabled);                                   // aktive Kurven
        Assert.False(result.Profiles.Single(p => p.Id == "pa").Curves.Single().Enabled); // aktives Profil
        Assert.True(result.Profiles.Single(p => p.Id == "pb").Curves.Single().Enabled);  // anderes Profil unberührt
    }

    [Fact]
    public void ApplyCurveToggles_LastValuePerCurveWins()
    {
        var config = AppConfig.Empty with
        {
            Curves = new[] { new CurveConfig { Id = "c", Name = "C", Enabled = true } },
            ActiveProfileId = null,
        };

        AppConfig result = ControlLoopService.ApplyCurveToggles(config, new[] { ("c", false), ("c", true) });

        Assert.True(result.Curves.Single().Enabled);
    }

    [Fact]
    public async Task OnCommand_SetCurveEnabled_DisablesCurve_AndPersists()
    {
        var hw = FanRig(canControl: true);
        var curve = new CurveConfig
        {
            Id = "ca",
            Name = "A",
            SourceSensorIds = new[] { "t" },
            Points = new[] { new CurvePoint(40, 30) },
        };
        var prof = new Profile
        {
            Id = "pa",
            Name = "A",
            Curves = new[] { curve },
            Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "ca") },
        };
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with
            {
                Curves = new[] { curve },
                Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "F", AssignedCurveId = "ca" } },
                Profiles = new[] { prof },
                ActiveProfileId = "pa",
            },
        };
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.Saves.Clear();

        ipc.Emit(new IpcCommand(IpcCommand.SetCurveEnabled, Target: "ca", Value: 0));

        // Im nächsten Tick angewandt & persistiert - in den aktiven Kurven UND im aktiven Profil.
        await WaitUntilAsync(() => store.Saves.Any(s => s.Curves.Any(c => c.Id == "ca" && !c.Enabled)));
        AppConfig saved = store.Saves.Last(s => s.Curves.Any(c => c.Id == "ca"));
        Assert.False(saved.Curves.Single(c => c.Id == "ca").Enabled);
        Assert.False(saved.Profiles.Single(p => p.Id == "pa").Curves.Single(c => c.Id == "ca").Enabled);

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: Reload ------------------------------------------------------------------------

    [Fact]
    public async Task OnCommand_Reload_RereadsConfigFromStore()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        int loadsAfterStart = store.LoadCount;

        // Datei ändert sich außerhalb (anderer Prozess) → Reload soll erneut laden.
        ipc.Emit(new IpcCommand(IpcCommand.Reload));

        await WaitUntilAsync(() => store.LoadCount > loadsAfterStart);

        await service.StopAsync(cts.Token);
    }

    // --- OnClientsChanged: Disconnect verwirft manuelle Overrides ---------------------------------

    [Fact]
    public async Task OnClientsChanged_ZeroClients_ClearsManualOverrides()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        // Manuell setzen und warten, bis es im Snapshot als manuell sichtbar ist.
        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 100));
        await WaitUntilAsync(() => FanIsManual(ipc, "hwmon7/pwm1"));

        // Letzter Client trennt → alle manuellen Overrides werden verworfen.
        ipc.EmitClients(0);

        await WaitUntilAsync(() => !FanIsManual(ipc, "hwmon7/pwm1"));

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnClientsChanged_NonZeroClients_KeepsManualOverrides()
    {
        var hw = FanRig(canControl: true);
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 100));
        await WaitUntilAsync(() => FanIsManual(ipc, "hwmon7/pwm1"));

        // Ein weiterer Client verbindet (count > 0) → Override bleibt bestehen.
        ipc.EmitClients(1);

        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        Assert.True(FanIsManual(ipc, "hwmon7/pwm1"));

        await service.StopAsync(cts.Token);
    }

    // --- OnCommand: unbekanntes/unvollständiges Kommando wird gemeldet, nicht still verschluckt --------

    [Fact]
    public async Task OnCommand_UnknownCommand_LogsWarning_AndKeepsServing()
    {
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var log = new CapturingLogger<ControlLoopService>();
        var service = NewService(hw, store, ipc, log: log);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(new IpcCommand("realod")); // Tippfehler von "reload" - trifft keinen Zweig (Emit ruft OnCommand synchron)

        // Nicht still verschluckt: eine Warnung nennt das ignorierte Kommando. Snapshot unter der Sperre,
        // da der Hintergrund-Tick-Loop parallel weiter loggt.
        Assert.Contains(log.Snapshot(), e => e.Level == LogLevel.Warning && e.Message.Contains("realod"));

        // Der Dispatch bleibt intakt: ein danach gesendetes gültiges Kommando greift weiterhin.
        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 128));
        await WaitUntilAsync(() => FanIsManual(ipc, "hwmon7/pwm1"));

        await service.StopAsync(cts.Token);
    }

    /// <summary>Sammelt Log-Einträge (Level + gerenderte Nachricht) thread-sicher - der Tick-Loop loggt parallel.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        /// <summary>Kopie der bisherigen Einträge (unter der Sperre) für kollisionsfreie Assertions.</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> Snapshot()
        {
            lock (_gate) return _entries.ToList();
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
