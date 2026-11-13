// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// MVC-Controller des Hauptfensters: hält den live aktualisierten Dashboard-Zustand (Temperaturen,
/// Lüfter) und stellt den Kurven-Editor (<see cref="Editor"/>) bereit. Die Daten kommen über den
/// IPC-Client vom Daemon (kein Hardware-Zugriff in der GUI); Updates werden per
/// <see cref="Dispatcher"/> auf den UI-Thread gemarshalt.
/// </summary>
public partial class MainController : ObservableObject, IDisposable
{
    private readonly ILiveMonitor _monitor;
    private readonly ICommandSink? _sink;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly bool _ownsMonitor; // true nur, wenn der Controller den Monitor selbst erzeugt hat
    private int _disposed;
    private readonly Dictionary<string, SensorRow> _sensorRows = new();
    private readonly Dictionary<string, FanRow> _fanRows = new(); // every fan, hidden ones too (the loops show them)
    private readonly List<FanRow> _fanOrder = new();               // the same rows in discovery order
    private readonly TimeProvider _clock;
    private readonly GuiErrorLog _errorLog;

    // Auto-Ausblenden des Kalibrier-Banners nach erfolgreichem Abschluss (Fehler bleiben bis manuell geschlossen).
    private CancellationTokenSource? _calibrationAutoHideCts;
    private static readonly TimeSpan CalibrationAutoHide = TimeSpan.FromSeconds(5);

    /// <summary>The visible temperature sensors.</summary>
    public ObservableCollection<SensorRow> Temperatures { get; } = new();

    /// <summary>The overview's temperature tiles: one per group in the saved order, "ungrouped" last.</summary>
    public ObservableCollection<SensorGroup> SensorGroups { get; } = new();

    /// <summary>The visible fans in the overview table's order (by position).</summary>
    public ObservableCollection<FanRow> Fans { get; } = new();

    public CurveEditorController Editor { get; }

    /// <summary>GUI-lokale Einstellungen (Theme, Tray) - als ein DataContext über das Hauptfenster gebunden.</summary>
    public SettingsController Settings { get; }

    /// <summary>Sicherung/Wiederherstellung/Reset im Einstellungen-Tab (Export/Import/Reset via IPC).</summary>
    public BackupController Backup { get; }

    /// <summary>Update-Hinweis (GitHub-Release-Check + dismissierbares Banner); der Check startet via <see cref="BeginUpdateCheck"/>.</summary>
    public UpdateController Update { get; } = new();

    // Zuletzt vom Daemon empfangene Config (autoritativ, inkl. Kalibrierung) - Quelle für den Backup-Export.
    private AppConfig _lastConfig = AppConfig.Empty;

    // Nach Reset/Import: den Editor neu aufbauen, sobald der Daemon die geänderte Config zurückspiegelt.
    // Auf die Signatur-Änderung warten (nicht schon beim Absenden), damit der Neuaufbau die NEUE Config trifft.
    private bool _awaitingConfigResync;
    private string _preResyncSignature = "";
    private int _resyncGraceTicks;
    private Func<MonitorSnapshot, bool>? _resyncUntil;   // a further condition of the rebuild - see ArmConfigResync
    private TaskCompletionSource<bool>? _resyncOutcome; // whether the daemon reported the change - see ArmConfigResync

    /// <summary>
    /// How many fresh snapshots (daemon ticks) an armed resync waits for the expected config change. Then it is
    /// disarmed: had the daemon written nothing new (the config sent was the same, or it refused it), the resync would
    /// stay armed for good, and a much later, foreign change (profile switch, curve on/off) would rebuild the editor
    /// together with its unsaved edits.
    /// </summary>
    internal const int ResyncGraceTicks = 10;

