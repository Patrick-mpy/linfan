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
public partial class CurveEditorController : ObservableObject
{
    private readonly Func<AppConfig, Task<bool>>? _save;
    private readonly Func<string, Task>? _activateProfile;
    private readonly Func<string, Task>? _calibrate;
    private readonly Func<string, Task>? _identify;
    private readonly Func<string, byte, Task>? _sendManual;
    private readonly Func<string, Task>? _sendAuto;
    private readonly Func<string, bool, Task>? _setCurveEnabled;
    private readonly Func<string, Task>? _startTachMapping;
    private readonly Func<Task>? _cancelTachMapping;
    private readonly Func<string, string?, Task>? _setFanTachometer;
    private readonly TimeSpan _statusAutoHide;
    private bool _initialized;
    private bool _applyingProfile; // unterdrückt das Live-Umschalten beim programmatischen Setzen
    private string? _savedConfigJson; // Baseline (zuletzt geladen/gespeichert) für die Dirty-Erkennung
    private bool _dirtyCheckDeferred; // eine im dirty-Zustand koaleszierte Prüfung wartet auf den nächsten Live-Tick
    private AirflowTuneResult? _lastAirflowResult; // letzte Airflow-Analyse, bis übernommen/verworfen

    // Edit-getriebene Dirty-Erkennung: an die ConfigChanged/PropertyChanged der dynamischen Zeilen gekoppelt
    // (Curves/Profiles ändern ihre Membership) → Subscriptions mitführen, um beim Entfernen/Reset abzumelden.
    private readonly HashSet<CurveEditRow> _subscribedCurves = new();
    private readonly HashSet<ProfileRow> _subscribedProfiles = new();

    public ObservableCollection<SensorOption> Sensors { get; } = new();
    public ObservableCollection<SensorOption> VisibleSensors { get; } = new();
    public ObservableCollection<CurveEditRow> Curves { get; } = new();
    public ObservableCollection<FanAssignRow> Fans { get; } = new();
    public ObservableCollection<ProfileRow> Profiles { get; } = new();

    /// <summary>
    /// Im Geräte-Tab angezeigte Teilmenge der Sensoren — gefiltert über <see cref="SensorSearch"/> und
    /// <see cref="HideHiddenSensors"/>. Discovery-Reihenfolge bleibt erhalten (kein Live-Umsortieren beim
    /// Tippen). Nicht zu verwechseln mit <see cref="VisibleSensors"/> (Quell-Sensoren für Kurven).
    /// </summary>
    public ObservableCollection<SensorOption> FilteredSensors { get; } = new();

    /// <summary>Im Geräte-Tab angezeigte Teilmenge der Lüfter — gefiltert über <see cref="FanSearch"/> und
    /// <see cref="HideHiddenFans"/>. Discovery-Reihenfolge bleibt erhalten.</summary>
    public ObservableCollection<FanAssignRow> FilteredFans { get; } = new();

    /// <summary>Lüfter-Checkboxen für die aktuell ausgewählte Kurve (neu aufgebaut bei Kurvenwechsel).</summary>
    public ObservableCollection<FanCurveCheck> SelectedCurveFans { get; } = new();

    /// <summary>Dieselben Lüfter-Checkboxen, nach Position/Gruppe gebündelt (wie das Dashboard) — Bindungsziel der gruppierten Anzeige.</summary>
    public ObservableCollection<FanCheckGroup> SelectedCurveFanGroups { get; } = new();

    /// <summary>Pro-Lüfter-Vorschläge des Airflow-Auto-Tune (nach „Airflow analysieren"); leer bis zur Analyse.</summary>
    public ObservableCollection<AirflowSuggestionRow> AirflowSuggestions { get; } = new();

    /// <summary>Hinweise/Warnungen aus der Airflow-Analyse (z. B. Unterdruck, fehlende Position).</summary>
    public ObservableCollection<string> AirflowHints { get; } = new();

