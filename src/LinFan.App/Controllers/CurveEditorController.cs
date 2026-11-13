// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Controller des Kurven-Editors. Ein <b>Profil</b> ist ein komplettes Setup (eigene Kurven +
/// Lüfter-Zuordnungen); beim Profilwechsel werden die Kurven der Editor-Collection neu geladen und im
/// Daemon aktiviert. Die Zuordnung ist <b>kurvenzentriert</b>: für die ausgewählte Kurve kreuzt man die
/// Lüfter an. „Speichern" schickt die Konfiguration über IPC an den Daemon (alleiniger Schreiber).
/// </summary>
public partial class CurveEditorController : ObservableObject, IDisposable
{
    private readonly Func<AppConfig, IReadOnlyDictionary<string, byte>, Task<bool>>? _save;
    private readonly Func<string, Task>? _activateProfile;
    private readonly Func<string, Task>? _calibrate;
    private readonly Func<string, Task>? _identify;
    private readonly Func<string, byte, Task>? _sendManual;
    private readonly Func<string, Task>? _sendAuto;
    private readonly Func<string, bool, Task>? _setCurveEnabled;
    private readonly Func<string, Task>? _startTachMapping;
    private readonly Func<Task>? _cancelTachMapping;
    private readonly Func<Task>? _cancelCalibration;
    private readonly Func<string, string?, Task>? _setFanTachometer;
    private bool _initialized;
    private bool _applyingProfile; // unterdrückt das Nachladen beim programmatischen Setzen der Auswahl
    private bool _applyingActiveProfile; // dito für das aktive Profil (kein IPC beim Laden/Verwerfen)
    private string _liveCurvesSignature = ""; // Fingerabdruck der zuletzt aus dem Snapshot gebauten Live-Kurven
    private string? _savedConfigJson; // Baseline (zuletzt geladen/gespeichert) für die Dirty-Erkennung
    private AppConfig? _savedConfig;  // the same baseline as an object (lazy, for the diff) - see SavedConfig
    private bool _dirtyCheckDeferred; // eine im dirty-Zustand koaleszierte Prüfung wartet auf den nächsten Live-Tick
    private readonly EditHistory _history;
    private readonly TimeProvider _clock;
    private int _nonEditDepth; // > 0 while a rebuild or live update runs - see WithoutHistory
    private ConfigDiff _diff = ConfigDiff.None; // last comparison with the baseline - source of the markers and DirtySummary

    // Edit-getriebene Dirty-Erkennung: an die ConfigChanged/PropertyChanged der dynamischen Zeilen gekoppelt
    // (Curves/Profiles ändern ihre Membership) → Subscriptions mitführen, um beim Entfernen/Reset abzumelden.
    private readonly HashSet<CurveEditRow> _subscribedCurves = new();
    private readonly HashSet<ProfileRow> _subscribedProfiles = new();

    public ObservableCollection<SensorOption> Sensors { get; } = new();

    /// <summary>
    /// The configured sensor group names in order (<see cref="AppConfig.SensorGroups"/>), held as editor state: loaded
    /// with the config, restored by <see cref="RestoreFrom"/> (discard all, undo, redo) and written back by every save - <see cref="BuildConfig"/>
    /// builds a fresh config, which would otherwise send an empty list and delete the groups.
    /// </summary>
    public IReadOnlyList<string> SensorGroups { get; private set; } = Array.Empty<string>();

    public ObservableCollection<SensorOption> VisibleSensors { get; } = new();
    public ObservableCollection<CurveEditRow> Curves { get; } = new();
    public ObservableCollection<FanAssignRow> Fans { get; } = new();
    public ObservableCollection<ProfileRow> Profiles { get; } = new();

    /// <summary>
    /// The curves the daemon is regulating with right now, rebuilt from the snapshot whenever the saved
    /// configuration changes. Deliberately its own set and not <see cref="Curves"/>: the editor may be
    /// showing a different profile entirely, and even for the active one its rows carry unsaved edits that
    /// are not running yet. The dashboard binds to these, so it never claims something is live that is not.
    /// </summary>
    public ObservableCollection<CurveEditRow> LiveCurves { get; } = new();

