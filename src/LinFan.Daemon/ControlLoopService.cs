// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;

namespace LinFan.Daemon;

/// <summary>
/// Hintergrunddienst, der den <see cref="ControlLoop"/> periodisch tickt, jeden Tick eine
/// <see cref="IpcSnapshot"/> an verbundene GUI-Clients broadcastet und <c>reload</c>-Kommandos
/// verarbeitet (Config neu einlesen, ohne Neustart). Ohne Root im Dry-Run.
/// </summary>
internal sealed class ControlLoopService : BackgroundService
{
    /// <summary>
    /// Default-Obergrenze, die <see cref="StopAsync"/> je Coordinator-Stop aufwendet. Knapp gehalten und
    /// weit unter dem systemd-<c>TimeoutStopSec</c> der Unit, damit das abschließende Fail-Safe-
    /// RestoreDefaults garantiert vor einem SIGKILL läuft (siehe <c>packaging/linfan-daemon.service</c>).
    /// </summary>
    private static readonly TimeSpan DefaultCoordinatorStopTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Upper bound <see cref="StopAsync"/> waits for <see cref="ExecuteAsync"/> to finish, so a loop wedged in a
    /// hardware call cannot delay the final RestoreDefaults past systemd's <c>TimeoutStopSec=20</c> and its SIGKILL.
    /// Budget against the host's ShutdownTimeout (15 s, set in <c>DaemonHost</c>): coordinators exclude each other,
    /// so one stop (3 s) + this (5 s) + two restores' gate waits (2 × 0.5 s) take about 9 s; even all three stops
    /// timing out (9 s) end at about 15 s, still below systemd's 20 s.
    /// </summary>
    private static readonly TimeSpan DefaultExecuteStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What a tick that threw reports: the watchdog did not run, the fans were put on firmware auto. Without a
    /// snapshot for it the GUI would keep showing the last good tick for as long as the ticks fail.
    /// </summary>
    private static readonly ControlTick FailedTick =
        new(FailSafeTriggered: true, double.NaN, Array.Empty<FanAction>(), "Tick-Fehler", FailSafeState.Error);

    // Sent after every tick in which the watchdog ran; a unit with WatchdogSec= restarts a daemon whose loop hangs.
    private static readonly ServiceState WatchdogPing = new("WATCHDOG=1");

    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly IConfigStore _store;
    private readonly IIpcServer _ipc;
    private readonly ILogger<ControlLoopService> _log;

    private volatile bool _reloadRequested;
    private volatile bool _pendingReset;  // Config auf Werkszustand zurücksetzen (GUI-Befehl)
    // Set when StopAsync begins: fans are already back on hardware auto, so a calibration or tach-mapping
    // result finishing now may have been measured against firmware control and must not be persisted.
    private volatile bool _stopping;
    private IpcConfig? _pendingSave;   // von der GUI gesendete Config (per Interlocked übergeben)
    private IpcConfig? _pendingReplace; // von der GUI gesendete Config, die die bestehende vollständig ersetzt
    private string? _pendingActiveProfile;
    // Curve on/off from the dashboard, buffered per tick. One slot per curve (last value wins) rather than a queue, so
    // quick toggles of different curves are all kept while a client cannot pile up entries - only curves of the live
    // config get in. Drained on the loop thread.
    private readonly ConcurrentDictionary<string, bool> _pendingCurveToggles = new(StringComparer.Ordinal);
    // Tacho-Zuordnung(en) (manuell / Auto-Kopplung): je Lüfter das RpmSource-Override setzen/löschen.
    // One slot per fan (last value wins) rather than a queue: the onboarding can still assign several fans in a
    // row, while a client can no longer pile up entries - only discovered fans get in. Drained on the loop thread.
    private readonly ConcurrentDictionary<string, string?> _pendingTachAssignments = new(StringComparer.Ordinal);

    // Hardware changes the user resolved (acceptHardware / forgetHardware), one slot per change key (last command wins).
    // Only keys of the last reported changes get in. Drained on the loop thread, which resolves each key afresh.
    private readonly ConcurrentDictionary<string, (bool Forget, bool ResetSensorNames)> _pendingHardware =
        new(StringComparer.Ordinal);

    // The hardware changes the last snapshot reported (written on the loop thread, read by OnCommand).
    private volatile IReadOnlyList<HardwareChange> _reportedChanges = [];
    // Change keys already logged this run, and whether detecting them failed (loop thread only).
    private readonly HashSet<string> _loggedHardwareChanges = new(StringComparer.Ordinal);
    private bool _hardwareChangesFailed;

    // Whether the calibration check failed this run (loop thread only) - logged once.
    private bool _calibrationCheckFailed;

    // Fans and fan-speed sensors the backend discovered. Discovery is fixed for the process lifetime in every
    // backend, so this is read once at start - the IPC thread must not wait on the hardware gate per command.
    private IReadOnlySet<string> _discoveredFanIds = new HashSet<string>();
    private IReadOnlySet<string> _discoveredRpmSensorIds = new HashSet<string>();
    // The descriptors themselves, for the devices behind them; null until read, or if discovery failed.
    private IReadOnlyList<FanDescriptor>? _discoveredFans;
    private IReadOnlyList<SensorDescriptor>? _discoveredSensors;
    private ControlLoop? _loop;        // für thread-sichere Steuerbefehle aus dem IPC-Thread
    private CalibrationCoordinator? _calibration;
    private IdentifyCoordinator? _identify;
    private TachMappingCoordinator? _tachMapping;
    private volatile AppConfig? _config;  // Live-Config (Referenz-Swap atomar) - u. a. für den Kalibrier-Watchdog

    // Why the config found at start is not the one in use, for the snapshot - until the next saved change. Only the
    // loop thread (and the start before it) touches these.
    private ConfigLoadProblem? _configProblem;
    private string? _configSetAsidePath;

    private readonly object _calLock = new();
    private (string FanId, FanCalibration Cal)? _pendingCalibration;

    // Serialisiert die wechselseitig ausschließenden Lüfter-Aktionen (Kalibrierung ⇄ Identifikation):
    // OnCommand kann pro IPC-Client nebenläufig laufen, daher muss „prüfen-dann-starten" atomar sein.
    private readonly object _controlLock = new();

    // Bereits gemeldete unbekannte Kurven-Quellen - dedup, damit je ID nur einmal pro Daemon-Lauf gewarnt wird.
    private readonly HashSet<string> _warnedUnknownSensors = new();

    // Fans handed back at full speed and reported (loop thread only) - a transient auto failure repeats every tick until
    // auto can be set again; cleared by the fan's next action that is no failure, so a channel flapping between full
    // speed and a failed write is not reported again at every flip.
    private readonly HashSet<string> _fullSpeedFans = new(StringComparer.Ordinal);

    // Fans whose last action failed and was reported (loop thread only) - cleared by the next action that does not fail.
    private readonly HashSet<string> _failingFans = new(StringComparer.Ordinal);

    // Set while ticks keep failing (loop thread only): the failure is logged as an error once per run.
    private bool _ticksFailing;

    // Test-Seam: überschreibt das aus der Config abgeleitete Tick-Intervall (Default: aus der Config).
    private readonly TimeSpan? _tickIntervalOverride;

    // Test-Seam: injizierbare CalibrationService-Factory (Default: echte Delays), analog zum
    // bereits vorhandenen Factory-Parameter des CalibrationCoordinator.
    private readonly Func<ISensorBackend, IFanController, CalibrationService>? _calibrationFactory;

    // Test-Seam: injizierbare Warte-Funktion des Identifikations-Pulses (Default: echtes Task.Delay).
    private readonly Func<TimeSpan, CancellationToken, Task>? _identifyDelay;

    // Test-Seam: injizierbare TachometerMappingService-Factory (Default: echte Delays), analog zur
    // CalibrationService-Factory - lässt einen Test die Auto-Kopplung ohne echte Settle-Wartezeit prüfen.
    private readonly Func<ISensorBackend, IFanController, TachometerMappingService>? _tachMappingFactory;

    // Test-Seam: Obergrenze je Coordinator-Stop im Shutdown (Default: DefaultCoordinatorStopTimeout).
    // Erlaubt einem Test, den „hängender Stop"-Pfad ohne echte 3-s-Wartezeit zu prüfen.
    private readonly TimeSpan _coordinatorStopTimeout;

    // Test seam: bound on waiting for ExecuteAsync in StopAsync (default: DefaultExecuteStopTimeout).
    private readonly TimeSpan _executeStopTimeout;

    // Without root the daemon only computes and never writes - no restore and no device action either: such an
    // instance (e.g. a dev build) would otherwise act on the fans of a live daemon wherever udev makes the PWM files
    // group-writable. Test seam dryRunOverride: tests run with or without root depending on the runner (the CI
    // container runs as root); in production the real privilege check decides.
    private readonly bool _dryRun;

