// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Hosting;
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

    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly IConfigStore _store;
    private readonly IIpcServer _ipc;
    private readonly ILogger<ControlLoopService> _log;

    private volatile bool _reloadRequested;
    private volatile bool _pendingReset;  // Config auf Werkszustand zurücksetzen (GUI-Befehl)
    private IpcConfig? _pendingSave;   // von der GUI gesendete Config (per Interlocked übergeben)
    private IpcConfig? _pendingReplace; // von der GUI gesendete Config, die die bestehende vollständig ersetzt
    private string? _pendingActiveProfile;
    // Kurve-an/aus vom Dashboard: gepuffert je Tick (Queue statt Einzel-Slot, damit rasche Mehrfach-
    // Umschaltungen verschiedener Kurven nicht verloren gehen). Wird auf dem Loop-Thread gedrained.
    private readonly ConcurrentQueue<(string CurveId, bool Enabled)> _pendingCurveToggles = new();
    // Tacho-Zuordnung(en) (manuell / Auto-Kopplung): je Lüfter das RpmSource-Override setzen/löschen.
    // Queue statt Einzel-Slot, damit das Onboarding mehrere Lüfter in Folge zuordnen kann, ohne dass
    // Zuordnungen verloren gehen; wird auf dem Loop-Thread gedrained.
    private readonly ConcurrentQueue<(string FanId, string? RpmSource)> _pendingTachAssignments = new();
    private ControlLoop? _loop;        // für thread-sichere Steuerbefehle aus dem IPC-Thread
    private CalibrationCoordinator? _calibration;
    private IdentifyCoordinator? _identify;
    private TachMappingCoordinator? _tachMapping;
    private volatile AppConfig? _config;  // Live-Config (Referenz-Swap atomar) — u. a. für den Kalibrier-Watchdog

    private readonly object _calLock = new();
    private (string FanId, FanCalibration Cal)? _pendingCalibration;

    // Serialisiert die wechselseitig ausschließenden Lüfter-Aktionen (Kalibrierung ⇄ Identifikation):
    // OnCommand kann pro IPC-Client nebenläufig laufen, daher muss „prüfen-dann-starten" atomar sein.
    private readonly object _controlLock = new();

    // Bereits gemeldete unbekannte Kurven-Quellen — dedup, damit je ID nur einmal pro Daemon-Lauf gewarnt wird.
    private readonly HashSet<string> _warnedUnknownSensors = new();

    // Test-Seam: überschreibt das aus der Config abgeleitete Tick-Intervall (Default: aus der Config).
    private readonly TimeSpan? _tickIntervalOverride;

    // Test-Seam: injizierbare CalibrationService-Factory (Default: echte Delays), analog zum
    // bereits vorhandenen Factory-Parameter des CalibrationCoordinator.
    private readonly Func<ISensorBackend, IFanController, CalibrationService>? _calibrationFactory;

    // Test-Seam: injizierbare Warte-Funktion des Identifikations-Pulses (Default: echtes Task.Delay).
    private readonly Func<TimeSpan, CancellationToken, Task>? _identifyDelay;

    // Test-Seam: injizierbare TachometerMappingService-Factory (Default: echte Delays), analog zur
    // CalibrationService-Factory — lässt einen Test die Auto-Kopplung ohne echte Settle-Wartezeit prüfen.
    private readonly Func<ISensorBackend, IFanController, TachometerMappingService>? _tachMappingFactory;

    // Test-Seam: Obergrenze je Coordinator-Stop im Shutdown (Default: DefaultCoordinatorStopTimeout).
    // Erlaubt einem Test, den „hängender Stop"-Pfad ohne echte 3-s-Wartezeit zu prüfen.
    private readonly TimeSpan _coordinatorStopTimeout;

    // Test-Seam: erzwingt den Dry-Run-Zustand (Default: aus den Prozessrechten via Privileges.IsElevated()).
    // Nötig, weil Unit-Tests je nach Runner mit ODER ohne Root laufen (der CI-Container läuft als Root) —
    // ohne Seam hinge der berichtete DaemonStatus (DryRun/Active) an der euid des Test-Runners. Produktiv
    // bleibt der Parameter null → die echte Rechte-Erkennung entscheidet.
    private readonly bool? _dryRunOverride;

    public ControlLoopService(
        ISensorBackend sensors, IFanController fans, IConfigStore store, IIpcServer ipc,
        ILogger<ControlLoopService> log, TimeSpan? tickInterval = null,
        Func<ISensorBackend, IFanController, CalibrationService>? calibrationFactory = null,
        Func<TimeSpan, CancellationToken, Task>? identifyDelay = null,
        TimeSpan? coordinatorStopTimeout = null,
        Func<ISensorBackend, IFanController, TachometerMappingService>? tachMappingFactory = null,
        bool? dryRunOverride = null)
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
        _dryRunOverride = dryRunOverride;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        bool dryRun = _dryRunOverride ?? !Privileges.IsElevated();
        var loop = new ControlLoop(_sensors, _fans, dryRun);
        _loop = loop; // Steuerbefehle (Manual/Auto) greifen thread-sicher über die Loop-Methoden
        var calibration = new CalibrationCoordinator(
            _sensors, _fans, _log, loop.Suspend, loop.Resume, QueueCalibrationResult, ct, _calibrationFactory,
            failSafeTempC: () => _config?.FailSafeTempC ?? CalibrationOptions.DefaultFailSafeTempC,
            tachometerOverride: ResolveTachOverride);
        _calibration = calibration;
        var identify = new IdentifyCoordinator(
            _sensors, _fans, _log, loop.Suspend, loop.Resume, ct, _identifyDelay,
            failSafeTempC: () => _config?.FailSafeTempC ?? CalibrationOptions.DefaultFailSafeTempC);
        _identify = identify;
        var tachMapping = new TachMappingCoordinator(
            _sensors, _fans, _log, loop.Suspend, loop.Resume,
            (fanId, tachId) => _pendingTachAssignments.Enqueue((fanId, tachId)),
            ct, _tachMappingFactory,
            failSafeTempC: () => _config?.FailSafeTempC ?? CalibrationOptions.DefaultFailSafeTempC);
        _tachMapping = tachMapping;

        // Setup (Config laden, IPC starten) liegt INNERHALB des try, damit das finally — und damit
        // RestoreDefaults — auch bei einem Setup-Fehler garantiert läuft (Fail-Safe, M1).
        try
        {
            AppConfig config = LoadAndMigrate();
            _config = config;
            WarnAboutUnknownSources(config);
            int interval = Math.Max(ConfigSanitizer.MinPollIntervalMs, config.PollIntervalMs);

            _ipc.CommandHandler = OnCommand;
            _ipc.ClientsChanged = OnClientsChanged;
            await _ipc.StartAsync(ct).ConfigureAwait(false);
            await Task.Yield(); // Host-Start abschließen, bevor der erste Tick läuft
            _log.LogInformation("IPC-Server hört auf {Path}", _ipc.Path);
            _log.LogInformation(
                "ControlLoop läuft · dryRun={DryRun} · {Fans} Lüfter, {Curves} Kurven · Intervall {Interval} ms · Config: {Path}",
                dryRun, config.Fans.Count, config.Curves.Count, interval, _store.ConfigPath);
            LogDiscovery(config); // Diagnose: was das Backend sieht + welcher Tacho zu welchem Lüfter gehört

            using var timer = new PeriodicTimer(_tickIntervalOverride ?? TimeSpan.FromMilliseconds(interval));
            do
            {
                // Ein einzelner fehlgeschlagener Tick darf den Daemon nie beenden (er steuert Hardware).
                try
                {
                    AppConfig updated = ApplyPendingConfigChanges(config);
                    if (!ReferenceEquals(updated, config))
                    {
                        config = updated;
                        _config = updated; // Live-Config aktuell halten (Kalibrier-Watchdog liest sie)
                        loop.ResetHysteresis(); // neue Kurven sofort anwenden, nicht erst bei Temp-Drift
                        WarnAboutUnknownSources(updated); // z. B. nach Profilwechsel auf Kurven mit toter Quelle
                    }

                    ControlTick tick = loop.Tick(config);
                    LogTick(tick);

                    // Fail-Safe (Übertemperatur/blind) bricht laufende Kalibrierung UND Identifikation
                    // sofort ab, damit deren Writes den sicheren Zustand nicht gleich wieder übertrampeln.
                    if (tick.FailSafeTriggered)
                    {
                        calibration.Cancel();
                        identify.Cancel();
                        tachMapping.Cancel();
                    }

                    DaemonStatus status = dryRun ? DaemonStatus.DryRun : DaemonStatus.Active;
                    IpcSnapshot snapshot = SnapshotBuilder.Build(
                        _sensors, _fans, status, dryRun, tick.HottestTempC, config,
                        loop.ManualFanIds(), calibration.Status, identify.Status, tachMapping.Status);
                    await _ipc.BroadcastAsync(snapshot).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Tick fehlgeschlagen — übersprungen.");
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
            _fans.RestoreDefaults();
            _log.LogInformation("ControlLoop gestoppt — Fail-Safe RestoreDefaults ausgeführt.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Laufende Kalibrierung erst sauber beenden (Rampe stoppt, Lüfter zurück auf Auto), BEVOR
        // der ExecuteAsync-finally das abschließende RestoreDefaults ausführt — sonst könnte die
        // weiterlaufende Rampe den sicheren Zustand wieder übertrampeln.
        //
        // Fail-Safe: je Stop nur BEGRENZT warten. Hängt ein Coordinator-Stop (z. B. hinter einem im
        // EC/Treiber festhängenden Hardware-Write), käme sonst weder base.StopAsync (cancelt das
        // Stopp-Token) noch das finally-RestoreDefaults in ExecuteAsync je dran — systemd SIGKILLt
        // nach TimeoutStopSec und überspränge damit jedes Fail-Safe-Restore. Nach dem (ggf. abge-
        // brochenen) Warten cancelt base.StopAsync das Token; der abgebrochene Coordinator-Lauf
        // bricht dann selbst ab und das finally stellt den sicheren Zustand her.
        if (_calibration is not null)
            await StopBoundedAsync(_calibration.StopAsync, "Kalibrierung").ConfigureAwait(false);
        if (_identify is not null)
            await StopBoundedAsync(_identify.StopAsync, "Identifikation").ConfigureAwait(false);
        if (_tachMapping is not null)
            await StopBoundedAsync(_tachMapping.StopAsync, "Sensor-Kopplung").ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Rest-Fenster schließen (Fail-Safe-Audit): ein nach dem Stop-Timeout spät aufwachender,
        // verwaister Coordinator-Write könnte das finally-RestoreDefaults noch mit einem Rampenwert
        // überschreiben. Ein idempotenter Abschluss-Restore als ALLERLETZTER Schritt stellt sicher,
        // dass der zuletzt wirksame Write Hardware-Auto ist. RestoreDefaults ist gate-begrenzt (hängt
        // nicht) und wirft laut Vertrag nicht — der Catch ist reine Vorsicht am äußersten Rand.
        try { _fans.RestoreDefaults(); }
        catch (Exception ex) { _log.LogWarning(ex, "Abschluss-RestoreDefaults beim Shutdown fehlgeschlagen."); }
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
            await stop().WaitAsync(_coordinatorStopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogWarning(
                "{Coordinator}-Stop nach {Ms} ms nicht abgeschlossen — Shutdown fährt fort; das "
                + "finally-RestoreDefaults stellt den sicheren Zustand her.",
                label, _coordinatorStopTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Coordinator}-Stop beim Shutdown fehlgeschlagen — Shutdown fährt fort.", label);
        }
    }

    /// <summary>
    /// Führt vor einem Reset/Import alle Lüfter in den sicheren Firmware-Auto-Zustand zurück: laufende
    /// Kalibrierung/Identifikation abbrechen (ihr Ergebnis ist danach gegenstandslos, und die Rampe würde
    /// sonst gegen <see cref="IFanController.RestoreDefaults"/> schreiben), GUI-Manual-Overrides fallen lassen
    /// und Hardware-Auto wiederherstellen. Ohne das bliebe ein zuvor kurvengeregelter Lüfter, der in der
    /// neuen Config fehlt, im Manual-Zustand hängen — ohne Kurve, ohne Firmware-Regelung (Fail-Safe-Lücke).
    /// Läuft auf dem Loop-Thread, wie alle anderen Hardware-Writes (dasselbe Cancel-Muster wie der Übertemp-
    /// Fail-Safe in <see cref="ExecuteAsync"/>).
    /// </summary>
    private void ReturnAllFansToSafeAuto()
    {
        _calibration?.Cancel();
        _identify?.Cancel();
        _tachMapping?.Cancel();
        _loop?.ClearAllManualOverrides();
        _fans.RestoreDefaults();
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
            AppConfig merged = Sanitized(ConfigMapper.ApplyCalibration(current, c.FanId, c.Cal));
            _store.Save(merged);
            _log.LogInformation("Kalibrierung übernommen: {Fan} · MinPwm={Pwm}.", c.FanId, c.Cal.StartPwm);
            return merged;
        }

        // Profilwechsel (Quick-Command) → Profil-Zuordnungen anwenden, persistieren.
        if (Interlocked.Exchange(ref _pendingActiveProfile, null) is { } profileId)
        {
            AppConfig switched = Sanitized(ProfileService.Apply(current, profileId));
            _store.Save(switched);
            _log.LogInformation("Profil aktiviert: {Profile}.", profileId);
            return switched;
        }

        // Kurve an/aus (Dashboard-Quick-Command) → Enabled-Flag setzen, persistieren. Deaktivierte
        // Kurven stellen ihre Lüfter im nächsten Tick auf Hardware-Auto (ControlLoop), Re-Enable wirkt
        // sofort (ResetHysteresis nach dem Config-Swap in ExecuteAsync).
        if (!_pendingCurveToggles.IsEmpty)
        {
            var toggles = new List<(string CurveId, bool Enabled)>();
            while (_pendingCurveToggles.TryDequeue(out var t))
                toggles.Add(t);
            AppConfig toggled = Sanitized(ApplyCurveToggles(current, toggles));
            _store.Save(toggled);
            _log.LogInformation("Kurven an/aus übernommen: {Toggles}.",
                string.Join(", ", toggles.Select(t => $"{t.CurveId}={(t.Enabled ? "an" : "aus")}")));
            return toggled;
        }

        // Tacho-Zuordnung(en) (manuell / Auto-Kopplung) → RpmSource-Override je Lüfter setzen/löschen,
        // einmal persistieren. Nur Config-Zustand, kein Hardware-Eingriff.
        if (!_pendingTachAssignments.IsEmpty)
        {
            AppConfig updated = current;
            var applied = new List<string>();
            while (_pendingTachAssignments.TryDequeue(out var a))
            {
                updated = ConfigMapper.ApplyTachometer(updated, a.FanId, a.RpmSource);
                applied.Add($"{a.FanId}={(string.IsNullOrWhiteSpace(a.RpmSource) ? "(keiner)" : a.RpmSource)}");
            }
            AppConfig saved = Sanitized(updated);
            _store.Save(saved);
            _log.LogInformation("Tacho-Zuordnung übernommen: {Assignments}.", string.Join(", ", applied));
            return saved;
        }

        // GUI hat eine Konfiguration gesendet → autoritativ zusammenführen, persistieren, übernehmen.
        if (Interlocked.Exchange(ref _pendingSave, null) is { } incoming)
        {
            AppConfig merged = ProfileService.EnsureProfiles(Sanitized(ConfigMapper.Merge(current, incoming)));
            _store.Save(merged);
            _log.LogInformation("Konfiguration von der GUI übernommen & gespeichert ({Fans} Lüfter, {Curves} Kurven).",
                merged.Fans.Count, merged.Curves.Count);
            return merged;
        }

        // GUI hat einen Import geschickt → bestehende Config VOLLSTÄNDIG ersetzen (kein Merge), persistieren.
        // MigrateHwmonIds macht ältere Backups robust (der Save-Pfad läuft sonst nicht durch die Migration).
        if (Interlocked.Exchange(ref _pendingReplace, null) is { } toReplace)
        {
            // Fail-Safe: zuvor kurvengeregelte Lüfter, die im Import fehlen, würden sonst im Manual-Zustand
            // hängen bleiben (nicht mehr in config.Fans → nie auf Auto zurückgestellt). Erst alle auf sicheres
            // Firmware-Auto; der Tick nach dem Swap regelt die im Import enthaltenen Lüfter sofort wieder.
            ReturnAllFansToSafeAuto();
            AppConfig replaced = ProfileService.EnsureProfiles(
                Sanitized(MigrateHwmonIds(ConfigMapper.Replace(current, toReplace), out _)));
            _store.Save(replaced);
            _log.LogInformation("Konfiguration importiert & vollständig ersetzt ({Fans} Lüfter, {Curves} Kurven).",
                replaced.Fans.Count, replaced.Curves.Count);
            return replaced;
        }

        // GUI hat einen Reset ausgelöst → auf Werkszustand zurück (alles leer). Die Hardware bleibt entdeckt
        // (Backends sind config-unabhängig); ohne Overrides zeigt der Snapshot rohe Hardware-Namen. Onboarding
        // wird bewusst NICHT erzwungen (OnboardingCompleted=true) — dafür gibt es den eigenen Menüpunkt.
        if (_pendingReset)
        {
            _pendingReset = false;
            // Fail-Safe: leere Config ⇒ keine Lüfter mehr in config.Fans ⇒ ein zuvor kurvengeregelter Lüfter
            // würde ohne Rückstellung im Manual-Zustand (fixer PWM) hängen. Daher alle auf Firmware-Auto.
            ReturnAllFansToSafeAuto();
            AppConfig fresh = ProfileService.EnsureProfiles(
                Sanitized(AppConfig.Empty with { OnboardingCompleted = true }));
            _store.Save(fresh);
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
    /// Setzt das <see cref="CurveConfig.Enabled"/>-Flag der genannten Kurven — sowohl in den aktiven
    /// <c>config.Curves</c> als auch in den Kurven des aktiven Profils, damit ein späterer Profilwechsel
    /// (<see cref="ProfileService.Apply"/>) den Stand nicht wieder überschreibt. Reine Funktion (testbar).
    /// </summary>
    internal static AppConfig ApplyCurveToggles(AppConfig config, IReadOnlyList<(string CurveId, bool Enabled)> toggles)
    {
        var desired = new Dictionary<string, bool>();
        foreach (var (id, enabled) in toggles)
            desired[id] = enabled; // letzter Wert je Kurve gewinnt

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
    private AppConfig LoadAndMigrate()
    {
        // First-Run-Signal VOR dem Laden/Speichern festhalten: existiert noch keine Config-Datei,
        // ist dies eine frische Installation (der Daemon selbst legt die Datei gleich an).
        bool existed = _store.Exists;
        AppConfig loaded = Sanitized(_store.Load());
        AppConfig clean = MigrateHwmonIds(loaded, out bool idsRewritten);
        bool migrate = idsRewritten                            // stabile Ids müssen persistiert werden
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
                _log.LogInformation("Profile migriert/initialisiert ({Count} Profile).", ensured.Profiles.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Profil-Migration konnte nicht gespeichert werden.");
            }
        }
        return ensured;
    }

    private AppConfig Sanitized(AppConfig config)
    {
        AppConfig clean = ConfigSanitizer.Sanitize(config, out IReadOnlyList<string> warnings);
        foreach (string warning in warnings)
            _log.LogWarning("Konfiguration korrigiert: {Warning}", warning);
        return clean;
    }

    /// <summary>
    /// Schema-2→3: instabile <c>hwmonN/…</c>-Ids einmalig auf das stabile <c>chip/channel</c>-Schema
    /// umschreiben, sofern das Backend eine Legacy-Zuordnung liefert (Linux; Windows-LHM war schon
    /// stabil → kein <see cref="ILegacyIdMap"/>). Best effort: Ids, die die aktuelle Enumeration nicht
    /// mehr kennt, bleiben stehen und degradieren wie gehabt (NaN + einmalige Warnung in
    /// <see cref="WarnAboutUnknownSources"/>, manuelle Neu-Zuordnung im Geräte-Tab).
    /// </summary>
    private AppConfig MigrateHwmonIds(AppConfig config, out bool changed)
    {
        changed = false;
        if (_sensors is not ILegacyIdMap map)
            return config;

        AppConfig migrated = HwmonIdMigration.Apply(config, map.LegacyToStableIds(), out changed);
        if (changed)
            _log.LogInformation(
                "hwmon-Ids auf stabiles Schema migriert (chip/channel) — übersteht künftige hwmonN-Umnummerierung.");
        return migrated;
    }

    private void OnCommand(IpcCommand command)
    {
        switch (command.Command)
        {
            case IpcCommand.Reload:
                _reloadRequested = true;
                break;
            case IpcCommand.SaveConfig when command.Config is { } cfg:
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
                    // Debug, nicht Info: ein Slider-Zug feuert (trotz GUI-Throttle) viele Befehle —
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
                // IsRunning (echter Lauf-Zustand) statt Status.Running prüfen — Status flippt schon auf
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
                _pendingTachAssignments.Enqueue((fan, command.RpmSource));
                _log.LogInformation("Tacho-Zuordnung: {Fan} → {Sensor}", fan,
                    string.IsNullOrWhiteSpace(command.RpmSource) ? "(keiner)" : command.RpmSource);
                break;
            case IpcCommand.SetActiveProfile when command.Target is { } profileId:
                Interlocked.Exchange(ref _pendingActiveProfile, profileId);
                break;
            case IpcCommand.SetCurveEnabled when command.Target is { } curveId && command.Value is { } flag:
                _pendingCurveToggles.Enqueue((curveId, flag != 0));
                break;
            default:
                // Kein Zweig hat gegriffen: unbekanntes Kommando (z. B. Tippfehler) ODER ein bekanntes mit
                // fehlendem/ungültigem Target/Value/Config (die when-Guards). Früher fiel das still durch —
                // jetzt sichtbar melden statt es zur Laufzeit stumm zu verschlucken (AGENTS.md: Fehler/
                // Unsicherheiten melden). Keine Nutzdaten (Config) loggen — nur, ob eine mitkam.
                _log.LogWarning(
                    "IPC-Kommando ignoriert (unbekannt oder unvollständig): {Command} "
                    + "(Target={Target}, Value={Value}, HatConfig={HasConfig}).",
                    command.Command, command.Target, command.Value, command.Config is not null);
                break;
        }
    }

    /// <summary>Übergibt ein Kalibrier-Ergebnis (vom Coordinator-Thread) zur Persistenz an den Tick-Loop.</summary>
    private void QueueCalibrationResult(string fanId, FanCalibration cal)
    {
        lock (_calLock)
            _pendingCalibration = (fanId, cal);
    }

    /// <summary>
    /// Effektiver Drehzahl-Sensor eines Lüfters für die Kalibrierung: das konfigurierte
    /// <see cref="FanConfig.RpmSource"/>-Override, aber nur wenn es einen aktuell vorhandenen RPM-Sensor
    /// benennt — ein veraltetes Override darf die Rampe nicht auf einen unbekannten Sensor messen lassen
    /// (<c>ReadValue</c> würfe sonst). Sonst <c>null</c> ⇒ der Coordinator nimmt den Backend-Tacho.
    /// </summary>
    private SensorId? ResolveTachOverride(FanId fanId)
    {
        if (_config?.Fans.FirstOrDefault(f => f.FanId == fanId.Value)?.RpmSource is not { Length: > 0 } rs)
            return null;
        bool known = _sensors.DiscoverSensors().Any(s => s.Kind == SensorKind.FanRpm && s.Id.Value == rs);
        return known ? new SensorId(rs) : null;
    }

    /// <summary>
    /// Diagnose-Dump beim Start: was das Backend entdeckt (Temp-/Drehzahl-Sensoren mit Momentanwert) und —
    /// am wichtigsten — welcher Tacho je Lüfter-Kanal wirkt (zugeordnetes <see cref="FanConfig.RpmSource"/>
    /// vor der Backend-Heuristik, sonst deren Paarung, sonst keiner). Genau das, was zum Ferndiagnostizieren
    /// von „kein Tachosignal" fehlte. Best-effort — ein Fehler hier ist rein diagnostisch, nie kritisch.
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
            foreach (SensorDescriptor s in temps)
                _log.LogInformation("  Temp [{Id}] '{Name}' = {Value} °C", s.Id.Value, s.Name, Fmt(_sensors.ReadValue(s.Id)));
            foreach (SensorDescriptor s in rpms)
                _log.LogInformation("  RPM  [{Id}] '{Name}' = {Value}", s.Id.Value, s.Name, Fmt(_sensors.ReadValue(s.Id)));

            var rpmIds = rpms.Select(s => s.Id.Value).ToHashSet();
            foreach (FanDescriptor f in fans)
            {
                string? overrideId = config.Fans.FirstOrDefault(c => c.FanId == f.Id.Value)?.RpmSource;
                string tach = !string.IsNullOrEmpty(overrideId) && rpmIds.Contains(overrideId)
                    ? $"{overrideId} (zugeordnet)"
                    : f.Tachometer is { } t ? $"{t.Value} (Heuristik)" : "— kein Tacho";
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

    // GUI-Disconnect: der Daemon regelt autonom über die Kurven weiter. ABER eine zuletzt manuell
    // gesetzte Steuerung (GUI-getrieben) darf nicht ohne Aufsicht stehen bleiben → beim letzten
    // getrennten Client alle manuellen Overrides verwerfen (Lüfter zurück auf Kurven-Regelung).
    private void OnClientsChanged(int count)
    {
        _log.LogInformation("GUI-Clients verbunden: {Count}.", count);
        if (count == 0)
            _loop?.ClearAllManualOverrides();
    }

    private void LogTick(ControlTick tick)
    {
        if (tick.FailSafeTriggered)
        {
            _log.LogWarning("FAIL-SAFE ({Reason}) — Lüfter auf Hardware-Auto.",
                tick.FailSafeReason ?? $"{tick.HottestTempC:0.0} °C");
            return;
        }

        foreach (FanAction a in tick.Actions)
        {
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
                    _log.LogWarning("{Fan}: Schreiben fehlgeschlagen ({Note})", a.FanId, a.Note);
                    break;
            }
        }
    }

    /// <summary>
    /// Warnt (einmalig je ID) über Kurven-Quellen, die das Backend nicht (mehr) kennt — typisch nach
    /// hwmon-Neunummerierung (Reboot/Kernel-Update). Die Regelung läuft dann ohne diese Quelle weiter
    /// (<see cref="SensorAggregator"/> → NaN, kein Crash); ohne diese Meldung bliebe der Grund unsichtbar.
    /// </summary>
    private void WarnAboutUnknownSources(AppConfig config)
    {
        foreach (string id in UnknownSourceIds(config, _sensors.DiscoverSensors()))
        {
            if (!_warnedUnknownSensors.Add(id))
                continue; // diese ID wurde in diesem Daemon-Lauf bereits gemeldet
            _log.LogWarning(
                "Kurven-Quelle {Sensor} ist dem Backend nicht bekannt (hwmon-Nummerierung geändert?) — "
                + "betroffene Kurve(n) regeln ohne diese Quelle. Bitte im Kurven-Tab neu zuordnen.",
                id);
        }
    }

    /// <summary>
    /// Liefert die in den aktiven Kurven als Quelle referenzierten Sensor-IDs, die nicht in der Discovery
    /// vorkommen (dedupliziert). Reine Funktion — testbar ohne laufenden Dienst.
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