    /// <summary>
    /// Counts the rebuilds of <see cref="LiveCurves"/>. Consumers that cache rows (the dashboard panel)
    /// cannot tell a replaced set from an unchanged one by content alone - an edited curve keeps its id -
    /// and would keep binding to discarded rows, which no longer receive live values.
    /// </summary>
    public int LiveCurvesRevision { get; private set; }

    /// <summary>
    /// Im Geräte-Tab angezeigte Teilmenge der Sensoren - gefiltert über <see cref="SensorSearch"/> und
    /// <see cref="ShowHiddenSensors"/>. Discovery-Reihenfolge bleibt erhalten (kein Live-Umsortieren beim
    /// Tippen). Nicht zu verwechseln mit <see cref="VisibleSensors"/> (Quell-Sensoren für Kurven).
    /// </summary>
    public ObservableCollection<SensorOption> FilteredSensors { get; } = new();

    /// <summary>Im Geräte-Tab angezeigte Teilmenge der Lüfter - gefiltert über <see cref="FanSearch"/> und
    /// <see cref="ShowHiddenFans"/>. Discovery-Reihenfolge bleibt erhalten.</summary>
    public ObservableCollection<FanAssignRow> FilteredFans { get; } = new();

    /// <summary>Lüfter-Checkboxen für die aktuell ausgewählte Kurve (neu aufgebaut bei Kurvenwechsel).</summary>
    public ObservableCollection<FanCurveCheck> SelectedCurveFans { get; } = new();

    /// <summary>Dieselben Lüfter-Checkboxen, nach Position/Gruppe gebündelt (wie das Dashboard) - Bindungsziel der gruppierten Anzeige.</summary>
    public ObservableCollection<FanCheckGroup> SelectedCurveFanGroups { get; } = new();

    /// <summary>
    /// Verfügbare Drehzahl-Sensoren für die Tacho-Auswahl je Lüfterzeile (geteilte Instanz aller Zeilen).
    /// Erster Eintrag ist immer der „Automatisch"-Eintrag (<see cref="TachSensorOption.Id"/> == <c>null</c>), der die
    /// eigene Zuordnung löscht; danach jeder RPM-Sensor aus der Discovery, mit seinem Live-Wert.
    /// </summary>
    public ObservableCollection<TachSensorOption> AvailableTachSensors { get; } = new();

    [ObservableProperty] private CurveEditRow? _selectedCurve;

    /// <summary>The profile the editor is showing - selecting one does not activate it (see <see cref="ActiveProfile"/>).</summary>
    [ObservableProperty] private ProfileRow? _selectedProfile;

    /// <summary>
    /// The profile the daemon regulates with. Split from <see cref="SelectedProfile"/> on purpose: while the
    /// two were one property, opening a profile in the editor switched the running configuration, so a
    /// profile could never be prepared before it took over the fans.
    /// </summary>
    [ObservableProperty] private ProfileRow? _activeProfile;

    /// <summary>Save/revert result message, rendered as the transient editor status toast.</summary>
    public TransientStatus Status { get; }

    // Geräte-Tab-Filter: Suchtext + „Versteckte ausblenden" je Liste. Änderungen bauen die jeweilige
    // gefilterte Sicht neu auf (Name-Bearbeitung triggert bewusst keinen Rebuild → kein Fokusverlust beim Tippen).
    [ObservableProperty] private string _sensorSearch = "";
    [ObservableProperty] private string _fanSearch = "";
    /// <summary>Hidden sensors still show in the settings list (to edit or show them again) - on by default.</summary>
    [ObservableProperty] private bool _showHiddenSensors = true;

    /// <summary>Hidden fans still show in the settings list - on by default.</summary>
    [ObservableProperty] private bool _showHiddenFans = true;