    /// <summary>
    /// Vorhandene Gruppennamen (Sensoren ∪ Lüfter) als Auto-Vervollständigung für die Gruppen-Auswahlfelder
    /// im Geräte-Tab. Geteilte Instanz aller Zeilen; ein frei getippter neuer Name bleibt möglich (kein Zwang).
    /// </summary>
    public ObservableCollection<string> AvailableGroups { get; } = new();

    /// <summary>
    /// Verfügbare Drehzahl-Sensoren fürs manuelle Tacho-Dropdown je Lüfterzeile (geteilte Instanz aller Zeilen).
    /// Erster Eintrag ist immer der „keiner"-Eintrag (<see cref="TachSensorOption.Id"/> == <c>null</c>), der die
    /// Zuordnung löscht; danach jeder RPM-Sensor aus der Discovery.
    /// </summary>
    public ObservableCollection<TachSensorOption> AvailableTachSensors { get; } = new();

    [ObservableProperty] private CurveEditRow? _selectedCurve;
    [ObservableProperty] private ProfileRow? _selectedProfile;
    [ObservableProperty] private string _status = "";

    // Geräte-Tab-Filter: Suchtext + „Versteckte ausblenden" je Liste. Änderungen bauen die jeweilige
    // gefilterte Sicht neu auf (Name-Bearbeitung triggert bewusst keinen Rebuild → kein Fokusverlust beim Tippen).
    [ObservableProperty] private string _sensorSearch = "";
    [ObservableProperty] private string _fanSearch = "";
    [ObservableProperty] private bool _hideHiddenSensors;
    [ObservableProperty] private bool _hideHiddenFans;

    /// <summary>True, solange das Profil-Namensfeld eingeblendet ist (nach „+ Profil"/„Duplizieren"/„Umbenennen").</summary>
    [ObservableProperty] private bool _isNamingProfile;

    /// <summary>Standard-Stützpunkte einer neu angelegten Kurve — bewusst ein paar mehr als das frühere 30/80-Paar.</summary>
    private static readonly (decimal Temp, decimal Percent)[] DefaultCurvePoints =
    {
        (30, 20), (50, 35), (65, 50), (80, 75), (90, 100),
    };

    /// <summary>True, sobald sich der Editor-Stand von der zuletzt gespeicherten/geladenen Konfiguration unterscheidet.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;

    /// <summary>True, sobald der erste Snapshot verarbeitet wurde (Geräte/Kurven geladen). Steuert Leer-/Lade-Hinweise.</summary>
    [ObservableProperty] private bool _isReady;

    /// <summary>Klartext-Verdikt der Druckbilanz (für die Airflow-Vorschau).</summary>
    [ObservableProperty] private string _airflowPressureText = "";

    /// <summary>True, sobald eine Airflow-Analyse vorliegt (zeigt die Vorschau-Sektion).</summary>
    [ObservableProperty] private bool _hasAirflowSuggestion;