    // Restores wait until it is settled that no other daemon holds the IPC endpoint: until then the fans may be another
    // daemon's, so a stop that comes first (SIGTERM while starting) restores nothing - under systemd ExecStopPost covers
    // that. A start that fails for any other reason settles it as ours, so whatever a predecessor left goes back to auto.
    private volatile bool _fansAreOurs;
    private volatile bool _anotherDaemonHoldsTheEndpoint;

    private readonly ISystemdNotifier? _systemd;

    // Test seam: replaces HardwareChanges.Detect over the discovery read at start (default: that).
    private readonly Func<AppConfig, IReadOnlyList<HardwareChange>>? _detectHardwareChanges;

    // Test seam: the calibration check with its own clock and tolerances (default: system clock, the tick interval).
    private readonly CalibrationPlausibility? _calibrationPlausibility;

    public ControlLoopService(
        ISensorBackend sensors, IFanController fans, IConfigStore store, IIpcServer ipc,
        ILogger<ControlLoopService> log, TimeSpan? tickInterval = null,
        Func<ISensorBackend, IFanController, CalibrationService>? calibrationFactory = null,
        Func<TimeSpan, CancellationToken, Task>? identifyDelay = null,
        TimeSpan? coordinatorStopTimeout = null,
        Func<ISensorBackend, IFanController, TachometerMappingService>? tachMappingFactory = null,
        bool? dryRunOverride = null,
        TimeSpan? executeStopTimeout = null,
        ISystemdNotifier? systemd = null,
        Func<AppConfig, IReadOnlyList<HardwareChange>>? detectHardwareChanges = null,
        CalibrationPlausibility? calibrationPlausibility = null)
    {
        _sensors = sensors;
        _fans = fans;
        _store = store;
        _ipc = ipc;
        _log = log;
        _tickIntervalOverride = tickInterval;
        _calibrationFactory = calibrationFactory;
        _identifyDelay = identifyDelay;
        _coordinatorStopTimeout = coordinatorStopTimeout ?? DefaultCoordinatorStopTimeout;
        _tachMappingFactory = tachMappingFactory;
        _dryRun = dryRunOverride ?? !Privileges.IsElevated();
        _executeStopTimeout = executeStopTimeout ?? DefaultExecuteStopTimeout;
        _systemd = systemd;
        _detectHardwareChanges = detectHardwareChanges;
        _calibrationPlausibility = calibrationPlausibility;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var loop = new ControlLoop(_sensors, _fans, _dryRun);
        _loop = loop; // Steuerbefehle (Manual/Auto) greifen thread-sicher über die Loop-Methoden
        var calibration = new CalibrationCoordinator(
            _sensors, _fans, _log, loop.Suspend, loop.Resume, QueueCalibrationResult, ct, _calibrationFactory,
            failSafeTempC: () => _config?.FailSafeTempC ?? CalibrationOptions.DefaultFailSafeTempC,
            couplings: CurrentCouplings);
        _calibration = calibration;
        var identify = new IdentifyCoordinator(
            _sensors, _fans, _log, loop.Suspend, loop.Resume, ct, _identifyDelay,
            failSafeTempC: () => _config?.FailSafeTempC ?? CalibrationOptions.DefaultFailSafeTempC);
        _identify = identify;
        var tachMapping = new TachMappingCoordinator(
            _sensors, _fans, _log, loop.Suspend, loop.Resume,
            QueueTachMappingResult,
            ct, _tachMappingFactory,
            failSafeTempC: () => _config?.FailSafeTempC ?? CalibrationOptions.DefaultFailSafeTempC);
        _tachMapping = tachMapping;

        // Setup (Config laden, IPC starten) liegt INNERHALB des try, damit das finally - und damit
        // RestoreDefaults - auch bei einem Setup-Fehler garantiert läuft (Fail-Safe, M1).
        try
        {
            // The endpoint first: if another daemon answers there, the fans are its - this instance must not even run
            // the start restore below (see _fansAreOurs, StandDown). Until the handlers are set further down, the server
            // drops what a client sends; the daemon has not taken on the fans yet either.
            try
            {
                await _ipc.StartAsync(ct).ConfigureAwait(false);
            }
            catch (IpcEndpointInUseException ex)
            {
                _anotherDaemonHoldsTheEndpoint = true;
                StandDown(ex);
                throw;
            }
            catch
            {
                _fansAreOurs = true;
                throw;
            }
            _fansAreOurs = true;

            // Whatever a predecessor left behind (SIGKILL, crash) goes back to hardware auto before the first tick:
            // a fan the loaded config does not regulate would otherwise keep that PWM, and with no fan under
            // control the watchdog's blind-sensor check is off too. This holds where the backend writes the auto
            // mode outright (Linux); the Windows backend can only restore what it changed itself in this process,
            // so if another LinFan writer of this boot left a restore marker (crashed or still running) it puts the
            // fans on 100 % instead.
            // Not in a dry run (see _dryRun) - TryRestoreDefaults skips it there.
            TryRestoreDefaults("Start-RestoreDefaults");
            AppConfig config = LoadOrStartFresh();
            _config = config;
            WarnAboutUnknownSources(config);
            int interval = Math.Max(ConfigSanitizer.MinPollIntervalMs, config.PollIntervalMs);
            RememberDiscovery();

            _ipc.CommandHandler = OnCommand;
            _ipc.ClientsChanged = OnClientsChanged;
            await Task.Yield(); // Host-Start abschließen, bevor der erste Tick läuft
            _log.LogInformation("IPC-Server hört auf {Path}", _ipc.Path);
            _log.LogInformation(
                "ControlLoop läuft · dryRun={DryRun} · {Fans} Lüfter, {Curves} Kurven · Intervall {Interval} ms · Config: {Path}",
                _dryRun, config.Fans.Count, config.Curves.Count, interval, _store.ConfigPath);
            LogDiscovery(config); // Diagnose: was das Backend sieht + welcher Tacho zu welchem Lüfter gehört

            // The config as last mapped for the snapshot, and its revision: mapped again only when the loop swaps in a
            // new config object (every change makes one), so clients can be sent the config only when it changed.
            AppConfig? mappedConfig = null;
            IpcConfig ipcConfig = IpcConfig.Empty;
            long configRevision = 0;
            IReadOnlyList<IpcHardwareChange>? hardwareChanges = null;

            TimeSpan tickInterval = _tickIntervalOverride ?? TimeSpan.FromMilliseconds(interval);
            CalibrationPlausibility plausibility = _calibrationPlausibility ?? new CalibrationPlausibility(tickInterval);
            using var timer = new PeriodicTimer(tickInterval);
            do
            {
                // Ein einzelner fehlgeschlagener Tick darf den Daemon nie beenden (er steuert Hardware).
                // Each stage has its own guard: a config change that fails to apply (possibly re-sent by a
                // client every few ms) must never skip loop.Tick, which carries the over-temperature watchdog.
                try
                {
                    AppConfig updated = ApplyPendingConfigChanges(config);
                    if (!ReferenceEquals(updated, config))
                    {
                        if (updated.FailSafeTempC != config.FailSafeTempC)
                            _log.LogWarning("Fail-Safe-Grenze geändert: {Old:0.#} °C → {New:0.#} °C.",
                                config.FailSafeTempC, updated.FailSafeTempC);
                        config = updated;
                        _config = updated; // Live-Config aktuell halten (Kalibrier-Watchdog liest sie)
                        // Every changed config has been saved (or, on reload, read back from the file) by now: the file
                        // from the start is gone or replaced, or now in use.
                        _configProblem = null;
                        _configSetAsidePath = null;
                        loop.ResetFilters(); // neue Kurven sofort anwenden, nicht erst bei Temp-Drift
                        WarnAboutUnknownSources(updated); // z. B. nach Profilwechsel auf Kurven mit toter Quelle
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Konfigurationsänderung fehlgeschlagen - bisherige Konfiguration bleibt aktiv.");
                }

                ControlTick tick;
                bool tickFailed = false;
                try
                {
                    // No client, no manual control (see OnClientsChanged). Checked here rather than on the disconnect
                    // event: a command still buffered from a client that has just gone can arrive after that event.
                    if (_ipc.ClientCount == 0)
                        loop.ClearAllManualOverrides();

                    tick = loop.Tick(config);

                    // A write wedged in the driver holds the hardware gate: the loop's writes time out, so its curves
                    // can no longer raise a fan. Handled like a failed tick - every fan to hardware auto, past the
                    // gate - until the gate is free again.
                    if (_fans is SynchronizedFanController { GateStalled: true })
                        throw new TimeoutException("Hardware-Gate blockiert (hängender Write) - Regelung nicht möglich.");

                    // Fail-Safe (Übertemperatur/blind) bricht laufende Kalibrierung UND Identifikation
                    // sofort ab, damit deren Writes den sicheren Zustand nicht gleich wieder übertrampeln.
                    if (tick.FailSafeTriggered)
                    {
                        calibration.Cancel();
                        identify.Cancel();
                        tachMapping.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    // A failed tick is an internal daemon error and must lead to the safe state (ARCHITECTURE
                    // §7): otherwise fans keep their last manual PWM with no watchdog for as long as it fails.
                    // Restore before logging: a blocked logger must not hold up the safe state.
                    Exception? restoreFailure = null;
                    try { ReturnAllFansToSafeAuto(); }
                    catch (Exception restoreEx) { restoreFailure = restoreEx; }
                    // Once per run of failing ticks - a cause that persists would otherwise log a trace every tick.
                    LogLevel level = _ticksFailing ? LogLevel.Debug : LogLevel.Error;
                    _log.Log(level, ex, "Tick fehlgeschlagen - Fail-Safe: Lüfter auf Hardware-Auto.");
                    if (restoreFailure is not null)
                        _log.Log(level, restoreFailure, "Fail-Safe nach Tick-Fehler fehlgeschlagen.");
                    _ticksFailing = true;
                    tick = FailedTick;
                    tickFailed = true;
                }

                if (!tickFailed && _ticksFailing)
                {
                    _ticksFailing = false;
                    _log.LogInformation("Regel-Tick läuft wieder.");
                }

                if (!tickFailed)
                    PingSystemdWatchdog();

                try
                {
                    if (!tickFailed)
                        LogTick(tick);
                    if (!ReferenceEquals(mappedConfig, config))
                    {
                        ipcConfig = ConfigMapper.ToIpc(config);
                        hardwareChanges = ReportHardwareChanges(config); // never throws: reporting must not cost the snapshot
                        mappedConfig = config;
                        configRevision++;
                    }
                    DaemonStatus status = _dryRun ? DaemonStatus.DryRun : DaemonStatus.Active;
                    IpcSnapshot snapshot = SnapshotBuilder.Build(
                        _sensors, _fans, status, _dryRun, tick, config, ipcConfig, configRevision,
                        loop.ManualFanIds(), calibration.Status, identify.Status, tachMapping.Status,
                        _configProblem, _configSetAsidePath, hardwareChanges);
                    // Never throws: a notice must not cost the snapshot.
                    snapshot = WithCalibrationMismatches(snapshot, plausibility, tick, config);
                    await _ipc.BroadcastAsync(snapshot).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Snapshot-Broadcast fehlgeschlagen - übersprungen.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // normaler Shutdown
        }
        finally
        {
            TryRestoreDefaults("Loop-Ende-RestoreDefaults");
            if (_fansAreOurs)
                _log.LogInformation(_dryRun
                    ? "ControlLoop gestoppt (Dry-Run, Lüfter unberührt)."
                    : "ControlLoop gestoppt - Fail-Safe RestoreDefaults ausgeführt.");
        }
    }

    /// <summary>
    /// Tells systemd the loop is alive. Only after a tick in which the watchdog ran: a loop stuck in a hardware call
    /// or failing every tick stops pinging, and the unit's WatchdogSec= restarts the daemon (its ExecStopPost= puts
    /// the fans on hardware auto in between). Never lets a notification problem reach the loop.
    /// </summary>
    private void PingSystemdWatchdog()
    {
        try
        {
            if (_systemd is { IsEnabled: true })
                _systemd.Notify(WatchdogPing);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "systemd-Watchdog-Meldung fehlgeschlagen.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // From the stop request on, only the safe state may reach the hardware: a loop or coordinator write
        // that wakes up late - after the bounded waits below gave up on it - must not undo the final
        // restore. Restoring right away also reaches the safe state in ~0.5 s instead of after the waits.
        _stopping = true;
        (_fans as IManualWriteLatch)?.BlockManualWrites();
        TryRestoreDefaults("Sofort-RestoreDefaults");

        try
        {
            // Laufende Kalibrierung erst sauber beenden (Rampe stoppt, Lüfter zurück auf Auto), BEVOR
            // der ExecuteAsync-finally das abschließende RestoreDefaults ausführt - sonst könnte die
            // weiterlaufende Rampe den sicheren Zustand wieder übertrampeln.
            //
            // Fail-Safe: je Stop nur BEGRENZT warten. Hängt ein Coordinator-Stop (z. B. hinter einem im
            // EC/Treiber festhängenden Hardware-Write), käme sonst weder base.StopAsync (cancelt das
            // Stopp-Token) noch das finally-RestoreDefaults in ExecuteAsync je dran - systemd SIGKILLt
            // nach TimeoutStopSec und überspränge damit jedes Fail-Safe-Restore. Nach dem (ggf. abge-
            // brochenen) Warten cancelt base.StopAsync das Token; der abgebrochene Coordinator-Lauf
            // bricht dann selbst ab und das finally stellt den sicheren Zustand her.
            if (_calibration is not null)
                await StopBoundedAsync(_calibration.StopAsync, "Kalibrierung").ConfigureAwait(false);
            if (_identify is not null)
                await StopBoundedAsync(_identify.StopAsync, "Identifikation").ConfigureAwait(false);
            if (_tachMapping is not null)
                await StopBoundedAsync(_tachMapping.StopAsync, "Sensor-Kopplung").ConfigureAwait(false);

            // base.StopAsync returns (without throwing) once this token fires, even if ExecuteAsync is stuck.
            using var executeStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            executeStop.CancelAfter(_executeStopTimeout);
            await base.StopAsync(executeStop.Token).ConfigureAwait(false);
        }
        finally
        {
            // Rest-Fenster schließen (Fail-Safe-Audit): ein nach dem Stop-Timeout spät aufwachender,
            // verwaister Coordinator-Write könnte das finally-RestoreDefaults noch mit einem Rampenwert
            // überschreiben. Ein idempotenter Abschluss-Restore als ALLERLETZTER Schritt stellt sicher,
            // dass der zuletzt wirksame Write Hardware-Auto ist - in a finally, so that nothing thrown
            // above can skip it.
            TryRestoreDefaults("Abschluss-RestoreDefaults");
        }
    }

    /// <summary>
    /// Another daemon holds the IPC endpoint, so the fans are its. One that serves keeps them as they are. One that holds
    /// the endpoint without sending anything (stopped, stuck) may have left them on a low manual value with no watchdog:
    /// they go to hardware auto - the safe direction, since that daemon takes them back with its next write if it
    /// resumes. Either way this instance ends.
    /// </summary>
    private void StandDown(IpcEndpointInUseException ex)
    {
        if (ex.Serving)
        {
            _log.LogCritical("Auf {Path} läuft bereits ein LinFan-Daemon - diese Instanz beendet sich, ohne einen Lüfter "
                + "anzufassen.", ex.Endpoint);
            return;
        }

        _log.LogCritical("Den Endpunkt {Path} hält ein LinFan-Daemon, der nichts sendet (angehalten oder hängend?) - Lüfter "
            + "auf Hardware-Auto, diese Instanz beendet sich. Den hängenden Prozess beenden und den Dienst neu starten.",
            ex.Endpoint);
        if (_dryRun)
            return;
        try { _fans.RestoreDefaults(); }
        catch (Exception restoreEx) { _log.LogError(restoreEx, "RestoreDefaults für den hängenden Daemon fehlgeschlagen."); }
    }

    /// <summary>Whether this instance ended because another daemon holds the IPC endpoint (exit code, see DaemonHost).</summary>
    internal bool AnotherDaemonHoldsTheEndpoint => _anotherDaemonHoldsTheEndpoint;

    /// <summary>RestoreDefaults is gate-bounded and does not throw by contract - the catch is a last guard.</summary>
    private void TryRestoreDefaults(string step)
    {
        if (_dryRun || !_fansAreOurs)
            return;
        try { _fans.RestoreDefaults(); }
        catch (Exception ex) { _log.LogWarning(ex, "{Step} fehlgeschlagen.", step); }
    }

    /// <summary>
    /// Wartet höchstens <see cref="_coordinatorStopTimeout"/> auf einen Coordinator-Stop und fährt sonst
    /// fort (Fail-Safe: der Shutdown darf nicht hinter einem hängenden Stop blockieren). Das abschließende
    /// RestoreDefaults in <see cref="ExecuteAsync"/> stellt den sicheren Zustand in jedem Fall her.
    /// </summary>
    private async Task StopBoundedAsync(Func<Task> stop, string label)
    {
        try
        {
            // Task.Run: stop() cancels synchronously, so the coordinator's cleanup (hardware writes) can run
            // inline before stop() even returns a task - the timeout would then never get to apply.
            await Task.Run(stop).WaitAsync(_coordinatorStopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning(
                "{Coordinator}-Stop nach {Ms} ms nicht abgeschlossen - Shutdown fährt fort; das "
                + "finally-RestoreDefaults stellt den sicheren Zustand her.",
                label, _coordinatorStopTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Coordinator}-Stop beim Shutdown fehlgeschlagen - Shutdown fährt fort.", label);
        }
    }

    /// <summary>
    /// Führt vor einem Reset/Import alle Lüfter in den sicheren Firmware-Auto-Zustand zurück: laufende
    /// Kalibrierung/Identifikation abbrechen (ihr Ergebnis ist danach gegenstandslos, und die Rampe würde
    /// sonst gegen <see cref="IFanController.RestoreDefaults"/> schreiben), GUI-Manual-Overrides fallen lassen
    /// und Hardware-Auto wiederherstellen. Ohne das bliebe ein zuvor kurvengeregelter Lüfter, der in der
    /// neuen Config fehlt, im Manual-Zustand hängen - ohne Kurve, ohne Firmware-Regelung (Fail-Safe-Lücke).
    /// Läuft auf dem Loop-Thread, wie alle anderen Hardware-Writes (dasselbe Cancel-Muster wie der Übertemp-
    /// Fail-Safe in <see cref="ExecuteAsync"/>).
    /// </summary>
    private void ReturnAllFansToSafeAuto()
    {
        try
        {
            _calibration?.Cancel();
            _identify?.Cancel();
            _tachMapping?.Cancel();
            _loop?.ClearAllManualOverrides();
        }
        finally
        {
            try
            {
                if (!_dryRun)
                    _fans.RestoreDefaults(); // the safe state must not depend on the steps before it succeeding
            }
            finally
            {
                // Every fan is on firmware auto now: without this the hysteresis would keep the curves from
                // writing again until the temperature drifts (after a failed tick there is no config change
                // that would reset it).
                _loop?.ResetFilters();
            }
        }
    }

    /// <summary>Übernimmt eine von der GUI gesendete bzw. eine angeforderte neu eingelesene Konfiguration.</summary>
    private AppConfig ApplyPendingConfigChanges(AppConfig current)
    {
        // Kalibrier-Ergebnis (vom Coordinator-Thread) → Anlaufpunkt + Messreihe persistieren.
        (string FanId, FanCalibration Cal)? cal;
        lock (_calLock)
        {
            cal = _pendingCalibration;
            _pendingCalibration = null;
        }
        if (cal is { } c)
        {
            HardwareDevice? device = _discoveredFans?.FirstOrDefault(f => f.Id.Value == c.FanId)?.Device;
            AppConfig merged = Sanitized(ConfigMapper.ApplyCalibration(current, c.FanId, c.Cal, device));
            _store.Save(merged);
            _log.LogInformation("Kalibrierung übernommen: {Fan} · MinPwm={Pwm}.", c.FanId, c.Cal.StartPwm);
            return merged;
        }

        // GUI hat eine Konfiguration gesendet → autoritativ zusammenführen, persistieren, übernehmen.
        // The last profile or curve command wins, whichever order they arrive in: a full config carries the
        // ActiveProfileId and every curve's Enabled flag, so OnCommand drops a profile switch or curve toggle still
        // pending from before it. Whatever is pending next to it therefore arrived later and has to build on it -
        // hence the save goes first, ahead of the switch and the toggles (a switch may also name a profile that only
        // this config adds).
        if (Interlocked.Exchange(ref _pendingSave, null) is { } incoming)
        {
            AppConfig candidate = ConfigMapper.Merge(current, incoming);
            if (ExceedsLimits(candidate, "Konfiguration von der GUI"))
                return current;
            AppConfig merged = Normalize(candidate);
            _store.Save(merged);
            _log.LogInformation("Konfiguration von der GUI übernommen & gespeichert ({Fans} Lüfter, {Curves} Kurven).",
                merged.Fans.Count, merged.Curves.Count);
            return merged;
        }

        // Hardware changes the user resolved. Unlike a profile switch or toggle, a later save, import, reset or reload
        // does not supersede them: each key is resolved against the config as it is now instead.
        if (!_pendingHardware.IsEmpty)
            return ApplyHardwareCommands(current);

        // Profilwechsel (Quick-Command) → Profil-Zuordnungen anwenden, persistieren.
        if (Interlocked.Exchange(ref _pendingActiveProfile, null) is { } profileId)
        {
            // An unknown id would be persisted as the active profile, and the next start would silently fall
            // back to whichever profile comes first.
            if (!current.Profiles.Any(p => p.Id == profileId))
            {
                _log.LogWarning("Profilwechsel abgelehnt: unbekanntes Profil {Profile}.", profileId);
                return current;
            }
            AppConfig switched = Sanitized(ProfileService.Apply(current, profileId));
            _store.Save(switched);
            _log.LogInformation("Profil aktiviert: {Profile}.", profileId);
            return switched;
        }

        // Kurve an/aus (Dashboard-Quick-Command) → Enabled-Flag setzen, persistieren. Deaktivierte
        // Kurven stellen ihre Lüfter im nächsten Tick auf Hardware-Auto (ControlLoop), Re-Enable wirkt
        // sofort (ResetFilters nach dem Config-Swap in ExecuteAsync).
        if (!_pendingCurveToggles.IsEmpty)
        {
            var toggles = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string curveId in _pendingCurveToggles.Keys)
            {
                if (_pendingCurveToggles.TryRemove(curveId, out bool enabled))
                    toggles[curveId] = enabled;
            }
            AppConfig toggled = Sanitized(ApplyCurveToggles(current, toggles));
            _store.Save(toggled);
            _log.LogInformation("Kurven an/aus übernommen: {Toggles}.",
                string.Join(", ", toggles.Select(t => $"{t.Key}={(t.Value ? "an" : "aus")}")));
            return toggled;
        }

        // Tacho-Zuordnung(en) (manuell / Auto-Kopplung) → RpmSource-Override je Lüfter setzen/löschen,
        // einmal persistieren. Nur Config-Zustand, kein Hardware-Eingriff.
        if (!_pendingTachAssignments.IsEmpty)
        {
            AppConfig updated = current;
            var applied = new List<string>();
            foreach (string fanId in _pendingTachAssignments.Keys)
            {
                if (!_pendingTachAssignments.TryRemove(fanId, out string? rpmSource))
                    continue;
                updated = ConfigMapper.ApplyTachometer(updated, fanId, rpmSource, out string? previous);
                applied.Add($"{fanId}={(string.IsNullOrWhiteSpace(rpmSource) ? "(keiner)" : rpmSource)}"
                            + (previous is null ? "" : $" (vorher bei {previous})"));
            }
            AppConfig saved = Sanitized(updated);
            _store.Save(saved);
            _log.LogInformation("Tacho-Zuordnung übernommen: {Assignments}.", string.Join(", ", applied));
            return saved;
        }

        // GUI hat einen Import geschickt → bestehende Config VOLLSTÄNDIG ersetzen (kein Merge), persistieren.
        if (Interlocked.Exchange(ref _pendingReplace, null) is { } toReplace)
        {
            AppConfig candidate = ConfigMapper.Replace(current, toReplace);
            if (ExceedsLimits(candidate, "Import"))
                return current;
            AppConfig replaced = Normalize(candidate);
            _store.Save(replaced);
            // Fail-Safe: zuvor kurvengeregelte Lüfter, die im Import fehlen, würden sonst im Manual-Zustand
            // hängen bleiben (nicht mehr in config.Fans → nie auf Auto zurückgestellt). Alle auf sicheres
            // Firmware-Auto; der Tick nach dem Swap regelt die im Import enthaltenen Lüfter sofort wieder.
            ReleaseAllFansAfterSave("Import");
            _log.LogInformation("Konfiguration importiert & vollständig ersetzt ({Fans} Lüfter, {Curves} Kurven).",
                replaced.Fans.Count, replaced.Curves.Count);
            return replaced;
        }

        // GUI hat einen Reset ausgelöst → auf Werkszustand zurück (alles leer). Die Hardware bleibt entdeckt
        // (Backends sind config-unabhängig); ohne Overrides zeigt der Snapshot rohe Hardware-Namen. Onboarding
        // wird bewusst NICHT erzwungen (OnboardingCompleted=true) - dafür gibt es den eigenen Menüpunkt.
        if (_pendingReset)
        {
            _pendingReset = false;
            AppConfig fresh = Normalize(ConfigMapper.Reset(current));
            _store.Save(fresh);
            // Fail-Safe: leere Config ⇒ keine Lüfter mehr in config.Fans ⇒ ein zuvor kurvengeregelter Lüfter
            // würde ohne Rückstellung im Manual-Zustand (fixer PWM) hängen. Daher alle auf Firmware-Auto.
            ReleaseAllFansAfterSave("Reset");
            _log.LogInformation("Konfiguration auf Werkszustand zurückgesetzt.");
            return fresh;
        }

        if (_reloadRequested)
        {
            _reloadRequested = false;
            AppConfig reloaded = LoadAndMigrate();
            _log.LogInformation("Konfiguration neu geladen ({Fans} Lüfter, {Curves} Kurven).",
                reloaded.Fans.Count, reloaded.Curves.Count);
            return reloaded;
        }

        return current;
    }

    /// <summary>
    /// Rejects a config from a client that exceeds <see cref="ConfigLimits"/>, before any per-entry work: the
    /// current config stays and the fans are not touched.
    /// </summary>
    private bool ExceedsLimits(AppConfig candidate, string what)
    {
        if (ConfigLimits.Check(candidate) is not { } violation)
            return false;
        _log.LogWarning("{What} abgelehnt: {Violation} - bisherige Konfiguration bleibt aktiv.", what, violation);
        return true;
    }

    /// <summary>
    /// Returns every fan to hardware auto once an import or reset is persisted - only then, so a command that is
    /// rejected or fails to save leaves the fans (and their manual overrides) alone. A failure here must not undo
    /// the swap either: the new config is already on disk.
    /// </summary>
    private void ReleaseAllFansAfterSave(string what)
    {
        try
        {
            ReturnAllFansToSafeAuto();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{What}: Lüfter nicht vollständig auf Hardware-Auto zurückgestellt.", what);
        }
    }

    /// <summary>
    /// Applies the pending hardware commands in one save. Each key is resolved against <paramref name="current"/> and the
    /// discovery: the entries a change covers now count, never the ones the GUI saw - one that left the change since
    /// (a calibration recorded the new device) stays, and a key that no longer resolves or changes nothing saves
    /// nothing. Fans whose entries are removed go back to hardware auto in the next tick, once the change is saved.
    /// </summary>
    private AppConfig ApplyHardwareCommands(AppConfig current)
    {
        AppConfig updated = current;
        var handBack = new List<string>();
        var applied = new List<string>();
        foreach (string key in _pendingHardware.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            if (!_pendingHardware.TryRemove(key, out (bool Forget, bool ResetSensorNames) command))
                continue;
            HardwareChange? change = DetectHardwareChanges(updated).FirstOrDefault(c => c.Key == key);
            if (change is null)
            {
                _log.LogInformation("Hardware-Änderung {Key} besteht nicht mehr - nichts übernommen.", key);
                continue;
            }

            AppConfig next = command.Forget
                ? HardwareChanges.Forget(updated, change)
                : HardwareChanges.Accept(updated, change, command.ResetSensorNames);
            if (ReferenceEquals(next, updated))
            {
                _log.LogInformation("Hardware-Änderung {Key}: nichts zu übernehmen ({Kind}).", key, change.Kind);
                continue;
            }
            updated = next;
            applied.Add(Describe(change, command.Forget, command.ResetSensorNames));
            // A dry run touches no fan, not even to hand it back.
            if (command.Forget && !_dryRun)
                handBack.AddRange(change.FanIds.Where(id => _discoveredFanIds.Contains(id) && CanControl(id)));
        }

        if (ReferenceEquals(updated, current))
            return current;
        AppConfig resolved = Normalize(updated);
        _store.Save(resolved);
        if (handBack.Count > 0)
            _loop?.HandBackToAuto(handBack);
        _log.LogInformation("Hardware-Änderung gespeichert: {Changes}.", string.Join("; ", applied));
        return resolved;
    }

    private static string Describe(HardwareChange change, bool forget, bool resetSensorNames)
    {
        string entries = $"{change.FanIds.Count} Lüfter, {change.SensorIds.Count} Sensoren";
        if (forget)
            return $"{DeviceText(change.Old)} vergessen ({entries} entfernt)";
        return $"{DeviceText(change.Old)} → {DeviceText(change.New)} übernommen ({entries}"
               + (resetSensorNames ? ", Sensornamen zurückgesetzt)" : ")");
    }

    // The fingerprint only where it says more than the name: on Windows both are LHM's hardware name.
    private static string DeviceText(HardwareDevice? device) =>
        device is null ? "(ohne Gerät)"
        : device.Fingerprint is null ? $"{device.Name} (kein Fingerabdruck)"
        : device.Fingerprint == device.Name ? device.Name
        : $"{device.Name} ({device.Fingerprint})";

    /// <summary>
    /// Setzt das <see cref="CurveConfig.Enabled"/>-Flag der genannten Kurven - sowohl in den aktiven
    /// <c>config.Curves</c> als auch in den Kurven des aktiven Profils, damit ein späterer Profilwechsel
    /// (<see cref="ProfileService.Apply"/>) den Stand nicht wieder überschreibt. Reine Funktion (testbar).
    /// </summary>
    internal static AppConfig ApplyCurveToggles(AppConfig config, IReadOnlyDictionary<string, bool> desired)
    {
        CurveConfig Flip(CurveConfig c) =>
            desired.TryGetValue(c.Id, out bool e) && e != c.Enabled ? c with { Enabled = e } : c;

        return config with
        {
            Curves = config.Curves.Select(Flip).ToList(),
            Profiles = config.Profiles
                .Select(p => p.Id == config.ActiveProfileId ? p with { Curves = p.Curves.Select(Flip).ToList() } : p)
                .ToList(),
        };
    }

    /// <summary>Lädt + sanitized + migriert Profile; persistiert einmalig, wenn die Migration etwas änderte.</summary>
    private AppConfig LoadAndMigrate() => LoadAndMigrate(_store.Load());

    private AppConfig LoadAndMigrate(AppConfig loaded)
    {
        // A file past the limits (an older version let a client inflate it, or a hand edit) is not used; at start
        // LoadOrStartFresh sets it aside and keeps its daemon-owned limits.
        if (ConfigLimits.Check(loaded) is { } violation)
            throw new InvalidOperationException($"Konfiguration überschreitet die Grenzen: {violation}.");
        // First-Run-Signal VOR dem Speichern festhalten: existiert noch keine Config-Datei,
        // ist dies eine frische Installation (der Daemon selbst legt die Datei gleich an).
        bool existed = _store.Exists;
        AppConfig clean = WithHardwareStamps(WithoutHardwareLabels(Sanitized(loaded), out int labels), out int stamped);
        bool migrate = labels > 0
                       || stamped > 0 // the first start after an update records the devices
                       || clean.Profiles.Count == 0
                       || clean.Profiles.Any(p => p.Curves.Count == 0)
                       || !clean.Profiles.Any(p => p.Id == clean.ActiveProfileId)
                       || clean.OnboardingCompleted is null;   // Flag muss einmalig gesetzt & persistiert werden.

        AppConfig ensured = ProfileService.EnsureProfiles(clean);

        // Onboarding-Status erstmalig festlegen: frische Installation (keine Datei) ⇒ Assistent zeigen (false);
        // bestehende Config ohne Feld (Altbestand) ⇒ als abgeschlossen behandeln (true), damit niemand genervt wird.
        if (ensured.OnboardingCompleted is null)
            ensured = ensured with { OnboardingCompleted = existed };

        if (migrate)
        {
            try
            {
                _store.Save(ensured);
                _log.LogInformation("Konfiguration migriert/initialisiert ({Count} Profile).", ensured.Profiles.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Profil-Migration konnte nicht gespeichert werden.");
            }
        }
        return ensured;
    }

    /// <summary>
    /// Loads the config for the start. One the daemon cannot use must not keep it from starting: no client could
    /// then reach it to repair or reset the config, and only root could fix the file. That file is set aside for
    /// inspection and the daemon starts without curves - the start already returned every fan to hardware auto,
    /// so the firmware keeps them, and the watchdog runs as always. If the file at least parsed, its daemon-owned
    /// limits carry over: an operator's stricter over-temperature limit must not fall back to the default just
    /// because, say, a curve list was over the limits.
    /// <para>
    /// A file that could not be read at all (I/O error, access denied) is different: its content may be fine, so
    /// it stays where it is, nothing is saved, and the next start reads it again.
    /// </para>
    /// </summary>
    private AppConfig LoadOrStartFresh()
    {
        AppConfig? parsed = null;
        try
        {
            parsed = _store.Load();
            return LoadAndMigrate(parsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Konfiguration {Path} nicht lesbar - Daemon läuft ohne Kurven (Lüfter auf Hardware-Auto); "
                              + "die Datei bleibt unangetastet, bis eine Änderung aus der GUI sie überschreibt.",
                _store.ConfigPath);
            _configProblem = ConfigLoadProblem.NotLoaded;
            return Normalize(ConfigMapper.Reset(parsed ?? AppConfig.Empty));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Konfiguration {Path} unbrauchbar - Daemon startet ohne Kurven (Lüfter auf Hardware-Auto).",
                _store.ConfigPath);
        }

        // Not onboarded: as on a first install, the GUI offers the setup assistant.
        AppConfig fresh = Normalize(ConfigMapper.Reset(parsed ?? AppConfig.Empty)) with { OnboardingCompleted = false };
        if (parsed is null)
            _log.LogError("Fail-Safe-Grenze der unbrauchbaren Konfiguration nicht lesbar - es gilt der Default {Limit:0.#} °C.",
                fresh.FailSafeTempC);
        _configProblem = ConfigLoadProblem.NotLoaded; // until the file is out of the way
        try
        {
            string aside = _store.SetAside();
            (_configProblem, _configSetAsidePath) = (ConfigLoadProblem.SetAside, aside);
            _log.LogWarning("Unbrauchbare Konfiguration beiseitegelegt: {Path}.", aside);
            _store.Save(fresh);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ersatz-Konfiguration nur im Speicher - die nächste Änderung aus der GUI schreibt die Datei.");
        }
        return fresh;
    }

    /// <summary>
    /// The one order every config from outside goes through - IPC, import, reset, and the file at start: sanitize, then
    /// clear names that are just the hardware label, record the devices of new entries, then apply the active profile,
    /// whose curves and assignments are sanitized by then too.
    /// </summary>
    private AppConfig Normalize(AppConfig raw) =>
        ProfileService.EnsureProfiles(WithHardwareStamps(WithoutHardwareLabels(Sanitized(raw), out _), out _));

    /// <summary>
    /// Records the device behind each detected entry that has none yet (<see cref="HardwareStamps"/>). Best effort and
    /// never throws: on the load path an exception here would get the user's config set aside - the entries then
    /// simply stay without a device until the next try.
    /// </summary>
    private AppConfig WithHardwareStamps(AppConfig config, out int stamped)
    {
        try
        {
            AppConfig result = HardwareStamps.Stamp(config, _fans.DiscoverFans(), _sensors.DiscoverSensors(), out stamped);
            if (stamped > 0)
                _log.LogInformation("{Count} Einträgen das erkannte Gerät zugeordnet.", stamped);
            return result;
        }
        catch (Exception ex)
        {
            stamped = 0;
            _log.LogWarning(ex, "Geräte der Einträge nicht erfassbar - sie bleiben vorerst ohne Gerät.");
            return config;
        }
    }

    /// <summary>
    /// Clears own names that are just the current hardware label (<see cref="OwnNames"/>) - older GUIs and the CLI saved
    /// the label as the name, and it stayed when the hardware behind the id changed. Best effort: without a discovery the
    /// names stay as they are.
    /// </summary>
    private AppConfig WithoutHardwareLabels(AppConfig config, out int cleared)
    {
        cleared = 0;
        try
        {
            AppConfig result = OwnNames.WithoutHardwareLabels(
                config, _sensors.DiscoverSensors(), _fans.DiscoverFans(), out cleared);
            if (cleared > 0)
                _log.LogInformation("{Count} Namen entfernt, die nur die Hardware-Bezeichnung waren.", cleared);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hardware-Bezeichnungen für den Namensabgleich nicht lesbar.");
            return config;
        }
    }

    private AppConfig Sanitized(AppConfig config)
    {
        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);
        foreach (string warning in warnings)
            _log.LogWarning("Konfiguration korrigiert: {Warning}", warning);
        return clean;
    }

    private void OnCommand(IpcCommand command)
    {
        // Before anything logs the strings: a peer's control characters would forge log or journal records.
        if (!IsWellFormed(command.Command) || !IsWellFormed(command.Target) || !IsWellFormed(command.RpmSource))
        {
            _log.LogWarning("IPC-Kommando verworfen: Steuer-/Formatzeichen oder mehr als {Max} Zeichen in Command, Target "
                            + "oder RpmSource.", ConfigLimits.MaxTextLength);
            return;
        }

        switch (command.Command)
        {
            case IpcCommand.Reload:
                _reloadRequested = true;
                break;
            case IpcCommand.SaveConfig when command.Config is { } cfg:
                // Supersedes an older pending switch and toggles (see ApplyPendingConfigChanges): a GUI builds every
                // config after its own switches and toggles, so this one already carries their result.
                Interlocked.Exchange(ref _pendingActiveProfile, null);
                _pendingCurveToggles.Clear();
                Interlocked.Exchange(ref _pendingSave, cfg);
                break;
            case IpcCommand.ReplaceConfig when command.Config is { } cfg:
                Interlocked.Exchange(ref _pendingReplace, cfg);
                break;
            case IpcCommand.ResetConfig:
                _pendingReset = true;
                break;
            case IpcCommand.SetManualPwm when command.Target is { } fan && command.Value is { } pwm:
                if (CanControl(fan))
                {
                    _loop?.SetManualOverride(fan, (byte)Math.Clamp(pwm, 0, 255));
                    // Debug, nicht Info: ein Slider-Zug feuert (trotz GUI-Throttle) viele Befehle -
                    // auf Info würde das die Logs fluten. Der Endzustand ist im Snapshot sichtbar.
                    _log.LogDebug("Manuell: {Fan} → pwm {Pwm}", fan, Math.Clamp(pwm, 0, 255));
                }
                else
                {
                    _log.LogWarning("Manuell abgelehnt: {Fan} ist nicht steuerbar.", fan);
                }
                break;
            case IpcCommand.SetFanAuto when command.Target is { } fan:
                _loop?.SetManualOverride(fan, null);
                _log.LogInformation("Manuell aus: {Fan} → zurück auf Kurve/Auto", fan);
                break;
            case IpcCommand.StartCalibration or IpcCommand.Identify or IpcCommand.StartTachMapping when _dryRun:
                // These drive a fan themselves, past the loop's dry run (see _dryRun).
                _log.LogWarning("{Command} abgelehnt: Dry-Run - ohne Root steuert der Daemon keine Lüfter.", command.Command);
                break;
            case IpcCommand.StartCalibration when command.Target is { } fan:
                // Prüfen-dann-Starten atomar gegen eine parallele Identifikation (anderer IPC-Thread).
                lock (_controlLock)
                {
                    if (!CanControl(fan))
                        _log.LogWarning("Kalibrierung abgelehnt: {Fan} ist nicht steuerbar.", fan);
                    else if (_identify?.IsRunning == true)
                        _log.LogWarning("Kalibrierung abgelehnt: Identifikation läuft.");
                    else if (_tachMapping?.IsRunning == true)
                        _log.LogWarning("Kalibrierung abgelehnt: Sensor-Kopplung läuft.");
                    else
                        _calibration?.Start(new FanId(fan));
                }
                break;
            case IpcCommand.Identify when command.Target is { } fan:
                // IsRunning (echter Lauf-Zustand) statt Status.Running prüfen - Status flippt schon auf
                // false, während der kalibrierte Lüfter im finally noch resumed wird.
                lock (_controlLock)
                {
                    if (!CanControl(fan))
                        _log.LogWarning("Identifikation abgelehnt: {Fan} ist nicht steuerbar.", fan);
                    else if (_calibration?.IsRunning == true)
                        _log.LogWarning("Identifikation abgelehnt: Kalibrierung läuft.");
                    else if (_tachMapping?.IsRunning == true)
                        _log.LogWarning("Identifikation abgelehnt: Sensor-Kopplung läuft.");
                    else
                        _identify?.Start(new FanId(fan));
                }
                break;
            case IpcCommand.StartTachMapping when command.Target is { } fan:
                // Prüfen-dann-Starten atomar gegen parallele Kalibrierung/Identifikation (anderer IPC-Thread).
                lock (_controlLock)
                {
                    if (!CanControl(fan))
                        _log.LogWarning("Sensor-Kopplung abgelehnt: {Fan} ist nicht steuerbar.", fan);
                    else if (_calibration?.IsRunning == true)
                        _log.LogWarning("Sensor-Kopplung abgelehnt: Kalibrierung läuft.");
                    else if (_identify?.IsRunning == true)
                        _log.LogWarning("Sensor-Kopplung abgelehnt: Identifikation läuft.");
                    else
                        _tachMapping?.Start(new FanId(fan));
                }
                break;
            case IpcCommand.CancelTachMapping:
                _tachMapping?.Cancel();
                break;
            case IpcCommand.CancelCalibration:
                _calibration?.Cancel();
                break;
            case IpcCommand.SetFanTachometer when command.Target is { } fan:
                // Fail-safe-neutral (kein Antreiben): nur das RpmSource-Override in der Config setzen/löschen.
                // Each accepted id becomes a persisted fan entry, so only discovered fans and speed sensors count.
                if (!_discoveredFanIds.Contains(fan)
                    || (!string.IsNullOrWhiteSpace(command.RpmSource) && !_discoveredRpmSensorIds.Contains(command.RpmSource.Trim())))
                {
                    _log.LogWarning("Tacho-Zuordnung abgelehnt: Lüfter oder Drehzahl-Sensor nicht erkannt.");
                    break;
                }
                // One sensor, one fan: the GUI offers no sensor another detected fan is coupled to. A measured coupling
                // (QueueTachMappingResult) takes the sensor over instead - the measurement outranks an earlier choice.
                if (CoupledToOtherDetectedFan(fan, command.RpmSource) is { } holder)
                {
                    _log.LogWarning("Tacho-Zuordnung abgelehnt: {Sensor} ist schon {Holder} zugeordnet.",
                        command.RpmSource!.Trim(), holder);
                    break;
                }
                _pendingTachAssignments[fan] = command.RpmSource;
                _log.LogInformation("Tacho-Zuordnung: {Fan} → {Sensor}", fan,
                    string.IsNullOrWhiteSpace(command.RpmSource) ? "(keiner)" : command.RpmSource);
                break;
            case IpcCommand.SetActiveProfile when command.Target is { } profileId:
                Interlocked.Exchange(ref _pendingActiveProfile, profileId);
                break;
            case IpcCommand.SetCurveEnabled when command.Target is { } curveId && command.Value is { } flag:
                if (_config?.Curves.Any(c => c.Id == curveId) != true)
                {
                    _log.LogWarning("Kurve an/aus abgelehnt: keine Kurve mit dieser Id in der aktiven Konfiguration.");
                    break;
                }
                _pendingCurveToggles[curveId] = flag != 0;
                break;
            case IpcCommand.AcceptHardware or IpcCommand.ForgetHardware when command.Target is { } key:
                QueueHardwareCommand(command.Command == IpcCommand.ForgetHardware, key, command.Value);
                break;
            default:
                // Kein Zweig hat gegriffen: unbekanntes Kommando (z. B. Tippfehler) ODER ein bekanntes mit
                // fehlendem/ungültigem Target/Value/Config (die when-Guards). Früher fiel das still durch -
                // jetzt sichtbar melden statt es zur Laufzeit stumm zu verschlucken (AGENTS.md: Fehler/
                // Unsicherheiten melden). Keine Nutzdaten (Config) loggen - nur, ob eine mitkam.
                _log.LogWarning(
                    "IPC-Kommando ignoriert (unbekannt oder unvollständig): {Command} "
                    + "(Target={Target}, Value={Value}, HatConfig={HasConfig}).",
                    command.Command, command.Target, command.Value, command.Config is not null);
                break;
        }
    }

    private static bool IsWellFormed(string? field) =>
        field is null || (field.Length <= ConfigLimits.MaxTextLength && UntrustedText.IsClean(field));

    /// <summary>Übergibt ein Kalibrier-Ergebnis (vom Coordinator-Thread) zur Persistenz an den Tick-Loop.</summary>
    private void QueueCalibrationResult(string fanId, FanCalibration cal)
    {
        if (_stopping)
            return;
        lock (_calLock)
            _pendingCalibration = (fanId, cal);
    }

    /// <summary>Übergibt eine Auto-Kopplung (vom Coordinator-Thread) zur Persistenz an den Tick-Loop.</summary>
    private void QueueTachMappingResult(string fanId, string? tachId)
    {
        if (_stopping)
            return;
        _pendingTachAssignments[fanId] = tachId;
    }

    /// <summary>
    /// Queues <c>acceptHardware</c>/<c>forgetHardware</c> for a change the last snapshot reported. A forget is refused
    /// while a device action runs on one of the change's fans or its result waits to be saved: that result would put the
    /// fan's entry right back, and the fan belongs to the action until it ends. Checked and queued under the lock the
    /// starts of those actions take.
    /// </summary>
    private void QueueHardwareCommand(bool forget, string key, int? value)
    {
        string what = forget ? "Gerät vergessen" : "Gerät übernehmen";
        if (value is not (null or 0 or 1))
        {
            _log.LogWarning("{What} abgelehnt: Wert {Value} ist weder 0 noch 1.", what, value);
            return;
        }
        HardwareChange? change = _reportedChanges.FirstOrDefault(c => c.Key == key);
        if (change is null)
        {
            _log.LogWarning("{What} abgelehnt: keine gemeldete Hardware-Änderung mit diesem Schlüssel.", what);
            return;
        }

        lock (_controlLock)
        {
            if (forget && DeviceActionOn(change.FanIds) is { } action)
            {
                _log.LogWarning("Gerät vergessen abgelehnt: {Action} auf einem betroffenen Lüfter.", action);
                return;
            }
            _pendingHardware[key] = (forget, value == 1);
        }
        _log.LogInformation("{What}: {Key} vorgemerkt.", what, key);
    }

    /// <summary>The device action that runs, or whose result waits, on one of <paramref name="fanIds"/>; <c>null</c> if none.</summary>
    private string? DeviceActionOn(IReadOnlyList<string> fanIds)
    {
        if (fanIds.Count == 0)
            return null;
        if (_calibration is { IsRunning: true } calibration
            && (calibration.Status?.FanId is not { } calibrating || fanIds.Contains(calibrating)))
            return "Kalibrierung läuft";
        lock (_calLock)
        {
            if (_pendingCalibration is { } result && fanIds.Contains(result.FanId))
                return "Kalibrierung wird gespeichert";
        }
        // Identification and coupling take every controllable fan, not only their target.
        if (fanIds.Any(_discoveredFanIds.Contains))
        {
            if (_identify?.IsRunning == true)
                return "Identifikation läuft";
            if (_tachMapping?.IsRunning == true)
                return "Sensor-Kopplung läuft";
        }
        return null;
    }

    private IReadOnlyList<HardwareChange> DetectHardwareChanges(AppConfig config)
    {
        if (_detectHardwareChanges is { } detect)
            return detect(config);
        return _discoveredFans is { } fans && _discoveredSensors is { } sensors
            ? HardwareChanges.Detect(config, fans, sensors, _dryRun)
            : [];
    }

    /// <summary>
    /// The hardware changes of <paramref name="config"/> for the snapshot, <c>null</c> when there are none. Logs each
    /// change once per run. Never throws: a failure reports none (and is logged once), the tick and its snapshot go on.
    /// </summary>
    private IReadOnlyList<IpcHardwareChange>? ReportHardwareChanges(AppConfig config)
    {
        try
        {
            IReadOnlyList<HardwareChange> changes = DetectHardwareChanges(config);
            List<IpcHardwareChange> reported = changes.Select(SnapshotBuilder.ToIpc).ToList();
            _reportedChanges = changes;
            foreach (HardwareChange change in changes)
            {
                if (!_loggedHardwareChanges.Add(change.Key))
                    continue;
                string fans = change.FanIds.Count == 0 ? "-" : string.Join(", ", change.FanIds);
                if (change.Kind == HardwareChangeKind.Replaced)
                    _log.LogWarning("Gerät ersetzt: {Old} → {New} · Lüfter: {Fans} · {Sensors} Sensoren - die GUI fragt, "
                                    + "ob die Einträge übernommen oder neu eingerichtet werden.",
                        DeviceText(change.Old), DeviceText(change.New), fans, change.SensorIds.Count);
                else
                    _log.LogInformation("Gerät nicht gefunden: {Old} · Lüfter: {Fans} · {Sensors} Sensoren.",
                        DeviceText(change.Old), fans, change.SensorIds.Count);
            }
            return reported.Count == 0 ? null : reported;
        }
        catch (Exception ex)
        {
            _reportedChanges = [];
            if (!_hardwareChangesFailed)
                _log.LogWarning(ex, "Hardware-Änderungen nicht bestimmbar - es werden keine gemeldet.");
            _hardwareChangesFailed = true;
            return null;
        }
    }

    /// <summary>
    /// Adds the calibration check's findings to the snapshot (<see cref="SnapshotBuilder.WithCalibrationMismatches"/>).
    /// A notice must not cost the snapshot: if the check fails, the snapshot goes out without findings, logged once per
    /// run. The check only reads what the snapshot read, so the tick is long done and nothing here reaches a fan.
    /// </summary>
    private IpcSnapshot WithCalibrationMismatches(
        IpcSnapshot snapshot, CalibrationPlausibility plausibility, ControlTick tick, AppConfig config)
    {
        try
        {
            return SnapshotBuilder.WithCalibrationMismatches(snapshot, plausibility, tick, config, _dryRun);
        }
        catch (Exception ex)
        {
            if (!_calibrationCheckFailed)
                _log.LogWarning(ex, "Kalibrierung nicht mit der Drehzahl abgleichbar - es werden keine Abweichungen gemeldet.");
            _calibrationCheckFailed = true;
            return snapshot;
        }
    }

    private void RememberDiscovery()
    {
        try
        {
            List<FanDescriptor> fans = _fans.DiscoverFans().ToList();
            List<SensorDescriptor> sensors = _sensors.DiscoverSensors().ToList();
            _discoveredFanIds = fans.Select(f => f.Id.Value).ToHashSet(StringComparer.Ordinal);
            _discoveredRpmSensorIds = sensors
                .Where(s => s.Kind == SensorKind.FanRpm)
                .Select(s => s.Id.Value)
                .ToHashSet(StringComparer.Ordinal);
            (_discoveredFans, _discoveredSensors) = (fans, sensors);
        }
        catch (Exception ex)
        {
            // Without it no tach assignment is accepted and no hardware change reported - degraded, but the daemon keeps
            // running.
            _log.LogWarning(ex, "Discovery nicht lesbar - Tacho-Zuordnungen werden abgelehnt, Hardware-Änderungen nicht "
                                + "gemeldet.");
        }
    }

    /// <summary>The detected fan other than <paramref name="fanId"/> coupled to <paramref name="sensorId"/>, or <c>null</c>.</summary>
    private string? CoupledToOtherDetectedFan(string fanId, string? sensorId)
    {
        if (string.IsNullOrWhiteSpace(sensorId))
            return null;
        string sensor = sensorId.Trim();
        return CurrentCouplings().FirstOrDefault(f =>
            f.FanId != fanId && f.RpmSource == sensor && _discoveredFanIds.Contains(f.FanId))?.FanId;
    }

    /// <summary>
    /// The live config's fans with the tach assignments still waiting for the next tick laid over them (without taking
    /// them out): the onboarding starts a calibration right after a coupling reports its match, and a second
    /// assignment of the same sensor must see the first.
    /// </summary>
    private IReadOnlyList<FanConfig> CurrentCouplings()
    {
        AppConfig config = _config ?? AppConfig.Empty;
        foreach ((string fanId, string? rpmSource) in _pendingTachAssignments)
            config = ConfigMapper.ApplyTachometer(config, fanId, rpmSource, out _);
        return config.Fans;
    }

    /// <summary>
    /// Diagnostic dump at start: what the backend discovers (the devices behind the channels, temperature and speed
    /// sensors with their current value) and - most important for diagnosing "no tach signal" remotely - which speed
    /// sensor each fan channel is read from (<see cref="FanTachometers"/>: its own coupling, else the backend's pairing
    /// unless another fan is coupled to it, else none). Best effort - a failure here is purely diagnostic.
    /// </summary>
    private void LogDiscovery(AppConfig config)
    {
        try
        {
            var sensors = _sensors.DiscoverSensors();
            var temps = sensors.Where(s => s.Kind == SensorKind.Temperature).ToList();
            var rpms = sensors.Where(s => s.Kind == SensorKind.FanRpm).ToList();
            var fans = _fans.DiscoverFans();

            _log.LogInformation("Discovery: {Temps} Temp-Sensoren, {Rpms} Drehzahl-Sensoren, {Fans} Luefter-Kanaele.",
                temps.Count, rpms.Count, fans.Count);
            string devices = string.Join(", ", sensors.Select(s => s.Device).Concat(fans.Select(f => f.Device))
                .OfType<HardwareDevice>()
                .Distinct()
                .OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Fingerprint, StringComparer.Ordinal)
                .Select(DeviceText));
            if (devices.Length > 0)
                _log.LogInformation("  Geräte: {Devices}", devices);
            foreach (SensorDescriptor s in temps)
                _log.LogInformation("  Temp [{Id}] '{Name}' = {Value} °C", s.Id.Value, s.Name, Fmt(_sensors.ReadValue(s.Id)));
            foreach (SensorDescriptor s in rpms)
                _log.LogInformation("  RPM  [{Id}] '{Name}' = {Value}", s.Id.Value, s.Name, Fmt(_sensors.ReadValue(s.Id)));

            var rpmIds = rpms.Select(s => s.Id.Value).ToHashSet();
            var tachometers = new FanTachometers(config.Fans);
            foreach (FanDescriptor f in fans)
            {
                string tach;
                if (tachometers.Of(f) is { } t)
                    tach = !tachometers.HasOwnCoupling(f) ? $"{t.Value} (Heuristik)"
                        : rpmIds.Contains(t.Value) ? $"{t.Value} (zugeordnet)"
                        : $"{t.Value} (zugeordnet, nicht erkannt)";
                else
                    tach = f.Tachometer is { } paired ? $"- kein Tacho ({paired.Value} ist einem anderen Lüfter zugeordnet)" : "- kein Tacho";
                _log.LogInformation("  Fan  [{Id}] '{Name}' steuerbar={Ctl} · Tacho={Tach}",
                    f.Id.Value, f.Name, f.CanControl, tach);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discovery-Dump fehlgeschlagen (nicht kritisch).");
        }
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0.#", CultureInfo.InvariantCulture);

    private bool CanControl(string fanId)
    {
        try { return _fans.CanControl(new FanId(fanId)); }
        catch { return false; } // unbekannter/fehlerhafter Kanal → nicht steuerbar
    }

    // After the last GUI disconnects the daemon keeps regulating through the curves, but a manual value the GUI set
    // must not stay unattended: the loop drops every manual override before each tick while no client is connected
    // (ExecuteAsync) - overrides only take effect in the tick, so that is the one place that needs to.
    // The IPC server logs each connection with its peer; the count is a detail.
    private void OnClientsChanged(int count) => _log.LogDebug("GUI-Clients verbunden: {Count}.", count);

    private void LogTick(ControlTick tick)
    {
        if (tick.FailSafeTriggered)
        {
            _log.LogWarning("FAIL-SAFE ({Reason}) - Lüfter auf Hardware-Auto.",
                tick.FailSafeReason ?? $"{tick.HottestTempC:0.0} °C");
            return;
        }

        foreach (FanAction a in tick.Actions)
        {
            if (a.Kind != FanActionKind.Failed)
                _failingFans.Remove(a.FanId);
            if (a.Kind is not (FanActionKind.FullSpeed or FanActionKind.Failed))
                _fullSpeedFans.Remove(a.FanId);

            switch (a.Kind)
            {
                case FanActionKind.Applied:
                case FanActionKind.DryRun:
                    _log.LogInformation("{Fan}: {Temp:0.0} °C → pwm {Pwm} ({Pct}%) [{Kind}]",
                        a.FanId, a.TemperatureC, a.Pwm, a.Pwm * 100 / 255, a.Kind);
                    break;
                case FanActionKind.Manual:
                    _log.LogInformation("{Fan}: manuell → pwm {Pwm} ({Pct}%)", a.FanId, a.Pwm, a.Pwm * 100 / 255);
                    break;
                case FanActionKind.Held:
                    _log.LogDebug("{Fan}: gehalten ({Temp:0.0} °C)", a.FanId, a.TemperatureC);
                    break;
                case FanActionKind.Skipped:
                    _log.LogDebug("{Fan}: übersprungen ({Note})", a.FanId, a.Note);
                    break;
                case FanActionKind.Failed:
                    // Once per fan until it recovers (a fan that is gone fails every tick), and never during shutdown,
                    // where the latch turns away every manual write on purpose.
                    if (!_stopping && _failingFans.Add(a.FanId))
                        _log.LogWarning("{Fan}: Schreiben fehlgeschlagen ({Note})", a.FanId, a.Note);
                    else
                        _log.LogDebug("{Fan}: Schreiben fehlgeschlagen ({Note})", a.FanId, a.Note);
                    break;
                case FanActionKind.FullSpeed:
                    // The note says whether the channel has no auto mode at all or setting it failed this time.
                    if (_fullSpeedFans.Add(a.FanId))
                        _log.LogWarning("{Fan}: Hardware-Auto nicht setzbar ({Note}) - stattdessen Volllast.", a.FanId, a.Note);
                    else
                        _log.LogDebug("{Fan}: Volllast statt Hardware-Auto", a.FanId);
                    break;
            }
        }
    }

    /// <summary>
    /// Warnt (einmalig je ID) über Kurven-Quellen, die das Backend nicht (mehr) kennt - typisch nach einem
    /// Hardware-Wechsel oder wenn ein Gerät nicht mehr erkannt wird. Die Regelung läuft dann ohne diese Quelle weiter
    /// (<see cref="SensorAggregator"/> → NaN, kein Crash); ohne diese Meldung bliebe der Grund unsichtbar.
    /// </summary>
    private void WarnAboutUnknownSources(AppConfig config)
    {
        try
        {
            foreach (string id in UnknownSourceIds(config, _sensors.DiscoverSensors()))
            {
                if (!_warnedUnknownSensors.Add(id))
                    continue; // diese ID wurde in diesem Daemon-Lauf bereits gemeldet
                _log.LogWarning(
                    "Kurven-Quelle {Sensor} ist dem Backend nicht bekannt (Hardware gewechselt oder nicht erkannt?) - "
                    + "betroffene Kurve(n) regeln ohne diese Quelle. Bitte die Quelle der Kurve neu zuordnen.",
                    id);
            }
        }
        catch (Exception ex)
        {
            // A diagnostic: it runs before IPC is up at start and must not take the daemon down with it.
            _log.LogWarning(ex, "Prüfung der Kurven-Quellen fehlgeschlagen (nicht kritisch).");
        }
    }

    /// <summary>
    /// Liefert die in den aktiven Kurven als Quelle referenzierten Sensor-IDs, die nicht in der Discovery
    /// vorkommen (dedupliziert). Reine Funktion - testbar ohne laufenden Dienst.
    /// </summary>
    internal static IReadOnlyList<string> UnknownSourceIds(AppConfig config, IEnumerable<SensorDescriptor> discovered)
    {
        var known = discovered.Select(s => s.Id.Value).ToHashSet(StringComparer.Ordinal);
        return config.Curves
            .SelectMany(c => c.SourceSensorIds)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !known.Contains(id))
            .ToList();
    }
}