    /// <summary>
    /// Welche Seite der Kurven-Tab rechts zeigt. Wird ausschließlich durch Klicks im Seitenmenü und die
    /// Anlege-/Sprung-Befehle gesetzt - bewusst nicht an die Auswahl gekoppelt: das Laden eines Profils
    /// setzt auch die Kurven-Auswahl, würde also sofort wieder auf den Kurven-Editor umschalten.
    /// </summary>
    [ObservableProperty] private CurveTabPane _pane = CurveTabPane.Curve;

    /// <summary>True, sobald sich der Editor-Stand von der zuletzt gespeicherten/geladenen Konfiguration unterscheidet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirtySummary), nameof(UnsavedBarText))]
    private bool _hasUnsavedChanges;

    /// <summary>Unsaved changes in curves or profile metadata - the dot on the curves tab.</summary>
    [ObservableProperty] private bool _curvesAreaDirty;

    /// <summary>Unsaved changes in fans, sensors or sensor groups - the dot on the settings tab.</summary>
    [ObservableProperty] private bool _settingsAreaDirty;

    /// <summary>
    /// What is unsaved, e.g. „Geändert: 1 Kurve, Lüfter" - empty while nothing is. Built from the last diff on every
    /// read, so it follows a language switch without a new comparison.
    /// </summary>
    public string DirtySummary => HasUnsavedChanges ? Summarize(_diff) : "";

    /// <summary>The unsaved-changes bar's line: the summary, and that the service does not have it yet.</summary>
    public string UnsavedBarText => HasUnsavedChanges ? Localizer.Instance.Format("UnsavedBar.Detail", DirtySummary) : "";

    /// <summary>
    /// Whether the daemon is reachable - fed by the <see cref="MainController"/> from every snapshot. Saving needs
    /// it (the command would only fail); undo does not, it only changes the draft.
    /// </summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>True, sobald der erste Snapshot verarbeitet wurde (Geräte/Kurven geladen). Steuert Leer-/Lade-Hinweise.</summary>
    [ObservableProperty] private bool _isReady;

    /// <param name="save">Schickt die bearbeitete Konfiguration an den Daemon; liefert den Erfolg zurück. The second
    /// argument is each fan's MinPwm as the daemon reported it last (<see cref="ICommandSink.SendConfigAsync"/>).</param>
    /// <param name="activateProfile">Aktiviert ein Profil live im Daemon (optional).</param>
    /// <param name="calibrate">Startet die Kalibrierung eines Lüfters im Daemon (optional).</param>
    /// <param name="setCurveEnabled">Schaltet eine Kurve live an/aus im Daemon (optional).</param>
    /// <param name="statusAutoHide">Wie lange eine Auto-Hide-Statusmeldung sichtbar bleibt (Default 4 s; injizierbar für Tests).</param>
    /// <param name="identify">Identifiziert einen Lüfter im Daemon (kurz auf 100 %, andere gedrosselt; optional).</param>
    /// <param name="sendManual">Setzt manuellen PWM (temporäre Steuerung im Geräte-Tab/Positions-Modal; optional).</param>
    /// <param name="sendAuto">Beendet die manuelle Steuerung → zurück auf Kurve/Hardware-Auto (optional).</param>
    /// <param name="startTachMapping">Startet die automatische Tacho-Kopplung eines Lüfters im Daemon (optional).</param>
    /// <param name="cancelTachMapping">Bricht eine laufende Tacho-Kopplung ab (optional).</param>
    /// <param name="setFanTachometer">Ordnet einem Lüfter fest einen Drehzahl-Sensor zu (leer/null ⇒ löschen; optional).</param>
    /// <param name="clock">Time source of the undo history's coalescing and the status toast's auto-hide (injectable for tests).</param>
    public CurveEditorController(
        Func<AppConfig, IReadOnlyDictionary<string, byte>, Task<bool>>? save = null, Func<string, Task>? activateProfile = null,
        Func<string, Task>? calibrate = null, Func<string, bool, Task>? setCurveEnabled = null,
        TimeSpan? statusAutoHide = null, Func<string, Task>? identify = null,
        Func<string, byte, Task>? sendManual = null, Func<string, Task>? sendAuto = null,
        Func<string, Task>? startTachMapping = null, Func<Task>? cancelTachMapping = null,
        Func<string, string?, Task>? setFanTachometer = null,
        TimeProvider? clock = null, Func<Task>? cancelCalibration = null)
    {
        _save = save;
        _activateProfile = activateProfile;
        _calibrate = calibrate;
        _identify = identify;
        _sendManual = sendManual;
        _sendAuto = sendAuto;
        _setCurveEnabled = setCurveEnabled;
        _startTachMapping = startTachMapping;
        _cancelTachMapping = cancelTachMapping;
        _cancelCalibration = cancelCalibration;
        _setFanTachometer = setFanTachometer;
        _clock = clock ?? TimeProvider.System;
        Status = new TransientStatus(statusAutoHide, _clock);
        _history = new EditHistory(_clock);
        _history.Changed += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };

        // Named handler, removed in Dispose: the localizer is an app-lifetime singleton and would keep every
        // editor alive otherwise (same as SettingsController).
        Localizer.Instance.PropertyChanged += OnLanguageChanged;

        // Im Konstruktor verdrahten, damit auch die während Initialize hinzugefügten Zeilen abonniert werden
        // (Dirty bleibt dort No-op, bis Initialize die Baseline setzt). Sensoren/Lüfter ändern ihre Membership
        // nicht → die werden in Initialize einmalig direkt abonniert (siehe On*RowChanged).
        Curves.CollectionChanged += OnCurvesChanged;
        Profiles.CollectionChanged += OnProfilesChanged;
    }

    /// <summary>Befüllt Sensoren, Profile, Lüfter und die Kurven des aktiven Profils (einmalig).</summary>
    public void Initialize(MonitorSnapshot snapshot)
    {
        if (_initialized || (snapshot.Sensors.Count == 0 && snapshot.Fans.Count == 0))
            return;
        _initialized = true;

        foreach (SensorReading s in snapshot.Sensors.Where(r => r.Kind == SensorKind.Temperature))
        {
            SensorConfig? sc = snapshot.Config.Sensors.FirstOrDefault(x => x.SensorId == s.Id);
            var opt = new SensorOption(s.Id, s.Name, visible: sc?.Hidden != true, group: sc?.Group, unit: s.Unit,
                                       hardwareLabel: s.Label);
            // Lebensdauer von SensorOption == Lebensdauer des Controllers (Initialize ist einmalig,
            // die Sensors-Collection wird nie neu aufgebaut) → kein Unsubscribe nötig.
            opt.PropertyChanged += OnSensorRowChanged;
            Sensors.Add(opt);
            if (opt.Visible)
                VisibleSensors.Add(opt);
        }
        SensorGroups = snapshot.Config.SensorGroups;
        CaseType = snapshot.Config.CaseType;

        // Drehzahl-Sensoren für die Tacho-Auswahl sammeln (geteilte Liste; „Automatisch"-Eintrag zuerst).
        AvailableTachSensors.Add(TachSensorOption.Automatic());
        foreach (SensorReading rpm in snapshot.Sensors.Where(r => r.Kind == SensorKind.FanRpm))
            AvailableTachSensors.Add(new TachSensorOption(rpm.Id, rpm.Name));

        foreach (FanReading f in snapshot.Fans)
        {
            // A fan the config does not list has no own name - its row shows „Lüfter N" (RefreshDefaultFanNames).
            FanConfig baseFan = snapshot.Config.Fans.FirstOrDefault(fc => fc.FanId == f.Id)
                ?? new FanConfig { FanId = f.Id };
            var fan = new FanAssignRow(baseFan, selected: null, Curves, f.CanControl, _calibrate,
                                       sendIdentify: _identify,
                                       sendManual: _sendManual, sendAuto: _sendAuto,
                                       sendTachMapping: _startTachMapping, cancelTachMapping: _cancelTachMapping,
                                       sendSetTach: _setFanTachometer, availableTachSensors: AvailableTachSensors,
                                       cancelCalibration: _cancelCalibration,
                                       readOnlyReason: f.ReadOnlyReason)
            {
                TachOwner = TachOwnerOf,
                TachAutoOwner = TachAutoOwnerOf,
            };
            // Zuordnungswechsel (Selected) muss das Aktiv-Badge der Kurven neu auswerten. Lebensdauer der
            // FanAssignRow == Lebensdauer des Controllers (Initialize ist einmalig) → kein Unsubscribe nötig.
            fan.PropertyChanged += OnFanRowChanged;
            Fans.Add(fan);
        }

        var rowIds = Fans.Select(f => f.FanId).ToHashSet(StringComparer.Ordinal);
        _fanNamesWithoutRow = snapshot.Config.Fans.Where(fc => !rowIds.Contains(fc.FanId)).Select(fc => fc.Name).ToList();
        RefreshDefaultFanNames();

        RebuildFilteredSensors(); // settings lists and their sections, initially unfiltered
        RebuildFilteredFans();
        NotifyHiddenSensors();
        NotifyHiddenFans();
        RefreshPositionMenus();
        RefreshDeviceActionsBlocked();

        foreach (Profile p in snapshot.Config.Profiles)
            Profiles.Add(RowOf(p));

        // Der Editor startet auf dem aktiven Profil - beides zeigt anfangs dasselbe, ist aber ab hier
        // getrennt: die Auswahl folgt dem Nutzer, das aktive Profil nur einer ausdrücklichen Aktivierung.
        ProfileRow? active = Profiles.FirstOrDefault(p => p.Id == snapshot.Config.ActiveProfileId)
                             ?? Profiles.FirstOrDefault();
        _applyingActiveProfile = true;
        ActiveProfile = active;
        _applyingActiveProfile = false;
        _applyingProfile = true;
        SelectedProfile = active;
        _applyingProfile = false;

        if (active is not null)
        {
            ApplyProfileToEditor(active);
        }
        else
        {
            // Fallback ohne Profile (dank Daemon-Migration selten): Kurven direkt aus der Config.
            _undetectedAssignments = []; // a resync must not carry them over from the config before
            foreach (CurveConfig c in snapshot.Config.Curves)
                Curves.Add(CurveEditRow.From(c, Sensors, Fans));
            foreach (FanAssignRow fan in Fans)
            {
                string? assigned = snapshot.Config.Fans.FirstOrDefault(x => x.FanId == fan.FanId)?.AssignedCurveId;
                fan.Selected = Curves.FirstOrDefault(c => c.Id == assigned);
            }
            SelectedCurve = Curves.FirstOrDefault();
            RebuildSelectedCurveFans();
        }

        // Die laufenden Kurven schon hier spiegeln, nicht erst beim ersten Live-Tick: bis dahin wüsste ein
        // Kurven-An/Aus nicht, dass es den Daemon etwas angeht (siehe IsLiveCurve).
        RebuildLiveCurvesIfChanged(snapshot.Config);

        // Baseline of the dirty check (= the daemon's state just loaded) and the start of a fresh history: after an
        // import, reset or the assistant, undo must not bring the replaced config back (saving it would then undo
        // the import through the daemon's merge).
        string json = Serialize(BuildConfig());
        SetBaseline(json);
        HasUnsavedChanges = false;
        _dirtyCheckDeferred = false;
        ApplyDiff(ConfigDiff.None);
        _history.Reset(json);
        RefreshAirflow(); // Settings › Airflow: schema, balance and the proposal for the running profile
        NotifyCurveCountChanged(); // adding curves needs an initialized editor
        IsReady = true;
    }

    /// <summary>
    /// Baut den Editor nach einem <b>Reset/Import</b> vollständig aus der neuen Daemon-Config neu auf.
    /// Lokale, noch nicht gespeicherte Änderungen werden dabei bewusst verworfen (Reset/Import ist eine
    /// explizite, autoritative Aktion). Ohne diesen Neuaufbau bliebe der Editor auf dem alten Stand - und
    /// ein späteres „Speichern" würde die alte Config zurückschreiben und Reset/Import stillschweigend
    /// rückgängig machen. Wird vom <see cref="MainController"/> ausgelöst, sobald der Daemon die geänderte
    /// Config zurückspiegelt (nicht schon beim Absenden - dann läge noch die alte vor).
    /// </summary>
    public void Resync(MonitorSnapshot snapshot)
    {
        // An open „Position wählen" works on a row that goes away: it closes as a cancel (the nudge ends below).
        _locationPick?.RequestClose();
        _locationPick = null;
        RevertAllManual(); // the rows go away - their manual control must not outlive them in the daemon
        SetBaseline(null);        // RefreshDirty ist damit No-op, während wir die Collections abbauen
        _initialized = false;

        _applyingProfile = true;  // Profil-Setter-Nebenwirkungen beim Abbau unterdrücken
        SelectedProfile = null;
        _applyingProfile = false;
        _applyingActiveProfile = true;
        ActiveProfile = null;
        _applyingActiveProfile = false;
        SelectedCurve = null;

        Curves.Clear();            // Reset → OnCurvesChanged meldet die Zeilen sauber ab
        LiveCurves.Clear();
        _liveCurvesSignature = "";
        Profiles.Clear();
        Sensors.Clear();
        VisibleSensors.Clear();
        Fans.Clear();
        FilteredSensors.Clear();
        FilteredFans.Clear();
        SelectedCurveFans.Clear();
        SelectedCurveFanGroups.Clear();
        ClearAirflow(); // holds references to the fan and profile rows that just went away
        AvailableTachSensors.Clear();
        SensorGroupSections.Clear();
        FanLocationSections.Clear();
        _sensorDragActive = false;

        HasUnsavedChanges = false;
        IsReady = false;
        // The rebuilt editor starts on the daemon's running profile: nothing left to confirm or to switch back to.
        _pendingSwitchId = null;
        _pendingSwitchViaSave = false;
        _preSwitchBaselineJson = null;
        _switchBackCommand = null;

        Initialize(snapshot); // setzt Baseline + IsReady neu aus der frischen Config und leert den Verlauf
    }

    /// <summary>
    /// Feeds the live tick into the editor (working points, live values, daemon-side fan state). Everything it
    /// changes is live state, never a user edit - it runs outside the history.
    /// </summary>
    public void UpdateLive(MonitorSnapshot snapshot)
    {
        if (!_initialized)
            return;

        WithoutHistory(() => ApplyLive(snapshot));
        FollowDaemonProfile(snapshot.Config);

        // Dirty-Erkennung läuft NICHT pro Tick aus den Live-Werten (die ändern die Config nicht) - sie hängt an
        // den echten Edit-Pfaden (MarkDirty über die Collection-/Row-Handler). Der Tick zieht nur eine im
        // dirty-Zustand koaleszierte Prüfung nach (z. B. ein Punkt-Drag zurück auf die Baseline) und schließt
        // einen ruhig gewordenen Verlaufsschritt, damit der teure Vergleich höchstens einmal je Tick statt pro
        // Maus-Sample läuft.
        SettleEdits(force: false);
    }

    /// <summary>
    /// The tick while the daemon is unreachable: nothing new arrived, so the live state (and the running curves) stay
    /// as last seen. Only the edit bookkeeping of <see cref="UpdateLive"/> runs - undo works offline.
    /// </summary>
    public void UpdateOffline()
    {
        if (_initialized)
            SettleEdits(force: false);
    }

    private MonitorSnapshot? _lastLiveSnapshot; // tells a new daemon snapshot from the last one applied again

    private void ApplyLive(MonitorSnapshot snapshot)
    {
        bool fresh = !ReferenceEquals(snapshot, _lastLiveSnapshot); // the monitor hands out the last one until the next tick
        _lastLiveSnapshot = snapshot;
        Dictionary<string, double> temps = snapshot.Sensors
            .Where(s => s.Kind == SensorKind.Temperature)
            .ToDictionary(s => s.Id, s => s.Value);

        RebuildLiveCurvesIfChanged(snapshot.Config);

        // Aggregierter Live-Wert über alle Quell-Sensoren - dieselbe Kernregel wie im Daemon-Regelpfad
        // (NaN-Werte ignorieren; Max bzw. Average; ohne lesbare Quelle → NaN). Gilt für die bearbeiteten
        // Kurven UND die des laufenden Profils (Dashboard) - es sind getrennte Zeilen.
        foreach (CurveEditRow c in Curves.Concat(LiveCurves))
        {
            IEnumerable<double> values = c.Sources.Select(
                src => temps.TryGetValue(src.Id, out double t) ? t : double.NaN);
            c.LiveTemperature = SensorAggregator.Aggregate(values, c.Aggregation);
        }

        // Live-Temperatur je Sensor-Zeile (Geräte-Tab) - reine Anzeige, fließt nicht in BuildConfig.
        foreach (SensorOption s in Sensors)
            s.SetLive(temps.TryGetValue(s.Id, out double t) ? t : double.NaN);

        // The reading of every speed sensor for the „assign by hand" list (display only).
        var sensorRpms = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (SensorReading s in snapshot.Sensors.Where(r => r.Kind == SensorKind.FanRpm))
            sensorRpms.TryAdd(s.Id, s.Value);
        foreach (TachSensorOption option in AvailableTachSensors)
            if (option.Id is { } tachId)
                option.SetLive(sensorRpms.TryGetValue(tachId, out double v) ? v : null);

        // Live-Drehzahl + Kalibrier-/Identify-/Kopplungs-Status je Lüfter-Zeile spiegeln (ebenfalls reine Anzeige).
        var live = new Dictionary<string, FanReading>(StringComparer.Ordinal);
        foreach (FanReading reading in snapshot.Fans)
            live.TryAdd(reading.Id, reading);
        // Config-Ids können (durch Hand-Edit) doppelt sein - erster gewinnt (wie im SnapshotBuilder),
        // statt hart am ToDictionary zu werfen und die ganze GUI abzureißen.
        var rpmSources = new Dictionary<string, string?>();
        var minPwms = new Dictionary<string, byte>();
        var calibrations = new Dictionary<string, FanCalibration?>();
        foreach (FanConfig f in snapshot.Config.Fans)
        {
            rpmSources.TryAdd(f.FanId, f.RpmSource);
            minPwms.TryAdd(f.FanId, f.MinPwm);
            calibrations.TryAdd(f.FanId, f.Calibration);
        }
        // Own sensor names - the only name a coupled speed sensor that is not detected right now still has.
        var sensorNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SensorConfig s in snapshot.Config.Sensors.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
            sensorNames.TryAdd(s.SensorId, s.Name);

        // Vom Daemon geänderte Anlaufpunkte (Kalibrier-Ergebnis) einsammeln - sie müssen anschließend auch in
        // die Dirty-Baseline und in jeden Verlaufsschritt, sonst gälten sie als ungespeicherte Nutzer-Änderung
        // (Leiste/Verwerfen) und Rückgängig holte den alten, zu niedrigen Anlaufpunkt zurück. Rebased whenever the
        // daemon reports a new value the baseline does not have yet - even if the row already showed it as a
        // draft; the echo of the editor's own save matches the baseline and changes nothing.
        Dictionary<string, byte>? adoptedMinPwm = null;
        Dictionary<string, FanCalibration>? adoptedCalibration = null;
        AppConfig? saved = SavedConfig;
        // While a save is on its way, Save sets the baseline only once the send returns - that would drop a start point
        // adopted in between, and the row would remember it as seen. Taken over at the next poll instead.
        bool adopt = !_saveInFlight;
        bool tachsMoved = false;
        foreach (FanAssignRow fan in Fans)
        {
            fan.ApplyCalibration(snapshot.Calibration);
            if (adopt && calibrations.TryGetValue(fan.FanId, out FanCalibration? cal) && fan.ApplyStoredCalibration(cal))
            {
                adoptedCalibration ??= new Dictionary<string, FanCalibration>(StringComparer.Ordinal);
                adoptedCalibration[fan.FanId] = cal!;
            }
            fan.ApplyIdentify(snapshot.Identify);
            fan.ApplyTachMapping(snapshot.TachMapping);
            string? src = rpmSources.GetValueOrDefault(fan.FanId);
            fan.ApplyRpmSource(src, src is null ? null : sensorNames.GetValueOrDefault(src));
            if (adopt && minPwms.TryGetValue(fan.FanId, out byte min) && fan.ApplyDaemonMinPwm(min)
                && saved?.Fans.FirstOrDefault(f => f.FanId == fan.FanId)?.MinPwm != min)
            {
                adoptedMinPwm ??= new Dictionary<string, byte>(StringComparer.Ordinal);
                adoptedMinPwm[fan.FanId] = min;
            }
            FanReading? reading = live.GetValueOrDefault(fan.FanId);
            fan.SetLive(reading?.Rpm, DrivenPwm(reading), fresh ? reading?.ManualOverride : null);
            fan.ApplyCalibrationMismatch(reading?.CalibrationMismatch);
            fan.ApplyLacksSpeedSensor(reading is not null && DeviceActionBlock.LacksSpeedSensor(snapshot, reading));
            tachsMoved |= fan.ApplyDaemonTachometer(reading?.Tachometer);
        }
        // Another fan's hardware pairing may have come or gone: every row's „assign by hand" list names it.
        if (tachsMoved)
            foreach (FanAssignRow fan in Fans)
                fan.RefreshTach();

        if (adoptedMinPwm is not null || adoptedCalibration is not null)
            RebaselineFanCalibration(adoptedMinPwm, adoptedCalibration);

        _deviceBusy = DeviceActionBlock.Busy(snapshot);
        _dryRun = snapshot.DryRun;
        RefreshDeviceActionsBlocked();
    }

    /// <summary>
    /// Rebuilds <see cref="LiveCurves"/> from the daemon's own curve set - that is what actually regulates,
    /// kept in step with the active profile by <c>ProfileService.Apply</c> on every save. Guarded by a
    /// signature so the rows survive between ticks (their live working point would otherwise restart every
    /// second) and are only replaced when the stored configuration really changed.
    /// </summary>
    private void RebuildLiveCurvesIfChanged(AppConfig config)
    {
        string signature = string.Join("|", config.Curves.Select(c =>
            $"{c.Id}␟{c.Name}␟{c.Enabled}␟{c.Aggregation}␟{c.HysteresisC}␟{c.SmoothingSeconds}␟{c.InterpolationMode}"
            + $"␟{string.Join(",", CurveSourceResolver.ResolveSources(c.SourceSensorId, c.SourceSensorIds))}"
            + $"␟{string.Join(",", c.Points.Select(p => $"{p.TemperatureC}:{p.Percent}"))}"));
        if (signature == _liveCurvesSignature)
            return;
        _liveCurvesSignature = signature;

        LiveCurves.Clear();
        foreach (CurveConfig c in config.Curves)
            LiveCurves.Add(CurveEditRow.From(c, Sensors, Fans));
        LiveCurvesRevision++;
    }

    /// <summary>
    /// The PWM of a fan the daemon drives itself (curve or manual) - only then is the reported value what the fan runs
    /// at. Under hardware auto, the fail-safe or a device action it is 0, stale or throttled: <c>null</c> (unknown).
    /// </summary>
    internal static byte? DrivenPwm(FanReading? reading) =>
        reading?.ControlState is FanControlState.Curve or FanControlState.CurveHolding or FanControlState.Manual
            ? reading.Pwm
            : null;

    /// <summary>
    /// Beendet eine ggf. in einer Lüfterzeile aktive temporäre Manuell-Steuerung → zurück auf Kurve/Hardware-Auto.
    /// Beim App-Shutdown aufgerufen, damit der Geräte-Tab symmetrisch zu Onboarding/Positions-Modal aufräumt
    /// (zusätzlich zum Daemon-Disconnect-Backstop). Best effort: feuert nur, was gerade noch über die IPC geht.
    /// </summary>
    public void RevertAllManual()
    {
        foreach (FanAssignRow fan in Fans)
            fan.Manual.Revert();
    }
}