    [ObservableProperty] private string _status = Localizer.Instance["MainCtrl.Connecting"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSensors), nameof(ShowNoFans), nameof(OverviewFrozen))]
    private bool _connected;

    /// <summary>Offline and dry-run banners above the tabs, with "Reconnect" and the "reconnected" toast.</summary>
    public ConnectionController Connection { get; }

    /// <summary>The version lines under the settings menu (this app, and the service when it runs another build).</summary>
    public VersionController Version { get; } = new();

    /// <summary>
    /// The toast after the GUI caught an error it did not expect: it keeps running, but may show something half
    /// updated. An error toast - it stays until closed - naming <see cref="ErrorLogPath"/>.
    /// </summary>
    public TransientStatus ErrorNotice { get; } = new();

    /// <summary>Where caught errors are recorded - shown selectable in the error toast and the unreadable-data banner.</summary>
    public string ErrorLogPath => _errorLog.FilePath;

    [ObservableProperty] private CalibrationStatus? _calibration;
    [ObservableProperty] private OnboardingController? _onboarding;

    /// <summary>The selected area (0 = overview, 1 = profiles &amp; curves, 2 = settings) - the header and the overview's links switch it.</summary>
    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>True, wenn das Dashboard mindestens einen sichtbaren Temperatursensor zeigt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSensors))]
    private bool _hasSensors;

    /// <summary>True, wenn das Dashboard mindestens einen sichtbaren Lüfter zeigt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFans))]
    private bool _hasFans;

    /// <summary>Text des Pre-Load-Hinweises im Geräte-Tab: trennt „verbinde" (kein Daemon) von „lade" (verbunden, noch nichts da).</summary>
    [ObservableProperty] private string _deviceLoadingText = Localizer.Instance["MainCtrl.ConnectingToService"];

    /// <summary>Wie <see cref="DeviceLoadingText"/>, aber für den Kurven-Tab.</summary>
    [ObservableProperty] private string _curveLoadingText = Localizer.Instance["MainCtrl.ConnectingToService"];

    /// <summary>
    /// "No visible temperature sensors": only while connected and loaded - without the daemon nothing is known, so the
    /// overview never claims there is nothing.
    /// </summary>
    public bool ShowNoSensors => HasLiveData && Connected && !HasSensors;

    /// <summary>"No visible fans" - the same rule as <see cref="ShowNoSensors"/>.</summary>
    public bool ShowNoFans => HasLiveData && Connected && !HasFans;

    /// <summary>
    /// Geräte-Tab: „Keine Geräte erkannt", wenn der Daemon verbunden ist und einen Snapshot ohne jedes Gerät
    /// geliefert hat. Bewusst NICHT an <c>Editor.IsReady</c> gekoppelt: der Editor bleibt bei null Geräten absichtlich
    /// „nicht bereit" (kein leeres Speichern), sonst würde dieser Hinweis nie erscheinen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceLoading), nameof(ShowCurveLoading))]
    private bool _showNoDevices;

    /// <summary>Geräte-Tab Pre-Load-Hinweis („Verbinde/Lade …"): nur solange weder Geräte geladen noch der Leer-Fall erkannt ist.</summary>
    public bool ShowDeviceLoading => !Editor.IsReady && !ShowNoDevices;

    /// <summary>
    /// Kurven-Tab Pre-Load-Hinweis („Verbinde/Lade …"): wie <see cref="ShowDeviceLoading"/>. Bei null Geräten
    /// bleibt <c>Editor.IsReady</c> absichtlich false; ohne diese Trennung hinge der Ladehinweis sonst ewig fest.
    /// Stattdessen greift dann <see cref="ShowNoDevices"/> (Kurven brauchen Geräte → „Keine Geräte/Kurven").
    /// </summary>
    public bool ShowCurveLoading => !Editor.IsReady && !ShowNoDevices;

    private readonly Func<string, byte, Task>? _sendManual;
    private readonly Func<string, Task>? _sendAuto;
    private readonly Func<string, Task>? _sendCalibrate;
    private readonly Func<Task>? _cancelCalibrate;
    private string _sensorGroupSignature = "";
    private bool _onboardingShown;

    /// <summary>Laufzeit-ctor (die App setzt diesen Controller als DataContext).</summary>
    public MainController() : this(new IpcLiveMonitor(), GuiErrorLog.Default) => _ownsMonitor = true;

    /// <param name="errorLog">
    /// Where caught errors are recorded. No default: a test must never write the user's log, and it should notice an
    /// error <see cref="ApplyGuarded"/> swallowed.
    /// </param>
    /// <param name="clock">Time source of the overview's "as of" stamp while offline (tests pin it).</param>
    public MainController(ILiveMonitor monitor, GuiErrorLog errorLog, ICommandSink? sink = null,
        TimeSpan? pollInterval = null, SettingsController? settings = null, IReconnectable? reconnect = null,
        TimeProvider? clock = null)
    {
        _monitor = monitor;
        _clock = clock ?? TimeProvider.System;
        _errorLog = errorLog;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        // Default: der echte Per-User-Store; Tests reichen einen mit temporärem Pfad herein, damit sie die
        // Einstellungen des Nutzers nicht umschreiben.
        Settings = settings ?? new SettingsController();
        // Like the sink below: the monitor itself if it can reconnect (IpcLiveMonitor).
        Connection = new ConnectionController(reconnect ?? monitor as IReconnectable, _errorLog.FilePath);

        // Steuerbefehle laufen über die Sink-Abstraktion (Daemon/IPC), nicht direkt auf die Hardware.
        // Default: derselbe Monitor, falls er Befehle versteht (IpcLiveMonitor) - Tests injizieren einen Fake.
        _sink = sink ?? monitor as ICommandSink;
        Editor = new CurveEditorController(
            _sink is null ? null : _sink.SendConfigAsync,
            _sink is null ? null : _sink.SendActiveProfileAsync,
            _sink is null ? null : _sink.SendStartCalibrationAsync,
            _sink is null ? null : _sink.SendSetCurveEnabledAsync,
            identify: _sink is null ? null : _sink.SendIdentifyAsync,
            sendManual: _sink is null ? null : _sink.SendManualPwmAsync,
            sendAuto: _sink is null ? null : _sink.SendFanAutoAsync,
            startTachMapping: _sink is null ? null : _sink.SendStartTachMappingAsync,
            cancelTachMapping: _sink is null ? null : _sink.SendCancelTachMappingAsync,
            setFanTachometer: _sink is null ? null : _sink.SendSetFanTachometerAsync,
            cancelCalibration: _sink is null ? null : _sink.SendCancelCalibrationAsync);
        // Links of the airflow page: another settings section, or a profile/curve in the curves tab.
        Editor.SettingsSectionRequested += (_, section) => Settings.OpenSection(section);
        // A fan test belongs to the section it was started in (fans, airflow): another one ends it.
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsController.SelectedSection))
                Editor.RevertAllManual();
        };
        Editor.EditorJumpRequested += (_, jump) => OpenInEditor(jump);
        _sendManual = _sink is null ? null : _sink.SendManualPwmAsync;
        _sendAuto = _sink is null ? null : _sink.SendFanAutoAsync;
        _sendCalibrate = _sink is null ? null : _sink.SendStartCalibrationAsync;
        _cancelCalibrate = _sink is null ? null : _sink.SendCancelCalibrationAsync;

        Backup = new BackupController(
            () => _lastConfig,
            _sink is null ? _ => Task.FromResult(false) : _sink.SendReplaceConfigAsync,
            _sink is null ? () => Task.FromResult(false) : _sink.SendResetConfigAsync,
            Settings,
            ArmConfigResync,
            CancelConfigResync);

        Editor.PropertyChanged += OnEditorPropertyChanged; // the header's profile button names the running profile
        RefreshProfileButton();
        _ = RunAsync(_loopCts.Token);
    }

    /// <summary>
    /// Stößt den einmaligen Update-Check an (von der App nach dem Fenster-Setup aufgerufen, nicht im ctor -
    /// so bleibt die Controller-Konstruktion in Tests netzfrei). Fire-and-forget, an den Loop-Token gekoppelt.
    /// </summary>
    public void BeginUpdateCheck() => _ = Update.CheckAsync(_loopCts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            MonitorSnapshot snapshot;
            try
            {
                snapshot = await Task.Run(_monitor.Read, ct);
            }
            catch (OperationCanceledException)
            {
                break; // Dispose / App-Shutdown
            }
            catch
            {
                if (!await DelayAsync(ct))
                    break;
                continue;
            }

            Dispatcher.UIThread.Post(() => ApplyGuarded(snapshot));
            if (!await DelayAsync(ct))
                break;
        }
    }

    /// <summary>
    /// A snapshot that fails to apply must not end the GUI - the next one may apply fine, and the daemon keeps the fans
    /// on its curves regardless. But the failure may have left a view half updated (an editor half initialized), so it
    /// goes to the GUI log and the user is told.
    /// </summary>
    private void ApplyGuarded(MonitorSnapshot snapshot)
    {
        try
        {
            Apply(snapshot);
        }
        catch (Exception ex)
        {
            ReportCaught("Snapshot", "Applying a snapshot from the service failed - the GUI keeps running", ex);
        }
    }

    /// <summary>
    /// An exception nobody caught on the UI thread - the app's dispatcher handler marks it handled and hands it here:
    /// recorded, and the user told.
    /// </summary>
    public void ReportUnhandled(Exception exception) =>
        ReportCaught("UI thread", "Unhandled exception - the GUI keeps running", exception);

    /// <summary>
    /// An error that needs no notice - the view handled it and told the user itself, or it has no bearing on what the
    /// window shows (a failed task nobody awaited). Recorded only. Any thread.
    /// </summary>
    public void RecordError(string source, string message, Exception exception) =>
        _errorLog.Error(source, message, exception);

    private void ReportCaught(string source, string message, Exception exception)
    {
        if (_errorLog.Error(source, message, exception))
            ErrorNotice.Set(Localizer.Instance["ErrorNotice.Text"], isError: true);
    }

    /// <summary>Wartet das Poll-Intervall ab; liefert <c>false</c>, wenn abgebrochen wurde.</summary>
    private async Task<bool> DelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_pollInterval, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private MonitorSnapshot? _lastFanRowsSnapshot;

    /// <summary>The window went to the tray: a fan test started in the settings ends - nobody sees it any more.</summary>
    public void OnHiddenToTray() => Editor.RevertAllManual();

    /// <summary>
    /// Stoppt den Poll-Loop (vom App-Shutdown bzw. in Tests aufgerufen). Hat der Controller den Monitor
    /// selbst erzeugt, wird auch dessen IPC-Loop beendet (injizierte Fakes/Monitore bleiben unangetastet).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        Editor.RevertAllManual(); // VOR dem Loop-Abbruch: aktive Geräte-Tab-Manuell-Steuerung sauber auf Auto (best effort)
        CancelCalibrationAutoHide();
        Settings.Dispose(); // löst das app-lebenslange Localizer-Abo des Einstellungs-Controllers
        Editor.Dispose();   // same for the editor (its unsaved-changes summary follows the language)
        Connection.Dispose(); // and the banner texts
        Version.Dispose();
        _loopCts.Cancel();
        _loopCts.Dispose();
        if (_ownsMonitor && _monitor is IAsyncDisposable monitor)
            _ = monitor.DisposeAsync(); // Fire-and-forget: beim Shutdown genügt das Signalisieren des Abbruchs
    }

    // The last snapshot from the monitor and the same with „Lüfter N" names - see WithFanNames.
    private MonitorSnapshot? _rawSnapshot;
    private MonitorSnapshot? _namedSnapshot;

    /// <summary>
    /// Gives every fan without an own name its „Lüfter N" (<see cref="DefaultFanNames.ApplyTo"/>) before anything reads the
    /// snapshot. The monitor hands out the same instance until the daemon ticks, and the rows tell a new tick from a repeat
    /// by that identity - so a repeat gets the same renamed instance, not a new one (after a language switch the names
    /// follow with the next tick).
    /// </summary>
    private MonitorSnapshot WithFanNames(MonitorSnapshot raw)
    {
        if (!ReferenceEquals(raw, _rawSnapshot))
        {
            _rawSnapshot = raw;
            _namedSnapshot = DefaultFanNames.ApplyTo(raw);
        }
        return _namedSnapshot!;
    }

    private void Apply(MonitorSnapshot raw)
    {
        bool fresh = !ReferenceEquals(raw, _rawSnapshot); // the monitor hands out the last one until the daemon ticks
        MonitorSnapshot snapshot = WithFanNames(raw);
        ApplyConnectionState(snapshot);
        // Offline the snapshot is empty. The rows keep the last connected state (the overview shows it frozen and
        // dimmed) instead of being blanked - a stale value with its time beats "no sensors detected".
        if (snapshot.Connected)
        {
            _lastConfig = snapshot.Config; // autoritativer Daemon-Stand (inkl. Kalibrierung) für den Backup-Export
            ApplySensorRows(snapshot);
            ApplyFanRows(snapshot);
        }
        ApplyDashboardSummary();
        FeedEditor(snapshot, fresh);
        ApplyOverview(snapshot);
        _hardwareChanges?.Apply(snapshot); // after the overview: its fan rows carry the device-action locks by now
        RefreshProfileButton();
        MaybeShowOnboarding(snapshot);
    }

    private void ApplyConnectionState(MonitorSnapshot snapshot)
    {
        Connected = snapshot.Connected;
        Connection.Apply(snapshot);
        Version.Apply(snapshot);
        // While the offline banner explains the problem, the profile button's tooltip only says that there is one.
        Status = Connection.ShowOfflineBanner ? Connection.StatusText : snapshot.Status;
        Editor.IsConnected = snapshot.Connected; // saving needs the daemon; the editor cannot tell on its own
        Calibration = snapshot.Calibration;
    }

    private void ApplySensorRows(MonitorSnapshot snapshot)
    {
        foreach (SensorReading s in snapshot.Sensors.Where(r => r.Kind == SensorKind.Temperature))
        {
            SensorConfig? sc = snapshot.Config.Sensors.FirstOrDefault(x => x.SensorId == s.Id);
            if (sc?.Hidden == true)
            {
                RemoveRow(_sensorRows, Temperatures, s.Id);
                continue;
            }
            Upsert(_sensorRows, Temperatures, s.Id, () => new SensorRow(s.Id, s.Name, s.Unit), row =>
            {
                row.Update(s.Name, s.Value);
                row.SetGroup(sc?.Group);
            });
        }
    }

    private void ApplyFanRows(MonitorSnapshot snapshot)
    {
        bool fresh = !ReferenceEquals(snapshot, _lastFanRowsSnapshot); // the monitor hands out the last one until the next tick
        _lastFanRowsSnapshot = snapshot;
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (FanReading f in snapshot.Fans)
        {
            FanConfig? cfg = snapshot.Config.Fans.FirstOrDefault(fc => fc.FanId == f.Id);
            if (cfg?.Hidden == true)
                hidden.Add(f.Id);
            if (!_fanRows.TryGetValue(f.Id, out FanRow? row))
            {
                row = CreateFanRow(f.Id, f.Name);
                _fanRows[f.Id] = row;
                _fanOrder.Add(row);
            }
            row.Update(f, fresh);
            row.SetPlacement(cfg?.Location ?? FanLocation.Unspecified);
            row.SetCalibration(cfg?.Calibration);
            row.IsCalibrating = snapshot.Calibration is { Running: true } rc && rc.FanId == f.Id;
        }
        SyncVisibleFans(hidden);
    }

    private void ApplyDashboardSummary()
    {
        HasSensors = Temperatures.Count > 0;
        HasFans = Fans.Count > 0;
        // Pre-Load-Hinweise (Geräte-/Kurven-Tab) an den Verbindungszustand koppeln statt fix.
        DeviceLoadingText = Connected ? Localizer.Instance["MainCtrl.LoadingDevices"] : Localizer.Instance["MainCtrl.ConnectingToService"];
        CurveLoadingText = Connected ? Localizer.Instance["MainCtrl.LoadingCurves"] : Localizer.Instance["MainCtrl.ConnectingToService"];
    }

    private void FeedEditor(MonitorSnapshot snapshot, bool fresh)
    {
        if (!snapshot.Connected)
        {
            // The offline snapshot carries an empty config: feeding it would drop the running curves the overview
            // shows (and, with a resync armed, rebuild the editor from nothing). The live state stays as last seen.
            // A pending verdict cannot wait for the connection: unconfirmed. The rebuild itself stays armed - the
            // daemon may have taken the change before the connection dropped.
            ReportConfigResync(changed: false);
            Editor.UpdateOffline();
            ShowNoDevices = false;
            OnPropertyChanged(nameof(ShowDeviceLoading));
            OnPropertyChanged(nameof(ShowCurveLoading));
            return;
        }

        // Editor einmalig aus dem ersten echten Snapshot befüllen (Sensoren/Lüfter/Zuordnungen) …
        // ABER nicht, solange der Erststart-Assistent noch aussteht: dann wäre der erste Snapshot die leere
        // Vor-Onboarding-Config, und die einmalige Initialize-Sperre würde das spätere Nachladen der im
        // Onboarding gewählten Positionen/Profile dauerhaft verhindern. OnboardingCompleted == false ist exakt
        // das Erststart-Signal (siehe Onboarding-Trigger unten); bei null/true normal initialisieren. Sobald
        // Finish/Skip die Config mit OnboardingCompleted = true (+ Positionen/Profile) sendet und der Daemon sie
        // rebroadcastet, greift dieser Aufruf genau einmal mit den echten Daten.
        // Nach Reset/Import: sobald der Daemon die geänderte Config zurückspiegelt (Signatur ≠ Vorher), den
        // Editor vollständig neu aufbauen - sonst bliebe er stale und ein späteres Speichern schriebe die alte
        // Config zurück. Vor dem Onboarding-Gate, weil Resync selbst Initialize aus der frischen Config fährt.
        if (_awaitingConfigResync && ConfigSignature(snapshot.Config) != _preResyncSignature
            && (_resyncUntil?.Invoke(snapshot) ?? true))
        {
            EndConfigResync(changed: true); // first: a rebuild that throws must not repeat on every tick
            Editor.Resync(snapshot);
        }
        else
        {
            // Only a new tick can bring the change; a repeat of the last snapshot does not use up the grace.
            if (_awaitingConfigResync && fresh && --_resyncGraceTicks <= 0)
                EndConfigResync(changed: false); // the grace ran out (see ResyncGraceTicks)

            if (snapshot.Config.OnboardingCompleted != false)
                Editor.Initialize(snapshot);
        }
        // … und danach pro Tick mit Live-Temperaturen für den Arbeitspunkt im Kurven-Graph speisen.
        Editor.UpdateLive(snapshot);

        // „Keine Geräte erkannt" im Geräte-Tab: verbunden, aber der Daemon meldet weder Sensoren noch Lüfter.
        // (Nicht an Editor.IsReady gekoppelt - der bleibt bei null Geräten absichtlich „nicht bereit".)
        // Während der Erststart-Assistent aussteht ist der Editor bewusst noch nicht befüllt (siehe oben) →
        // dann nicht „keine Geräte" behaupten, sonst blitzt der Leer-Hinweis hinter dem Assistenten auf.
        ShowNoDevices = Connected && snapshot.Config.OnboardingCompleted != false
                        && Editor.Sensors.Count == 0 && Editor.Fans.Count == 0;
        // Editor.IsReady ist eine Fremd-Property → die abgeleiteten Lade-Sichtbarkeiten explizit neu auswerten lassen.
        OnPropertyChanged(nameof(ShowDeviceLoading));
        OnPropertyChanged(nameof(ShowCurveLoading));
    }

    private void MaybeShowOnboarding(MonitorSnapshot snapshot)
    {
        // Onboarding: einmalig anzeigen, wenn der Daemon meldet, dass es noch nicht abgeschlossen ist.
        if (Onboarding is null && !_onboardingShown
            && snapshot.Config.OnboardingCompleted == false
            && _sink is not null)
        {
            _onboardingShown = true;
            Onboarding = new OnboardingController(
                _sink.SendStartCalibrationAsync,
                _sink.SendCancelCalibrationAsync,
                SendOnboardingConfigAsync,
                onClose: () => Onboarding = null,
                sendIdentify: _sink.SendIdentifyAsync,
                sendManual: _sink.SendManualPwmAsync,
                sendAuto: _sink.SendFanAutoAsync,
                sendStartTachMapping: _sink.SendStartTachMappingAsync,
                sendCancelTachMapping: _sink.SendCancelTachMappingAsync);
        }

        Onboarding?.Apply(snapshot);
    }

    /// <summary>
    /// Startet den Onboarding-Assistenten manuell (Einstellungen → Sicherung & Einrichtung). Ohne Daemon-Sink (Tests)
    /// oder bei bereits laufendem Assistenten ein No-op. Das Fenster zeigt <c>MainWindow</c> automatisch,
    /// sobald <see cref="Onboarding"/> ungleich null wird (gleicher Pfad wie der Erststart-Trigger).
    /// </summary>
    [RelayCommand]
    private void StartOnboarding()
    {
        if (_sink is null || Onboarding is not null)
            return;
        Editor.RevertAllManual(); // the assistant drives the fans itself; a test left on in the settings must not linger
        Onboarding = new OnboardingController(
            _sink.SendStartCalibrationAsync,
            _sink.SendCancelCalibrationAsync,
            SendOnboardingConfigAsync,
            onClose: () => Onboarding = null,
            sendIdentify: _sink.SendIdentifyAsync,
            sendManual: _sink.SendManualPwmAsync,
            sendAuto: _sink.SendFanAutoAsync,
            sendStartTachMapping: _sink.SendStartTachMappingAsync,
            sendCancelTachMapping: _sink.SendCancelTachMappingAsync);
    }

    /// <summary>
    /// Remembers the current config and waits for it to change (reset, import, setup assistant), to rebuild the editor
    /// then (<see cref="Apply"/>). Armed before the config goes out, so the baseline is the one from before. The task
    /// tells whether the daemon took it: <c>true</c> once a snapshot reports a changed config, <c>false</c> after
    /// <see cref="ResyncGraceTicks"/> ticks without one, when the connection is lost meanwhile, or when a newer arm
    /// supersedes it (the daemon applies only the latest pending replace). Handed to the
    /// <see cref="BackupController"/> as a callback.
    /// </summary>
    private Task<bool> ArmConfigResync() => ArmConfigResync(until: null);

    /// <param name="until">
    /// What else the snapshot of the rebuild must show - the hardware dialog's change being resolved: a config change that
    /// overtakes the command (a calibration landing) would otherwise rebuild the editor too early and leave it stale.
    /// </param>
    private Task<bool> ArmConfigResync(Func<MonitorSnapshot, bool>? until)
    {
        ReportConfigResync(changed: false);
        // Asynchronous continuations: the verdict is reached inside Apply, and what awaits it (the import's status and UI
        // prefs, a language switch among them) must not run in the middle of it.
        _resyncOutcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _preResyncSignature = ConfigSignature(_lastConfig);
        _awaitingConfigResync = true;
        _resyncUntil = until;
        _resyncGraceTicks = ResyncGraceTicks;
        return _resyncOutcome.Task;
    }

    /// <summary>Nothing went out after all: wait neither for a change nor rebuild on a foreign one later.</summary>
    private void CancelConfigResync() => EndConfigResync(changed: false);

    private void EndConfigResync(bool changed)
    {
        _awaitingConfigResync = false;
        _resyncUntil = null;
        ReportConfigResync(changed);
    }

    private void ReportConfigResync(bool changed)
    {
        TaskCompletionSource<bool>? outcome = _resyncOutcome;
        _resyncOutcome = null;
        outcome?.TrySetResult(changed);
    }

    /// <summary>
    /// Config-Sender des Assistenten: armiert den Editor-Neuaufbau, BEVOR gesendet wird (so ist die
    /// Signatur-Basis garantiert die Config von vor dem Assistenten). Ohne das bliebe ein bereits befüllter
    /// Editor - der Fall „Einstellungen → Sicherung & Einrichtung", also jede Wiederholung auf bestehender Config - auf
    /// dem alten Stand: die im Assistenten gewählten Positionen/Profile wären in den Tabs unsichtbar und das
    /// nächste Speichern schriebe sie stillschweigend wieder weg.
    /// </summary>
    private async Task<bool> SendOnboardingConfigAsync(AppConfig config)
    {
        if (_sink is null)
            return false;

        _ = ArmConfigResync();
        // The assistant never edits a start point: its MinPwm values are the daemon's from when it built the config. As the
        // base they leave the daemon's own in place - also one a calibration set after that.
        var unchanged = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (FanConfig fan in config.Fans)
            unchanged.TryAdd(fan.FanId, fan.MinPwm);
        bool ok = await _sink.SendConfigAsync(config, unchanged);
        if (!ok)
            CancelConfigResync(); // nothing on its way - do not wait for a foreign change
        return ok;
    }

    /// <summary>What counts as a changed config - for the resync and for an import of the running config.</summary>
    internal static string ConfigSignature(AppConfig config) =>
        System.Text.Json.JsonSerializer.Serialize(config);

    private FanRow CreateFanRow(string id, string name)
    {
        var row = new FanRow(id, name);
        row.BindCommands(_sendManual, _sendAuto, _sendCalibrate, EditCurve);
        return row;
    }

    [RelayCommand]
    private Task CancelCalibration() => _cancelCalibrate?.Invoke() ?? Task.CompletedTask;

    /// <summary>
    /// Blendet die Kalibrier-Meldung nach erfolgreichem Abschluss automatisch aus - ein fertiger Lauf
    /// (Done, kein Fehler) braucht keine Bestätigung. Laufende bzw. fehlgeschlagene Läufe bleiben stehen
    /// (der Fehler bis zum manuellen Schließen). Record-Wertgleichheit sorgt dafür, dass dies pro Übergang
    /// genau einmal greift (gleiche Done-Snapshots feuern kein erneutes Changed).
    /// </summary>
    partial void OnCalibrationChanged(CalibrationStatus? value)
    {
        if (value is { Done: true, FailReason: null })
            StartCalibrationAutoHide();
        else
            CancelCalibrationAutoHide(); // laufend / leer / fehlerhaft → kein Auto-Ausblenden
    }

    private void StartCalibrationAutoHide()
    {
        CancelCalibrationAutoHide();
        _calibrationAutoHideCts = new CancellationTokenSource();
        _ = HideCalibrationAfterAsync(_calibrationAutoHideCts);
    }

    private void CancelCalibrationAutoHide()
    {
        _calibrationAutoHideCts?.Cancel();
        _calibrationAutoHideCts = null;
    }

    private async Task HideCalibrationAfterAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(CalibrationAutoHide, cts.Token);
            // Nur ausblenden, wenn dieses Halten noch aktuell ist und der Erfolgs-Status weiterhin ansteht:
            // gleicher Pfad wie der „Schließen"-Knopf → der Daemon räumt den Kalibrier-Status weg.
            if (ReferenceEquals(_calibrationAutoHideCts, cts) && Calibration is { Done: true, FailReason: null })
                await CancelCalibration();
        }
        catch (OperationCanceledException)
        {
            // durch einen neuen Lauf / neueres Halten abgelöst
        }
        finally
        {
            if (ReferenceEquals(_calibrationAutoHideCts, cts))
                _calibrationAutoHideCts = null;
            cts.Dispose();
        }
    }

    private static void RemoveRow<T>(Dictionary<string, T> index, ObservableCollection<T> collection, string id)
    {
        if (index.Remove(id, out T? row))
            collection.Remove(row);
    }

    private static void Upsert<T>(
        Dictionary<string, T> index, ObservableCollection<T> collection,
        string id, Func<T> create, Action<T> update)
    {
        if (!index.TryGetValue(id, out T? row))
        {
            row = create();
            index[id] = row;
            collection.Add(row);
        }
        update(row);
    }
}
