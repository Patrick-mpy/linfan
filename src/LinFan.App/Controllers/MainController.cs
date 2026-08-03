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
    private readonly Dictionary<string, FanRow> _fanRows = new();

    // Auto-Ausblenden des Kalibrier-Banners nach erfolgreichem Abschluss (Fehler bleiben bis manuell geschlossen).
    private CancellationTokenSource? _calibrationAutoHideCts;
    private static readonly TimeSpan CalibrationAutoHide = TimeSpan.FromSeconds(5);

    public ObservableCollection<SensorRow> Temperatures { get; } = new();
    public ObservableCollection<SensorGroup> SensorGroups { get; } = new();
    public ObservableCollection<FanRow> Fans { get; } = new();
    public ObservableCollection<FanGroup> FanGroups { get; } = new();

    /// <summary>Aktive Kurven fürs Dashboard-Panel (Kurven, die mindestens einen sichtbaren Lüfter regeln).</summary>
    public ObservableCollection<DashboardCurveRow> ActiveCurves { get; } = new();

    public CurveEditorController Editor { get; }

    /// <summary>GUI-lokale Einstellungen (Theme, Tray) — als ein DataContext über das Hauptfenster gebunden.</summary>
    public SettingsController Settings { get; } = new();

    /// <summary>Sicherung/Wiederherstellung/Reset im Einstellungen-Tab (Export/Import/Reset via IPC).</summary>
    public BackupController Backup { get; }

    /// <summary>Update-Hinweis (GitHub-Release-Check + dismissierbares Banner); der Check startet via <see cref="BeginUpdateCheck"/>.</summary>
    public UpdateController Update { get; } = new();

    // Zuletzt vom Daemon empfangene Config (autoritativ, inkl. Kalibrierung) — Quelle für den Backup-Export.
    private AppConfig _lastConfig = AppConfig.Empty;

    // Nach Reset/Import: den Editor neu aufbauen, sobald der Daemon die geänderte Config zurückspiegelt.
    // Auf die Signatur-Änderung warten (nicht schon beim Absenden), damit der Neuaufbau die NEUE Config trifft.
    private bool _awaitingConfigResync;
    private string _preResyncSignature = "";

    [ObservableProperty] private string _status = Localizer.Instance["MainCtrl.Connecting"];
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private CalibrationStatus? _calibration;
    [ObservableProperty] private OnboardingController? _onboarding;

    /// <summary>Aktiver Tab (0 = Übersicht, 1 = Kurven &amp; Zuordnung, 2 = Einstellungen) — für „Kurve bearbeiten" vom Dashboard.</summary>
    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>Dashboard-Karten ein-/ausklappen — reiner View-Zustand, In-Memory (nicht persistiert).</summary>
    [ObservableProperty] private bool _temperaturesExpanded = true;
    [ObservableProperty] private bool _fansExpanded = true;
    [ObservableProperty] private bool _activeCurvesExpanded = true;

    /// <summary>True, wenn mindestens eine Kurve einen sichtbaren Lüfter regelt — steuert die Sichtbarkeit des Panels.</summary>
    [ObservableProperty] private bool _hasActiveCurves;

    /// <summary>Kurzinfo im eingeklappten Temperatur-Header: der wärmste Sensor (z. B. „max 48.0 °C"); leer ohne Messwert.</summary>
    [ObservableProperty] private string _maxTempDisplay = "";

    /// <summary>True, sobald der erste Snapshot verarbeitet wurde — vorher zeigt das Dashboard keinen Leer-Hinweis.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSensors), nameof(ShowNoFans))]
    private bool _hasSnapshot;

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

    /// <summary>Dashboard-Karte „Temperaturen": Leer-Platzhalter nur, wenn geladen UND keine Sensoren da sind.</summary>
    public bool ShowNoSensors => HasSnapshot && !HasSensors;

    /// <summary>Dashboard-Karte „Lüfter": Leer-Platzhalter nur, wenn geladen UND keine Lüfter da sind.</summary>
    public bool ShowNoFans => HasSnapshot && !HasFans;

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
    private string _fanGroupSignature = "";
    private string _sensorGroupSignature = "";
    private string _activeCurvesSignature = "";
    private bool _onboardingShown;

    /// <summary>Laufzeit-ctor (die App setzt diesen Controller als DataContext).</summary>
    public MainController() : this(new IpcLiveMonitor()) => _ownsMonitor = true;

    public MainController(ILiveMonitor monitor, ICommandSink? sink = null, TimeSpan? pollInterval = null)
    {
        _monitor = monitor;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);

        // Steuerbefehle laufen über die Sink-Abstraktion (Daemon/IPC), nicht direkt auf die Hardware.
        // Default: derselbe Monitor, falls er Befehle versteht (IpcLiveMonitor) — Tests injizieren einen Fake.
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
            setFanTachometer: _sink is null ? null : _sink.SendSetFanTachometerAsync);
        _sendManual = _sink is null ? null : _sink.SendManualPwmAsync;
        _sendAuto = _sink is null ? null : _sink.SendFanAutoAsync;
        _sendCalibrate = _sink is null ? null : _sink.SendStartCalibrationAsync;
        _cancelCalibrate = _sink is null ? null : _sink.SendCancelCalibrationAsync;

        Backup = new BackupController(
            () => _lastConfig,
            _sink is null ? _ => Task.FromResult(false) : _sink.SendReplaceConfigAsync,
            _sink is null ? () => Task.FromResult(false) : _sink.SendResetConfigAsync,
            Settings,
            onConfigReplaced: ArmConfigResync);

        _ = RunAsync(_loopCts.Token);
    }

    /// <summary>
    /// Stößt den einmaligen Update-Check an (von der App nach dem Fenster-Setup aufgerufen, nicht im ctor —
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

            Dispatcher.UIThread.Post(() => Apply(snapshot));
            if (!await DelayAsync(ct))
                break;
        }
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
        _loopCts.Cancel();
        _loopCts.Dispose();
        if (_ownsMonitor && _monitor is IAsyncDisposable monitor)
            _ = monitor.DisposeAsync(); // Fire-and-forget: beim Shutdown genügt das Signalisieren des Abbruchs
    }

    private void Apply(MonitorSnapshot snapshot)
    {
        ApplyConnectionState(snapshot);
        ApplySensorRows(snapshot);
        ApplyFanRows(snapshot);
        ApplyDashboardSummary(snapshot);
        FeedEditor(snapshot);
        MaybeShowOnboarding(snapshot);
    }

    private void ApplyConnectionState(MonitorSnapshot snapshot)
    {
        Status = snapshot.Status;
        Connected = snapshot.Connected;
        Calibration = snapshot.Calibration;
        _lastConfig = snapshot.Config; // autoritativer Daemon-Stand (inkl. Kalibrierung) für den Backup-Export
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
        RebuildSensorGroupsIfChanged();
    }

    private void ApplyFanRows(MonitorSnapshot snapshot)
    {
        foreach (FanReading f in snapshot.Fans)
        {
            FanConfig? cfg = snapshot.Config.Fans.FirstOrDefault(fc => fc.FanId == f.Id);
            if (cfg?.Hidden == true)
            {
                RemoveRow(_fanRows, Fans, f.Id);
                continue;
            }
            Upsert(_fanRows, Fans, f.Id, () => CreateFanRow(f.Id, f.Name), row =>
            {
                row.Update(f);
                row.SetPlacement(cfg?.Location ?? FanLocation.Unspecified, cfg?.Group);
                row.SetCalibration(cfg?.Calibration);
                row.IsCalibrating = snapshot.Calibration is { Running: true } rc && rc.FanId == f.Id;
            });
        }
        RebuildFanGroupsIfChanged();
    }

    private void ApplyDashboardSummary(MonitorSnapshot snapshot)
    {
        // Dashboard-Leer-Hinweise: nach dem ersten Snapshot zeigen, je nach Inhalt der Dashboard-Collections.
        HasSnapshot = true;
        HasSensors = SensorGroups.Count > 0;
        HasFans = FanGroups.Count > 0;

        // Kurzinfo für den eingeklappten Temperatur-Header: wärmster Sensor mit Messwert.
        SensorRow? hottest = Temperatures
            .Where(r => !double.IsNaN(r.Value))
            .OrderByDescending(r => r.Value)
            .FirstOrDefault();
        MaxTempDisplay = hottest is null ? "" : Localizer.Instance.Format("MainCtrl.MaxTemp", hottest.Display);
        // Pre-Load-Hinweise (Geräte-/Kurven-Tab) an den Verbindungszustand koppeln statt fix.
        DeviceLoadingText = Connected ? Localizer.Instance["MainCtrl.LoadingDevices"] : Localizer.Instance["MainCtrl.ConnectingToService"];
        CurveLoadingText = Connected ? Localizer.Instance["MainCtrl.LoadingCurves"] : Localizer.Instance["MainCtrl.ConnectingToService"];
    }

    private void FeedEditor(MonitorSnapshot snapshot)
    {
        // Editor einmalig aus dem ersten echten Snapshot befüllen (Sensoren/Lüfter/Zuordnungen) …
        // ABER nicht, solange der Erststart-Assistent noch aussteht: dann wäre der erste Snapshot die leere
        // Vor-Onboarding-Config, und die einmalige Initialize-Sperre würde das spätere Nachladen der im
        // Onboarding gewählten Positionen/Profile dauerhaft verhindern. OnboardingCompleted == false ist exakt
        // das Erststart-Signal (siehe Onboarding-Trigger unten); bei null/true normal initialisieren. Sobald
        // Finish/Skip die Config mit OnboardingCompleted = true (+ Positionen/Profile) sendet und der Daemon sie
        // rebroadcastet, greift dieser Aufruf genau einmal mit den echten Daten.
        // Nach Reset/Import: sobald der Daemon die geänderte Config zurückspiegelt (Signatur ≠ Vorher), den
        // Editor vollständig neu aufbauen — sonst bliebe er stale und ein späteres Speichern schriebe die alte
        // Config zurück. Vor dem Onboarding-Gate, weil Resync selbst Initialize aus der frischen Config fährt.
        if (_awaitingConfigResync && ConfigSignature(snapshot.Config) != _preResyncSignature)
        {
            _awaitingConfigResync = false;
            Editor.Resync(snapshot);
        }
        else if (snapshot.Config.OnboardingCompleted != false)
        {
            Editor.Initialize(snapshot);
        }
        // … und danach pro Tick mit Live-Temperaturen für den Arbeitspunkt im Kurven-Graph speisen.
        Editor.UpdateLive(snapshot);

        // Dashboard-Panel „Aktive Kurven": aus den (live aktualisierten) Editor-Kurven + Zuordnungen ableiten.
        RebuildActiveCurvesIfChanged();

        // „Keine Geräte erkannt" im Geräte-Tab: verbunden, aber der Daemon meldet weder Sensoren noch Lüfter.
        // (Nicht an Editor.IsReady gekoppelt — der bleibt bei null Geräten absichtlich „nicht bereit".)
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
                _sink.SendConfigAsync,
                onClose: () => Onboarding = null,
                sendIdentify: _sink.SendIdentifyAsync,
                sendManual: _sink.SendManualPwmAsync,
                sendAuto: _sink.SendFanAutoAsync,
                sendStartTachMapping: _sink.SendStartTachMappingAsync,
                sendCancelTachMapping: _sink.SendCancelTachMappingAsync);
        }

        Onboarding?.Apply(snapshot);
    }

    /// <summary>Gruppiert die Lüfter nach <see cref="FanRow.GroupKey"/> — nur neu, wenn sich die Zugehörigkeit ändert.</summary>
    private void RebuildFanGroupsIfChanged()
    {
        // Wechsel-Gate zuerst, über die Sammlung in ihrer stabilen Einfügereihenfolge (Upsert hängt an, Remove
        // entfernt — ohne Mitgliederwechsel keine Umordnung). Damit fällt die teure OrderBy/ThenBy-Sortierung pro
        // Live-Tick weg; die Signatur ist eine Funktion derselben (GroupKey,Name)-Menge → dasselbe Gate wie zuvor.
        string signature = string.Join("|", Fans.Select(f => $"{f.GroupKey}␟{f.Name}"));
        if (signature == _fanGroupSignature)
            return;
        _fanGroupSignature = signature;

        List<FanRow> ordered = Fans
            .OrderBy(f => f.GroupKey == FanGroup.Ungrouped ? "￿" : f.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FanGroups.Clear();
        foreach (IGrouping<string, FanRow> grp in ordered.GroupBy(f => f.GroupKey))
        {
            var group = new FanGroup(grp.Key);
            foreach (FanRow fan in grp)
                group.Fans.Add(fan);
            FanGroups.Add(group);
        }
    }

    /// <summary>Gruppiert die Sensoren nach <see cref="SensorRow.GroupKey"/> — nur neu bei Zugehörigkeitsänderung.</summary>
    private void RebuildSensorGroupsIfChanged()
    {
        // Wechsel-Gate zuerst, über die Sammlung in Einfügereihenfolge (wie bei den Lüftern) → OrderBy/ThenBy nur
        // beim tatsächlichen Wechsel, nicht pro Live-Tick. Dieselbe (GroupKey,Name)-Menge, also dasselbe Gate.
        string signature = string.Join("|", Temperatures.Select(s => $"{s.GroupKey}␟{s.Name}"));
        if (signature == _sensorGroupSignature)
            return;
        _sensorGroupSignature = signature;

        List<SensorRow> ordered = Temperatures
            .OrderBy(s => s.GroupKey == SensorGroup.Ungrouped ? "￿" : s.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Doppelte Anzeigenamen unterscheidbar machen (z. B. zwei „amdgpu edge"): nur bei Kollision
        // die Hardware-Id als dezenten Zusatz zeigen. Folgt der Signatur → nur bei Namens-/Gruppenwechsel.
        HashSet<string> duplicateNames = ordered
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SensorRow s in ordered)
            s.Disambiguator = duplicateNames.Contains(s.Name) ? s.Id : "";

        SensorGroups.Clear();
        foreach (IGrouping<string, SensorRow> grp in ordered.GroupBy(s => s.GroupKey))
        {
            var group = new SensorGroup(grp.Key);
            foreach (SensorRow sensor in grp)
                group.Sensors.Add(sensor);
            SensorGroups.Add(group);
        }
    }

    /// <summary>
    /// Baut das Dashboard-Panel „Aktive Kurven" neu, wenn sich Kurven, Zuordnungen oder Quellen ändern
    /// (Signatur-Vergleich, wie bei den Gruppen). Aktiv = die Kurve regelt mindestens einen <b>sichtbaren</b>
    /// Lüfter (versteckte sind aus dem Dashboard ausgeblendet). Jede Zeile referenziert Live-Objekte
    /// (Kurve, Lüfter-Zeilen) → Arbeitstemperatur/PWM folgen per Binding ohne Neuaufbau.
    /// </summary>
    private void RebuildActiveCurvesIfChanged()
    {
        var rows = new List<(CurveEditRow Curve, List<FanRow> Fans)>();
        foreach (CurveEditRow curve in Editor.Curves)
        {
            List<FanRow> fans = Editor.Fans
                .Where(fa => ReferenceEquals(fa.Selected, curve))
                .Select(fa => _fanRows.GetValueOrDefault(fa.FanId))
                .OfType<FanRow>()
                .ToList();
            if (fans.Count > 0)
                rows.Add((curve, fans));
        }

        string signature = string.Join("|", rows.Select(r =>
            $"{r.Curve.Id}␟{string.Join(",", r.Fans.Select(f => f.FanId))}␟{string.Join(",", r.Curve.Sources.Select(s => s.Id))}"));
        if (signature == _activeCurvesSignature)
            return;
        _activeCurvesSignature = signature;

        ActiveCurves.Clear();
        foreach (var (curve, fans) in rows)
            ActiveCurves.Add(new DashboardCurveRow(
                curve, fans,
                enabled => Editor.SetCurveEnabled(curve, enabled),
                () => EditCurve(curve)));
        HasActiveCurves = ActiveCurves.Count > 0;
    }

    /// <summary>„Kurve bearbeiten" vom Dashboard: die Kurve im Editor auswählen und in den Kurven-Tab wechseln.</summary>
    private void EditCurve(CurveEditRow curve)
    {
        Editor.SelectedCurve = curve;
        SelectedTabIndex = 1; // Kurven & Zuordnung (nach Entfernen des Geräte-Tabs: Index 1)
    }

    /// <summary>
    /// Startet den Onboarding-Assistenten manuell (Einstellungen → Onboarding). Ohne Daemon-Sink (Tests)
    /// oder bei bereits laufendem Assistenten ein No-op. Das Fenster zeigt <c>MainWindow</c> automatisch,
    /// sobald <see cref="Onboarding"/> ungleich null wird (gleicher Pfad wie der Erststart-Trigger).
    /// </summary>
    [RelayCommand]
    private void StartOnboarding()
    {
        if (_sink is null || Onboarding is not null)
            return;
        Onboarding = new OnboardingController(
            _sink.SendStartCalibrationAsync,
            _sink.SendCancelCalibrationAsync,
            _sink.SendConfigAsync,
            onClose: () => Onboarding = null,
            sendIdentify: _sink.SendIdentifyAsync,
            sendManual: _sink.SendManualPwmAsync,
            sendAuto: _sink.SendFanAutoAsync);
    }

    /// <summary>
    /// Merkt den aktuellen Config-Stand und wartet auf dessen Änderung (Reset/Import), um dann den Editor
    /// neu aufzubauen (<see cref="Apply"/>). Wird als Callback an den <see cref="BackupController"/> gereicht.
    /// </summary>
    private void ArmConfigResync()
    {
        _preResyncSignature = ConfigSignature(_lastConfig);
        _awaitingConfigResync = true;
    }

    private static string ConfigSignature(AppConfig config) =>
        System.Text.Json.JsonSerializer.Serialize(config);

    private FanRow CreateFanRow(string id, string name)
    {
        var row = new FanRow(id, name);
        row.BindCommands(_sendManual, _sendAuto, _sendCalibrate);
        return row;
    }

    [RelayCommand]
    private Task CancelCalibration() => _cancelCalibrate?.Invoke() ?? Task.CompletedTask;

    /// <summary>
    /// Blendet die Kalibrier-Meldung nach erfolgreichem Abschluss automatisch aus — ein fertiger Lauf
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