    /// <param name="save">Schickt die bearbeitete Konfiguration an den Daemon; liefert den Erfolg zurück.</param>
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
    public CurveEditorController(
        Func<AppConfig, Task<bool>>? save = null, Func<string, Task>? activateProfile = null,
        Func<string, Task>? calibrate = null, Func<string, bool, Task>? setCurveEnabled = null,
        TimeSpan? statusAutoHide = null, Func<string, Task>? identify = null,
        Func<string, byte, Task>? sendManual = null, Func<string, Task>? sendAuto = null,
        Func<string, Task>? startTachMapping = null, Func<Task>? cancelTachMapping = null,
        Func<string, string?, Task>? setFanTachometer = null)
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
        _setFanTachometer = setFanTachometer;
        _statusAutoHide = statusAutoHide ?? TimeSpan.FromSeconds(4);

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
                                       availableGroups: AvailableGroups);
            // Lebensdauer von SensorOption == Lebensdauer des Controllers (Initialize ist einmalig,
            // die Sensors-Collection wird nie neu aufgebaut) → kein Unsubscribe nötig.
            opt.PropertyChanged += OnSensorRowChanged;
            Sensors.Add(opt);
            if (opt.Visible)
                VisibleSensors.Add(opt);
        }

        // Drehzahl-Sensoren fürs manuelle Tacho-Dropdown sammeln (geteilte Liste; „keiner"-Eintrag zuerst).
        AvailableTachSensors.Add(new TachSensorOption(null, Localizer.Instance["FanAssignRow.NoTachometer"]));
        foreach (SensorReading rpm in snapshot.Sensors.Where(r => r.Kind == SensorKind.FanRpm))
            AvailableTachSensors.Add(new TachSensorOption(rpm.Id, rpm.Name));

        foreach (FanReading f in snapshot.Fans)
        {
            FanConfig baseFan = snapshot.Config.Fans.FirstOrDefault(fc => fc.FanId == f.Id)
                ?? new FanConfig { FanId = f.Id, Name = f.Name };
            var fan = new FanAssignRow(baseFan, selected: null, Curves, f.CanControl, _calibrate,
                                       availableGroups: AvailableGroups, sendIdentify: _identify,
                                       sendManual: _sendManual, sendAuto: _sendAuto,
                                       sendTachMapping: _startTachMapping, cancelTachMapping: _cancelTachMapping,
                                       sendSetTach: _setFanTachometer, availableTachSensors: AvailableTachSensors);
            // Zuordnungswechsel (Selected) muss das Aktiv-Badge der Kurven neu auswerten. Lebensdauer der
            // FanAssignRow == Lebensdauer des Controllers (Initialize ist einmalig) → kein Unsubscribe nötig.
            fan.PropertyChanged += OnFanRowChanged;
            Fans.Add(fan);
        }

        RefreshAvailableGroups(); // Vorschläge aus den geladenen Config-Gruppen befüllen
        RebuildFilteredSensors(); // Geräte-Tab-Listen initial befüllen (ungefiltert)
        RebuildFilteredFans();

        foreach (Profile p in snapshot.Config.Profiles)
            Profiles.Add(new ProfileRow(p.Id, p.Name, p.Curves, p.Assignments));

        ProfileRow? active = Profiles.FirstOrDefault(p => p.Id == snapshot.Config.ActiveProfileId)
                             ?? Profiles.FirstOrDefault();
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
            foreach (CurveConfig c in snapshot.Config.Curves)
                Curves.Add(CurveEditRow.From(c, Sensors, VisibleSensors, Fans));
            foreach (FanAssignRow fan in Fans)
            {
                string? assigned = snapshot.Config.Fans.FirstOrDefault(x => x.FanId == fan.FanId)?.AssignedCurveId;
                fan.Selected = Curves.FirstOrDefault(c => c.Id == assigned);
            }
            SelectedCurve = Curves.FirstOrDefault();
            RebuildSelectedCurveFans();
        }

        // Baseline für die Dirty-Erkennung festhalten (entspricht dem geladenen Daemon-Stand).
        _savedConfigJson = Serialize(BuildConfig());
        HasUnsavedChanges = false;
        IsReady = true;
    }

    /// <summary>
    /// Baut den Editor nach einem <b>Reset/Import</b> vollständig aus der neuen Daemon-Config neu auf.
    /// Lokale, noch nicht gespeicherte Änderungen werden dabei bewusst verworfen (Reset/Import ist eine
    /// explizite, autoritative Aktion). Ohne diesen Neuaufbau bliebe der Editor auf dem alten Stand — und
    /// ein späteres „Speichern" würde die alte Config zurückschreiben und Reset/Import stillschweigend
    /// rückgängig machen. Wird vom <see cref="MainController"/> ausgelöst, sobald der Daemon die geänderte
    /// Config zurückspiegelt (nicht schon beim Absenden — dann läge noch die alte vor).
    /// </summary>
    public void Resync(MonitorSnapshot snapshot)
    {
        _savedConfigJson = null;  // RefreshDirty ist damit No-op, während wir die Collections abbauen
        _initialized = false;

        _applyingProfile = true;  // Profil-Setter-Nebenwirkungen beim Abbau unterdrücken
        SelectedProfile = null;
        _applyingProfile = false;
        SelectedCurve = null;

        Curves.Clear();            // Reset → OnCurvesChanged meldet die Zeilen sauber ab
        Profiles.Clear();
        Sensors.Clear();
        VisibleSensors.Clear();
        Fans.Clear();
        FilteredSensors.Clear();
        FilteredFans.Clear();
        SelectedCurveFans.Clear();
        SelectedCurveFanGroups.Clear();
        AirflowSuggestions.Clear();
        AirflowHints.Clear();
        HasAirflowSuggestion = false;
        _lastAirflowResult = null;
        AvailableGroups.Clear();
        AvailableTachSensors.Clear();

        HasUnsavedChanges = false;
        IsReady = false;

        Initialize(snapshot); // setzt Baseline + IsReady neu aus der frischen Config
    }

    /// <summary>Speist jede Kurve mit der Live-Temperatur ihres Quell-Sensors (für den Arbeitspunkt im Graph).</summary>
    public void UpdateLive(MonitorSnapshot snapshot)
    {
        if (!_initialized)
            return;

        Dictionary<string, double> temps = snapshot.Sensors
            .Where(s => s.Kind == SensorKind.Temperature)
            .ToDictionary(s => s.Id, s => s.Value);

        // Aggregierter Live-Wert über alle Quell-Sensoren — dieselbe Kernregel wie im Daemon-Regelpfad
        // (NaN-Werte ignorieren; Max bzw. Average; ohne lesbare Quelle → NaN).
        foreach (CurveEditRow c in Curves)
        {
            IEnumerable<double> values = c.Sources.Select(
                src => temps.TryGetValue(src.Id, out double t) ? t : double.NaN);
            c.LiveTemperature = SensorAggregator.Aggregate(values, c.Aggregation);
        }

        // Live-Temperatur je Sensor-Zeile (Geräte-Tab) — reine Anzeige, fließt nicht in BuildConfig.
        foreach (SensorOption s in Sensors)
            s.SetLive(temps.TryGetValue(s.Id, out double t) ? t : double.NaN);

        // Live-Drehzahl + Kalibrier-/Identify-/Kopplungs-Status je Lüfter-Zeile spiegeln (ebenfalls reine Anzeige).
        Dictionary<string, double?> rpms = snapshot.Fans.ToDictionary(f => f.Id, f => f.Rpm);
        // Config-Ids können (durch Id-Migration/Hand-Edit) doppelt sein — erster gewinnt (wie im SnapshotBuilder),
        // statt hart am ToDictionary zu werfen und die ganze GUI abzureißen.
        var rpmSources = new Dictionary<string, string?>();
        foreach (FanConfig f in snapshot.Config.Fans)
            rpmSources.TryAdd(f.FanId, f.RpmSource);
        foreach (FanAssignRow fan in Fans)
        {
            fan.ApplyCalibration(snapshot.Calibration);
            fan.ApplyIdentify(snapshot.Identify);
            fan.ApplyTachMapping(snapshot.TachMapping);
            fan.ApplyRpmSource(rpmSources.TryGetValue(fan.FanId, out string? src) ? src : null);
            fan.SetLiveRpm(rpms.TryGetValue(fan.FanId, out double? r) ? r : null);
        }

        // Dirty-Erkennung läuft NICHT pro Tick aus den Live-Werten (die ändern die Config nicht) — sie hängt an
        // den echten Edit-Pfaden (MarkDirty über die Collection-/Row-Handler). Der Tick zieht nur eine im
        // dirty-Zustand koaleszierte Prüfung nach (z. B. ein Punkt-Drag zurück auf die Baseline), damit der
        // teure Vergleich höchstens einmal je Tick statt pro Maus-Sample läuft.
        if (_dirtyCheckDeferred)
            RefreshDirty();
    }

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
