// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;

namespace LinFan.App.Controllers;

/// <summary>
/// Controller des Onboarding-Assistenten: führt den Nutzer durch Kalibrierung und Profil-Wahl.
/// Testbar via Delegates - kein direkter IpcLiveMonitor-Typ.
/// </summary>
public partial class OnboardingController : ObservableObject
{
    private readonly Func<string, Task> _sendStartCalibration;
    private readonly Func<Task> _sendCancelCalibration;
    private readonly Func<AppConfig, Task<bool>> _sendConfig;
    private readonly Action _onClose;
    private readonly Func<string, Task>? _sendIdentify;
    private readonly Func<string, byte, Task>? _sendManual;
    private readonly Func<string, Task>? _sendAuto;
    // Automatische Tacho-Kopplung als Vorstufe der Kalibrierung. Optional: fehlt sie (ältere Verdrahtung/Tests),
    // wird der Kopplungs-Schritt übersprungen und direkt kalibriert (unveränderter Alt-Pfad).
    private readonly Func<string, Task>? _sendStartTachMapping;
    private readonly Func<Task>? _sendCancelTachMapping;

    // Latch: verhindert doppeltes Senden (Skip + Window-Close)
    private bool _closeSent;

    // Wird beim ersten Apply befüllt und danach nur noch für Live-Updates genutzt
    private bool _populated;
    private MonitorSnapshot? _cachedSnapshot;

    // Zustand für die laufende Kalibriersequenz
    private CancellationTokenSource? _calibrationCts;
    private TaskCompletionSource<bool>? _calibrationDoneTcs;
    private string? _waitingForFanId;

    // Zustand für die Kopplungs-Vorstufe (spiegelt das Kalibrier-Warte-Muster; nie gleichzeitig aktiv)
    private TaskCompletionSource<bool>? _tachMappingDoneTcs;
    private string? _waitingTachForFanId;
    private TachMappingStatus? _tachResult; // vom Apply gesetzter Abschluss-Status, den die Schleife auswertet

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStepper))]
    private OnboardingStep _currentStep = OnboardingStep.Welcome;

    /// <summary>The step indicator - not on the welcome page, which is not one of the four steps.</summary>
    public bool ShowStepper => CurrentStep != OnboardingStep.Welcome;

    /// <summary>
    /// Where the daemon moved an unusable configuration, which is why this assistant opened - the welcome page says
    /// so. Kept for the assistant's lifetime: the daemon drops the report with the first saved change, which may come
    /// from this very assistant (a calibration).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigSetAsideNote), nameof(HasConfigSetAsideNote))]
    private string? _configSetAsidePath;

    public string? ConfigSetAsideNote => ConfigSetAsidePath is { } path
        ? Localizer.Instance.Format("Onboarding.ConfigSetAside", path)
        : null;

    public bool HasConfigSetAsideNote => ConfigSetAsidePath is not null;

    /// <summary>The daemon runs dry (without the rights to write fans) - the calibration step says so.</summary>
    [ObservableProperty] private bool _isDryRun;

    /// <summary>
    /// Why the daemon would refuse calibration, coupling and identification right now (offline, dry run, a device action
    /// running - see <see cref="DeviceActionBlock"/>); empty when it would not. The sequence would otherwise wait 60 s
    /// per fan for results that never come.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalibrateAllCommand))]
    private string _deviceActionsBlockedReason = "";

    /// <summary>
    /// No fan is controllable because the driver locks fan control (ThinkPad without <c>fan_control=1</c>) - the
    /// calibration step names the fix instead of only "no controllable fans".
    /// </summary>
    [ObservableProperty] private bool _driverLocked;

    /// <summary>The step indicator's segments, one per step after the welcome page; rebuilt by <see cref="RebuildSteps"/>.</summary>
    public ObservableCollection<OnboardingStepItem> Steps { get; } = new();

    private static readonly (OnboardingStep Step, string LabelKey)[] StepLabels =
    [
        (OnboardingStep.Calibration, "OnboardingCtrl.StepCalibration"),
        (OnboardingStep.Devices, "OnboardingCtrl.StepDevices"),
        (OnboardingStep.ChooseProfile, "OnboardingCtrl.StepProfile"),
        (OnboardingStep.Done, "OnboardingCtrl.StepDone"),
    ];

    [ObservableProperty] private bool _isCalibrating;
    [ObservableProperty] private string _calibrationProgress = "";
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>Position des gerade kalibrierten Lüfters in der Sequenz (1-basiert; 0 = noch keiner).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationHeadline))]
    private int _calibrationFanIndex;

    /// <summary>Gesamtzahl der zu kalibrierenden (steuerbaren) Lüfter der laufenden Sequenz.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationHeadline))]
    private int _calibrationFanCount;

    /// <summary>Anzeigename des gerade kalibrierten Lüfters.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationHeadline))]
    private string _calibrationFanName = "";

    /// <summary>Fortschritt des aktuellen Lüfters in Prozent (0..100), abgeleitet aus dem PWM-Rampenwert.</summary>
    [ObservableProperty] private double _calibrationFanProgress;

    /// <summary>Gesamtfortschritt der Sequenz in Prozent (0..100): bereits fertige Lüfter + Anteil des aktuellen.</summary>
    [ObservableProperty] private double _calibrationOverallProgress;

    /// <summary>Kopfzeile der Fortschrittsanzeige, z. B. „Kalibriere Lüfter 2 von 5: CPU Fan". Leer ohne laufende Sequenz.</summary>
    public string CalibrationHeadline => CalibrationFanCount > 0
        ? Localizer.Instance.Format("OnboardingCtrl.CalibrationHeadline", CalibrationFanIndex, CalibrationFanCount, CalibrationFanName)
        : "";

    /// <summary>Steuerbare Lüfter - Grundlage der Kalibrierung. Teilmenge von <see cref="Fans"/> (gleiche Instanzen).</summary>
    public ObservableCollection<OnboardingFanRow> ControllableFans { get; } = new();

    /// <summary>Alle erkannten Lüfter - Grundlage der (optionalen) Positions-Auswahl im Geräte-Schritt.</summary>
    public ObservableCollection<OnboardingFanRow> Fans { get; } = new();

    /// <summary>
    /// The case type the positions are picked in - from the config, changed in „Position wählen"; Finish keeps it.
    /// </summary>
    public CaseType CaseType { get; private set; }

    /// <summary>The „Position wählen" dialog for <paramref name="row"/>, with the other fans in the schema.</summary>
    public LocationPickController CreateLocationPick(OnboardingFanRow row) => new(
        row.FanId, row.DisplayName, row.Location.Value, CaseType, row.Manual,
        Fans.Where(f => !ReferenceEquals(f, row))
            .Select(f => new CaseFan(f.FanId, f.DisplayName, f.Location.Value, f.Manual))
            .ToList());

    /// <summary>Takes over what „Position wählen" returned: the row's position and the case type it was picked in.</summary>
    public void ApplyLocationPick(OnboardingFanRow row, LocationPick pick)
    {
        CaseType = pick.CaseType;
        row.Location = FanLocationOption.For(pick.Location);
    }

    /// <summary>
    /// Temperatursensoren - speisen sowohl die Primärsensor-Wahl als auch die Sichtbarkeits-Auswahl
    /// im Geräte-Schritt (gleiches Scope wie der Geräte-Tab, der nur Temperatursensoren persistiert).
    /// </summary>
    public ObservableCollection<SensorOption> TemperatureSensors { get; } = new();

    /// <summary>
    /// Choices for the primary sensor: visible sensors only, plus the current selection while it is hidden (kept
    /// selectable, mirroring the curve source lists). Sensors deselected in the devices step must not be offered as
    /// the primary curve source.
    /// </summary>
    public IReadOnlyList<SensorOption> PrimarySensorChoices =>
        TemperatureSensors.Where(s => s.Visible || ReferenceEquals(s, SelectedPrimarySensor)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinish), nameof(PrimarySensorChipName))]
    private SensorOption? _selectedPrimarySensor;

    /// <summary>Accessible name of the primary-sensor chip, which shows the value only as text inside.</summary>
    public string PrimarySensorChipName => Localizer.Instance.Format("Onboarding.PrimarySensorChipName",
        SelectedPrimarySensor?.Name ?? Localizer.Instance["Onboarding.PrimarySensorNone"]);

    /// <summary>
    /// The primary-sensor chip's menu, rebuilt as it opens (<see cref="RefreshPrimarySensorMenu"/>). One collection
    /// filled in place: the open flyout keeps the items source it was created with.
    /// </summary>
    public ObservableCollection<MenuChoice> PrimarySensorMenu { get; } = new();

    /// <summary>
    /// Builds the primary-sensor menu: the <see cref="PrimarySensorChoices"/> under the headings of their sensor groups
    /// (the devices step's), each with its live temperature. Built as the menu opens, so the values are current.
    /// </summary>
    public void RefreshPrimarySensorMenu()
    {
        IReadOnlyList<SensorOption> choices = PrimarySensorChoices;
        ObservableCollection<MenuChoice> menu = PrimarySensorMenu;
        menu.Clear();
        var listed = new HashSet<SensorOption>();
        foreach (OnboardingSensorGroup group in TemperatureSensorGroups)
            AddSection(group.Name, group.Sensors.Where(choices.Contains).ToList());
        AddSection(GroupLabels.Ungrouped, choices.Where(s => !listed.Contains(s)).ToList());

        void AddSection(string heading, IReadOnlyList<SensorOption> sensors)
        {
            if (sensors.Count == 0)
                return;
            menu.Add(MenuChoice.Heading(heading));
            foreach (SensorOption sensor in sensors)
            {
                listed.Add(sensor);
                menu.Add(MenuChoice.Value(sensor.Name, new RelayCommand(() => SelectedPrimarySensor = sensor),
                    ReferenceEquals(sensor, SelectedPrimarySensor), detail: sensor.LiveValue));
            }
        }
    }

    /// <summary>
    /// True while the profile step builds the three profiles from the airflow analysis - at least one
    /// controllable fan has a role-specific mounting position. False = primary-sensor fallback
    /// (<see cref="DefaultProfiles"/>), the pre-airflow behavior.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    private bool _useAirflowProfiles;

    /// <summary>Pressure-balance line of the airflow preview (localized, materialized text).</summary>
    [ObservableProperty] private string _airflowPressureText = "";

    /// <summary>Per-fan role/curve summary shown in airflow mode - display-only, no opt-out here.</summary>
    public ObservableCollection<AirflowSuggestionRow> AirflowSummary { get; } = new();

    /// <summary>Localized airflow hints (missing intake, count-estimate only, …).</summary>
    public ObservableCollection<string> AirflowHints { get; } = new();

    /// <summary>Finish needs a selected primary sensor only in fallback mode.</summary>
    public bool CanFinish => UseAirflowProfiles || SelectedPrimarySensor is not null;

    // Done-page summary: raw facts captured by PopulateDoneSummary; the strings materialize in the
    // getters so a language switch only needs change notifications. The profile is kept as an id and
    // resolved via ProfileOptions in the getter - those are rebuilt on language change, so the name
    // switches language with the rest of the page.
    private int _doneCalibrated;
    private int _doneControllable;
    private int _donePositions;
    private int _doneFanCount;
    private string _doneProfileId = "";
    private bool _doneUsedAirflow;
    private string _donePrimarySensorName = "";

    public string DoneCalibrationSummary =>
        Localizer.Instance.Format("OnboardingCtrl.DoneCalibrated", _doneCalibrated, _doneControllable);

    public string DonePositionsSummary =>
        Localizer.Instance.Format("OnboardingCtrl.DonePositions", _donePositions, _doneFanCount);

    public string DoneProfileSummary
    {
        get
        {
            string profileName = ProfileOptions.FirstOrDefault(p => p.Id == _doneProfileId)?.DisplayName ?? _doneProfileId;
            return _doneUsedAirflow
                ? Localizer.Instance.Format("OnboardingCtrl.DoneProfileAirflow", profileName)
                : Localizer.Instance.Format("OnboardingCtrl.DoneProfileFallback", profileName, _donePrimarySensorName);
        }
    }

    /// <summary>Captures what the finished run actually configured - shown on the Done page.</summary>
    private void PopulateDoneSummary(bool usedAirflow, string? primarySensorName)
    {
        _doneCalibrated = ControllableFans.Count(f => f.CalibrationState == OnboardingCalibrationState.Done);
        _doneControllable = ControllableFans.Count;
        _donePositions = Fans.Count(f => f.Location.Value != FanLocation.Unspecified);
        _doneFanCount = Fans.Count;
        _doneProfileId = SelectedProfileId;
        _doneUsedAirflow = usedAirflow;
        _donePrimarySensorName = primarySensorName ?? "";
        OnPropertyChanged(nameof(DoneCalibrationSummary));
        OnPropertyChanged(nameof(DonePositionsSummary));
        OnPropertyChanged(nameof(DoneProfileSummary));
    }

    [ObservableProperty] private string _selectedProfileId = "balanced";

    /// <summary>In der UI gewählte Profil-Option (ListBox-Bindung); hält <see cref="SelectedProfileId"/> synchron.</summary>
    [ObservableProperty] private ProfileOption? _selectedProfile;

    /// <summary>True, sobald mindestens ein steuerbarer Lüfter erkannt wurde (steuert die Leer-Anzeige).</summary>
    [ObservableProperty] private bool _hasControllableFans;

    partial void OnSelectedProfileChanged(ProfileOption? value)
    {
        if (value is not null)
            SelectedProfileId = value.Id;
    }

    public IReadOnlyList<ProfileOption> ProfileOptions { get; private set; } = BuildProfileOptions();

    private static IReadOnlyList<ProfileOption> BuildProfileOptions() =>
    [
        new ProfileOption("silent", Localizer.Instance["OnboardingCtrl.ProfileSilentName"], Localizer.Instance["OnboardingCtrl.ProfileSilentDesc"]),
        new ProfileOption("balanced", Localizer.Instance["OnboardingCtrl.ProfileBalancedName"], Localizer.Instance["OnboardingCtrl.ProfileBalancedDesc"]),
        new ProfileOption("performance", Localizer.Instance["OnboardingCtrl.ProfilePerformanceName"], Localizer.Instance["OnboardingCtrl.ProfilePerformanceDesc"]),
    ];

    public OnboardingController(
        Func<string, Task> sendStartCalibration,
        Func<Task> sendCancelCalibration,
        Func<AppConfig, Task<bool>> sendConfig,
        Action onClose,
        Func<string, Task>? sendIdentify = null,
        Func<string, byte, Task>? sendManual = null,
        Func<string, Task>? sendAuto = null,
        Func<string, Task>? sendStartTachMapping = null,
        Func<Task>? sendCancelTachMapping = null)
    {
        _sendStartCalibration = sendStartCalibration;
        _sendCancelCalibration = sendCancelCalibration;
        _sendConfig = sendConfig;
        _onClose = onClose;
        _sendIdentify = sendIdentify;
        _sendManual = sendManual;
        _sendAuto = sendAuto;
        _sendStartTachMapping = sendStartTachMapping;
        _sendCancelTachMapping = sendCancelTachMapping;

        // Default-Auswahl in der ListBox vorbelegen (Id bleibt "balanced").
        _selectedProfile = ProfileOptions.FirstOrDefault(p => p.Id == _selectedProfileId);
        RebuildSteps();

        // Bei Sprachwechsel die Profil-Optionen neu aufbauen: die ListBox bindet DisplayName/Description
        // direkt, daher müssen frische Instanzen mit übersetzten Texten nachgereicht werden. Die Auswahl
        // wird über die Id rekonstruiert (nicht die alte Instanz gehalten). CalibrationHeadline ist ein
        // berechneter Localizer-Lookup und muss ebenfalls neu gelesen werden.
        // Benannter Handler + Unsubscribe beim Schließen (CloseWizard): dieser Controller wird pro
        // Onboarding-Durchlauf neu erzeugt (Erststart und „Einstellungen → Sicherung & Einrichtung"), ein anonymes
        // Dauer-Abo am app-lebenslangen Localizer-Singleton würde jede alte Instanz am Leben halten.
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        ProfileOptions = BuildProfileOptions();
        OnPropertyChanged(nameof(ProfileOptions));
        SelectedProfile = ProfileOptions.FirstOrDefault(p => p.Id == SelectedProfileId);
        OnPropertyChanged(nameof(CalibrationHeadline));
        RebuildSteps();
        // Suggested group names are in the UI language, and a name can merge with an existing group in one
        // language but not in the other - regroup rather than rename.
        RebuildSensorGroups(_cachedSnapshot?.Config.SensorGroups ?? []);
        // The airflow preview holds materialized localized strings → rebuild while visible.
        if (CurrentStep == OnboardingStep.ChooseProfile)
            RefreshProfilePreview();
        OnPropertyChanged(nameof(ConfigSetAsideNote));
        OnPropertyChanged(nameof(PrimarySensorChipName));
        RefreshDefaultFanNames();
        foreach (OnboardingFanRow fan in Fans)
            fan.NotifyLanguageChanged();
        // The Done summary getters read the Localizer directly - re-announce them.
        OnPropertyChanged(nameof(DoneCalibrationSummary));
        OnPropertyChanged(nameof(DonePositionsSummary));
        OnPropertyChanged(nameof(DoneProfileSummary));
    }

    /// <summary>Schließt den Assistenten: löst das Localizer-Abo und meldet an den Besitzer zurück.</summary>
    private void CloseWizard()
    {
        Localizer.Instance.PropertyChanged -= OnLanguageChanged;
        _onClose();
    }

    /// <summary>
    /// Wird vom <see cref="MainController"/> einmal pro Tick auf dem UI-Thread aufgerufen.
    /// Befüllt beim ersten Aufruf Lüfter und Sensoren; verarbeitet danach Kalibrier-Updates.
    /// </summary>
    public void Apply(MonitorSnapshot snapshot)
    {
        bool fresh = !ReferenceEquals(snapshot, _cachedSnapshot); // the monitor hands out the last one until the next tick
        _cachedSnapshot = snapshot;
        IsDryRun = snapshot.DryRun;
        DeviceActionsBlockedReason = DeviceActionBlock.Reason(snapshot.Connected, snapshot.DryRun, DeviceActionBlock.Busy(snapshot));
        DriverLocked = snapshot.Fans.Any(f => f.ReadOnlyReason == FanReadOnlyReason.DriverLocked);
        if (snapshot.ConfigProblem is ConfigLoadProblem.SetAside)
            ConfigSetAsidePath ??= snapshot.ConfigSetAsidePath;

        if (!_populated && (snapshot.Fans.Count > 0 || snapshot.Sensors.Count > 0))
        {
            _populated = true;
            Populate(snapshot);
        }

        // Live speed and power per fan for the manual slider (display only; also while the position dialog is open, as it
        // shares the row). The power is where manual control starts when the fan is nudged.
        var liveById = new Dictionary<string, FanReading>(StringComparer.Ordinal);
        foreach (FanReading reading in snapshot.Fans)
            liveById.TryAdd(reading.Id, reading);
        foreach (OnboardingFanRow fan in Fans)
        {
            FanReading? reading = liveById.GetValueOrDefault(fan.FanId);
            fan.SetLive(reading?.Rpm, CurveEditorController.DrivenPwm(reading), fresh ? reading?.ManualOverride : null);
            fan.DeviceActionsBlockedReason = DeviceActionsBlockedReason;
        }

        // Live temperature per sensor - the devices step lists it, the primary-sensor menu names it.
        if (fresh)
        {
            var temps = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (SensorReading reading in snapshot.Sensors)
                temps.TryAdd(reading.Id, reading.Value);
            foreach (SensorOption sensor in TemperatureSensors)
                sensor.SetLive(temps.TryGetValue(sensor.Id, out double value) ? value : double.NaN);
        }

        // Kopplungs-Vorstufe aus dem Snapshot ableiten (läuft vor der Kalibrierung; nie gleichzeitig).
        if (_waitingTachForFanId is { } tachFanId && snapshot.TachMapping is { } tm && tm.FanId == tachFanId)
        {
            OnboardingFanRow? row = ControllableFans.FirstOrDefault(f => f.FanId == tachFanId);
            if (!tm.Running)
            {
                // Abschluss (Matched/NoResponse/Ambiguous/Failed) → Ergebnis merken; die Auswertung (weiter vs.
                // überspringen) und die Zustands-/Textwahl trifft die Sequenz-Schleife, sobald das TCS wacht.
                _tachResult = tm;
                _tachMappingDoneTcs?.TrySetResult(true);
            }
            else if (row is not null)
            {
                row.CalibrationState = OnboardingCalibrationState.Coupling;
            }
        }

        // Kalibrier-Fortschritt aus dem Snapshot ableiten
        if (_waitingForFanId is { } fanId && snapshot.Calibration is { } cal && cal.FanId == fanId)
        {
            // Anzeigename statt Hardware-Id zeigen (z. B. „thinkpad pwm1" statt „hwmon8/pwm1").
            OnboardingFanRow? row = ControllableFans.FirstOrDefault(f => f.FanId == fanId);
            string fanName = row?.DisplayName ?? fanId;
            if (cal.Done || cal.FailReason is not null)
            {
                if (cal.FailReason is { } reason)
                {
                    CalibrationProgress = Localizer.Instance.Format(
                        "OnboardingCtrl.CalibrationError", fanName, IpcStatusText.Fail(reason, cal.OverTempC, cal.OverLimitC));
                    if (row is not null) row.CalibrationState = OnboardingCalibrationState.Failed;
                }
                else
                {
                    CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CalibrationDone", fanName, cal.StartPwm);
                    if (row is not null) row.CalibrationState = OnboardingCalibrationState.Done;
                }

                // Aktuellen Lüfter als abgeschlossen werten - Gesamtfortschritt auf den Anteil der fertigen zählen.
                CalibrationFanProgress = 100;
                if (CalibrationFanCount > 0)
                    CalibrationOverallProgress = (double)CalibrationFanIndex / CalibrationFanCount * 100;

                // TCS signalisieren, damit die Sequenz weitermacht
                _calibrationDoneTcs?.TrySetResult(true);
            }
            else if (cal.Running)
            {
                if (row is not null) row.CalibrationState = OnboardingCalibrationState.Running;

                // Die Rampe läuft den vollen PWM-Bereich 0..255 ab (CalibrationService) → CurrentPwm/255 ist ein
                // monotoner Fortschritt für den aktuellen Lüfter; der Gesamtfortschritt addiert die fertigen davor.
                double fraction = Math.Clamp(cal.CurrentPwm / 255.0, 0, 1);
                CalibrationFanProgress = fraction * 100;
                if (CalibrationFanCount > 0)
                    CalibrationOverallProgress = (CalibrationFanIndex - 1 + fraction) / CalibrationFanCount * 100;

                CalibrationProgress = Localizer.Instance.Format(
                    "OnboardingCtrl.CalibrationPhase", IpcStatusText.Phase(cal.Phase, cal.CurrentPwm), cal.CurrentPwm, cal.CurrentRpm);
            }
        }
    }

    // --- Schritt-Navigation -----------------------------------------------------------------------

    /// <summary>Jeder Schritt-Wechsel beendet eine ggf. laufende temporäre Manuell-Steuerung (sie lebt nur im
    /// Geräte-Schritt) → zugeordnete Lüfter zurück auf Kurve/Hardware-Auto.</summary>
    partial void OnCurrentStepChanged(OnboardingStep value)
    {
        RevertAllManual();
        RebuildSteps();
        // Entering the profile step re-evaluates the airflow gate + preview: the positions were just
        // chosen in the devices step, so the mode can flip on every visit.
        if (value == OnboardingStep.ChooseProfile)
            RefreshProfilePreview();
    }

    private void RebuildSteps()
    {
        Steps.Clear();
        for (int i = 0; i < StepLabels.Length; i++)
        {
            (OnboardingStep step, string labelKey) = StepLabels[i];
            Steps.Add(new OnboardingStepItem(
                $"{i + 1} · {Localizer.Instance[labelKey]}", IsDone: step < CurrentStep, IsCurrent: step == CurrentStep));
        }
    }

    private void RevertAllManual()
    {
        foreach (OnboardingFanRow fan in Fans)
            fan.Manual.Revert();
    }

    [RelayCommand]
    private void Next()
    {
        // Done is reachable only through a successful Finish (it is the receipt page, not a step
        // that can be walked into) - hence no ChooseProfile arm here.
        CurrentStep = CurrentStep switch
        {
            OnboardingStep.Welcome => OnboardingStep.Calibration,
            OnboardingStep.Calibration => OnboardingStep.Devices,
            OnboardingStep.Devices => OnboardingStep.ChooseProfile,
            _ => CurrentStep,
        };
    }

    [RelayCommand]
    private async Task Back()
    {
        // Zurück aus dem Kalibrierschritt bricht eine laufende Kalibrierung ab
        if (CurrentStep == OnboardingStep.Calibration && IsCalibrating)
            await AbortCalibrationAsync();

        // No Done arm: the config is already saved there - Back would suggest an undo that no
        // longer exists. The nav bar hides the Back button on Done accordingly.
        CurrentStep = CurrentStep switch
        {
            OnboardingStep.Calibration => OnboardingStep.Welcome,
            OnboardingStep.Devices => OnboardingStep.Calibration,
            OnboardingStep.ChooseProfile => OnboardingStep.Devices,
            _ => CurrentStep,
        };
    }

    // --- Kalibrierung -----------------------------------------------------------------------------

    private bool CanCalibrateAll() => DeviceActionsBlockedReason.Length == 0;

    [RelayCommand(CanExecute = nameof(CanCalibrateAll))]
    private async Task CalibrateAll()
    {
        if (IsCalibrating || ControllableFans.Count == 0)
        {
            if (ControllableFans.Count == 0)
                NextCommand.Execute(null);
            return;
        }

        IsCalibrating = true;
        _calibrationCts = new CancellationTokenSource();
        var ct = _calibrationCts.Token;

        int total = ControllableFans.Count;
        CalibrationFanCount = total;
        CalibrationOverallProgress = 0;
        foreach (OnboardingFanRow f in ControllableFans)
            f.CalibrationState = OnboardingCalibrationState.Pending;

        try
        {
            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested)
                    break;

                OnboardingFanRow fan = ControllableFans[i];
                CalibrationFanIndex = i + 1;
                CalibrationFanName = fan.DisplayName;
                CalibrationFanProgress = 0;

                // Phase 1: Tacho automatisch koppeln (Vorstufe - die Kalibrierung braucht den Tacho). Ist der
                // Kopplungs-Pfad nicht verdrahtet, direkt kalibrieren (Alt-Verhalten). Kein Tacho / nicht eindeutig
                // / Fehler → Kalibrierung dieses Lüfters überspringen, nächster Lüfter.
                if (_sendStartTachMapping is not null)
                {
                    bool proceed = await CoupleFanAsync(fan, i + 1, total, ct);
                    if (ct.IsCancellationRequested)
                        break;
                    if (!proceed)
                        continue;
                }

                // Phase 2: Kalibrieren (nutzt den gerade gekoppelten Tacho automatisch - daemon-seitig via RpmSource).
                fan.CalibrationState = OnboardingCalibrationState.Running;
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CalibrationStarting", i + 1, total);

                _calibrationDoneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitingForFanId = fan.FanId;

                await _sendStartCalibration(fan.FanId);

                // Warten bis Done - mit defensivem Timeout (60 s)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                combined.Token.Register(() => _calibrationDoneTcs.TrySetResult(false));

                bool done = await _calibrationDoneTcs.Task;
                _waitingForFanId = null;

                if (ct.IsCancellationRequested)
                    break;

                if (!done)
                {
                    // Timeout: der Daemon meldete weder Done noch Error. Die laufende Kalibrierung abbrechen,
                    // sonst verwirft der CalibrationCoordinator (nur eine gleichzeitig) den nächsten
                    // StartCalibration still und der folgende Lüfter würde übersprungen.
                    CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CalibrationTimeout", i + 1, total);
                    fan.CalibrationState = OnboardingCalibrationState.Failed;
                    await _sendCancelCalibration();
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Localizer.Instance.Format("OnboardingCtrl.CalibrationInterrupted", ex.Message);
        }
        finally
        {
            IsCalibrating = false;
            _calibrationCts?.Dispose();
            _calibrationCts = null;
            _waitingForFanId = null;
        }
    }

    /// <summary>
    /// Koppelt den Drehzahl-Sensor eines Lüfters (Phase 1, vor der Kalibrierung) und spiegelt das
    /// Kalibrier-Warte-Muster (TCS + 60-s-Timeout). Liefert <c>true</c>, wenn genau ein Tacho zugeordnet wurde
    /// (Kalibrierung kann folgen); <c>false</c> bei kein Tacho / nicht eindeutig / Fehler / Timeout - dann ist
    /// der Lüfter entsprechend markiert und die Kalibrierung wird übersprungen.
    /// </summary>
    private async Task<bool> CoupleFanAsync(OnboardingFanRow fan, int index, int total, CancellationToken ct)
    {
        fan.CalibrationState = OnboardingCalibrationState.Coupling;
        CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CouplingStarting", index, total);

        _tachResult = null;
        _tachMappingDoneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitingTachForFanId = fan.FanId;

        await _sendStartTachMapping!(fan.FanId);

        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
        {
            combined.Token.Register(() => _tachMappingDoneTcs.TrySetResult(false));
            bool signaled = await _tachMappingDoneTcs.Task;
            _waitingTachForFanId = null;

            if (ct.IsCancellationRequested)
                return false;

            if (!signaled)
            {
                // Timeout: laufende Kopplung abbrechen, damit der Koordinator frei für den nächsten Lüfter ist.
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CouplingTimeout", index, total);
                fan.CalibrationState = OnboardingCalibrationState.Failed;
                AdvanceOverall();
                await AckTachMapping();
                return false;
            }
        }

        TachMappingStatus? result = _tachResult;
        TachMappingPhase? phase = result?.Phase;

        if (phase == TachMappingPhase.Matched)
            return true; // weiter zur Kalibrierung - der Daemon persistiert die Zuordnung selbst

        // Nicht eindeutig gekoppelt (kein Signal / mehrdeutig - z. B. zwei Tacho-Kanäle für denselben Lüfter,
        // die gemeinsam ansteigen). Dreht der Lüfter aber messbar, existiert bereits ein brauchbarer Tacho
        // (Backend-Heuristik oder frühere Zuordnung, vom Daemon bei nicht-eindeutigem Ergebnis unangetastet):
        // dann NICHT überspringen, sondern damit kalibrieren - die Kopplung soll eine funktionierende Paarung
        // verbessern, nicht entwerten. Nur ein wirklich tacholoser Lüfter (keine Live-Drehzahl) wird übersprungen.
        if ((phase is TachMappingPhase.NoResponse or TachMappingPhase.Ambiguous) && FanHasLiveTacho(fan.FanId))
        {
            await AckTachMapping(); // Ergebnis quittieren → Koordinator frei, dann mit vorhandenem Tacho kalibrieren
            return true;
        }

        switch (phase)
        {
            case TachMappingPhase.NoResponse:
                fan.CalibrationState = OnboardingCalibrationState.NoTacho;
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CouplingNoTacho", fan.DisplayName);
                break;

            case TachMappingPhase.Ambiguous:
                fan.CalibrationState = OnboardingCalibrationState.Ambiguous;
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CouplingAmbiguous", fan.DisplayName);
                break;

            default: // Failed (Übertemp / kein Watchdog / Unknown) oder unerwartet null
                fan.CalibrationState = OnboardingCalibrationState.Failed;
                string reason = result?.FailReason is { } r
                    ? IpcStatusText.Fail(r, result.OverTempC, result.OverLimitC)
                    : "";
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CouplingFailed", fan.DisplayName, reason);
                break;
        }

        AdvanceOverall();       // übersprungenen Lüfter im Gesamtfortschritt mitzählen (Balken stockt nicht)
        await AckTachMapping();  // Abschluss-Status quittieren → Koordinator frei für den nächsten Lüfter
        return false;
    }

    /// <summary>Rechnet den aktuellen Lüfter als abgeschlossen in den Gesamtfortschritt (fertige / Gesamtzahl).</summary>
    private void AdvanceOverall()
    {
        if (CalibrationFanCount > 0)
            CalibrationOverallProgress = (double)CalibrationFanIndex / CalibrationFanCount * 100;
    }

    /// <summary>Quittiert einen Kopplungs-Abschluss-Status im Daemon (best effort; ohne verdrahteten Pfad ein No-op).</summary>
    private Task AckTachMapping() => _sendCancelTachMapping?.Invoke() ?? Task.CompletedTask;

    /// <summary>
    /// Dreht der Lüfter aktuell messbar (positive Live-Drehzahl)? Dann existiert ein brauchbarer Tacho
    /// (Heuristik oder frühere Zuordnung), und eine nicht-eindeutige Kopplung soll die Kalibrierung nicht
    /// verhindern. <see cref="FanReading.Rpm"/> ist <c>null</c>, wenn kein Tacho vorhanden/lesbar ist.
    /// </summary>
    private bool FanHasLiveTacho(string fanId) =>
        _cachedSnapshot?.Fans.FirstOrDefault(f => f.Id == fanId)?.Rpm is { } rpm && rpm > 0;

    [RelayCommand]
    private async Task SkipCalibration()
    {
        if (IsCalibrating)
            await AbortCalibrationAsync();

        NextCommand.Execute(null);
    }

    private async Task AbortCalibrationAsync()
    {
        _calibrationCts?.Cancel();
        _calibrationDoneTcs?.TrySetResult(false);
        _tachMappingDoneTcs?.TrySetResult(false); // eine ggf. laufende Kopplungs-Vorstufe ebenfalls freigeben
        await _sendCancelCalibration();
        if (_sendCancelTachMapping is not null)
            await _sendCancelTachMapping();
        IsCalibrating = false;
        CalibrationProgress = Localizer.Instance["OnboardingCtrl.CalibrationAborted"];
    }

    // --- Abschluss / Überspringen -----------------------------------------------------------------

    [RelayCommand]
    private async Task Finish()
    {
        RevertAllManual(); // eine offene temporäre Manuell-Steuerung beenden, bevor der Assistent schließt

        if (_cachedSnapshot is null)
        {
            CloseWizard();
            return;
        }

        AppConfig draft = BuildDraftConfig(_cachedSnapshot.Config);

        // Profilkurven nur den steuerbaren Lüftern zuordnen - read-only Kanäle würden sonst je Tick nur
        // eine erfolglose SetPwm-Aktion erzeugen. So bekommen auch ohne Kalibrierung alle steuerbaren Lüfter
        // eine Zuordnung (Quelle ist die Live-Discovery, nicht die ggf. leere Config).
        var controllableIds = ControllableFans.Select(f => f.FanId).ToHashSet(StringComparer.Ordinal);

        // Anzeigenamen lokalisiert durchreichen - die persistierten Profil-/Kurven-Namen folgen der
        // UI-Sprache beim Onboarding (Core bleibt sprachneutral).
        string silentName = Localizer.Instance["OnboardingCtrl.ProfileSilentName"];
        string balancedName = Localizer.Instance["OnboardingCtrl.ProfileBalancedName"];
        string performanceName = Localizer.Instance["OnboardingCtrl.ProfilePerformanceName"];

        IReadOnlyList<Profile> profiles;
        string? primarySensorName = null;
        // Recompute the gate here - Finish must not depend on the profile step having been visited.
        bool useAirflow = ComputeUseAirflow();
        if (useAirflow)
        {
            // Same guard as the fallback branch: with every temperature sensor hidden the role curves
            // would get empty source lists and silently regulate nothing (per-tick NaN skip).
            if (!TemperatureSensors.Any(s => s.Visible))
            {
                StatusMessage = Localizer.Instance["OnboardingCtrl.NoTemperatureSensor"];
                return;
            }

            // Airflow mode: all three profiles carry role curves (CPU/GPU/intake/exhaust) in the
            // matching aggressiveness variant; sensor sources come from the Core heuristic, so no
            // primary sensor is required.
            profiles = AirflowTuneService.BuildProfiles(
                draft, controllableIds, AirflowText.CurveNames(),
                silentName, balancedName, performanceName, SensorOption.Labels(TemperatureSensors));
        }
        else
        {
            string? primarySensorId = SelectedPrimarySensor?.Id
                ?? SelectPrimarySensorId(TemperatureSensors, _cachedSnapshot.Sensors);

            if (string.IsNullOrEmpty(primarySensorId))
            {
                StatusMessage = Localizer.Instance["OnboardingCtrl.NoTemperatureSensor"];
                return;
            }

            primarySensorName = TemperatureSensors.FirstOrDefault(s => s.Id == primarySensorId)?.Name ?? primarySensorId;
            var controllableFans = draft.Fans.Where(f => controllableIds.Contains(f.FanId)).ToList();
            profiles = DefaultProfiles.Build(
                controllableFans, primarySensorId!,
                silentName, balancedName, performanceName);
        }

        AppConfig withProfiles = draft with
        {
            Profiles = profiles.ToList(),
            ActiveProfileId = SelectedProfileId,
            OnboardingCompleted = true,
        };

        AppConfig result = ProfileService.Apply(withProfiles, SelectedProfileId);

        // Finish replaces the whole curve set, so any AssignedCurveId the profile assignments did not
        // rewrite (hidden fans, read-only channels) may now dangle. Null it as config hygiene - an
        // unassigned fan is a regular state (hardware auto). The ControlLoop independently treats a
        // dangling id like null at runtime; this pass keeps the persisted config honest.
        var curveIds = result.Curves.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        result = result with
        {
            Fans = result.Fans
                .Select(f => f.AssignedCurveId is { } id && !curveIds.Contains(id)
                    ? f with { AssignedCurveId = null }
                    : f)
                .ToList(),
        };

        // Latch BEFORE the send: closing the window mid-send runs OnClosing→Skip, which would
        // otherwise race a profile-less (OnboardingCompleted-only) config against the profile config
        // sent here. On the Done page the latch then also suppresses Skip's resend.
        _closeSent = true;
        bool sent = await _sendConfig(result);
        if (!sent)
        {
            _closeSent = false; // failed save: re-arm so Skip / window close can still persist OnboardingCompleted
            StatusMessage = Localizer.Instance["OnboardingCtrl.SaveFailed"];
            return;
        }

        PopulateDoneSummary(useAirflow, primarySensorName);
        CurrentStep = OnboardingStep.Done;
    }

    /// <summary>Closes the wizard from the Done page - the config is already saved, nothing to send.</summary>
    [RelayCommand]
    private void Close() => CloseWizard();

    /// <summary>
    /// Builds the config draft the wizard persists BEFORE profiles are attached: fans from the live
    /// discovery with chosen positions and numbered default names (hidden config fans re-merged
    /// verbatim - no wizard row, no data loss) plus the sensor visibility. Shared by the profile-step
    /// preview and Finish so both operate on identical data.
    /// </summary>
    private AppConfig BuildDraftConfig(AppConfig baseConfig)
    {
        // Lüfter aus der Live-Discovery (nicht nur aus der persistierten Config - die ist bei übersprungener
        // Kalibrierung leer): bestehende Einträge je Id behalten (Kalibrierung/PWM-Grenzen bleiben erhalten),
        // gewählte Position einsetzen. ALLE Lüfter werden persistiert (auch read-only) - die Position zählt in
        // der Airflow-Druckbilanz.
        var baseFanById = baseConfig.Fans.ToDictionary(f => f.FanId);
        var fans = Fans
            .Select(row =>
            {
                FanConfig baseFan = baseFanById.TryGetValue(row.FanId, out FanConfig? existing)
                    ? existing
                    : new FanConfig { FanId = row.FanId };
                // The name the step showed: the one typed, or „Lüfter N" for an empty field instead of the hardware path
                // (that stays visible under the name in the settings).
                return baseFan with { Name = row.DisplayName, Location = row.Location.Value };
            })
            .ToList();

        // Hidden fans have no wizard row - carry their config over verbatim (calibration, PWM limits,
        // name, location, Hidden). Dropping them here would be silent data loss.
        var rowIds = fans.Select(f => f.FanId).ToHashSet(StringComparer.Ordinal);
        fans.AddRange(baseConfig.Fans.Where(f => f.Hidden && !rowIds.Contains(f.FanId)));

        // Sensor-Sichtbarkeit (Temperatursensoren - gleiches Scope wie der Geräte-Tab). Every sensor also gets the
        // group the devices step showed it in: its own, or the suggested one. Part of the draft, not only of Finish,
        // so the airflow preview already sees them - its CPU/GPU detection reads the group too.
        SensorGroupPlan groups = PlanSensorGroups(baseConfig.SensorGroups);
        var sensors = TemperatureSensors.Select(s => s.ToConfig() with { Group = groups.GroupById[s.Id] }).ToList();

        // Same no-data-loss rule as for the hidden fans above: config entries whose sensor is not in
        // the current discovery (unplugged device, EIO channel) have no wizard row - carry them over
        // verbatim instead of silently dropping name/group/visibility.
        var sensorIds = sensors.Select(s => s.SensorId).ToHashSet(StringComparer.Ordinal);
        sensors.AddRange(baseConfig.Sensors.Where(s => !sensorIds.Contains(s.SensorId)));

        // Over ConfigLimits.MaxSensorGroups the daemon would refuse the whole config; the names past it are new
        // ones (the configured list is within the limit), and their sensors still form the group without them.
        return baseConfig with
        {
            Fans = fans,
            Sensors = sensors,
            SensorGroups = groups.Names.Take(ConfigLimits.MaxSensorGroups).ToList(),
            CaseType = CaseType,
        };
    }

    /// <summary>Airflow gate: at least one controllable fan has a role-specific mounting position.</summary>
    private bool ComputeUseAirflow() =>
        ControllableFans.Any(f => AirflowTuneService.HasRoleSpecificCurve(f.Location.Value));

    /// <summary>
    /// Re-evaluates the airflow gate and fills the profile-step preview (pressure balance, hints,
    /// per-fan role/curve summary). Uses the balanced variant for the texts - ids, roles, and sensor
    /// sources are identical across aggressiveness variants.
    /// </summary>
    private void RefreshProfilePreview()
    {
        UseAirflowProfiles = _cachedSnapshot is not null && ComputeUseAirflow();
        if (!UseAirflowProfiles)
        {
            AirflowSummary.Clear();
            AirflowHints.Clear();
            AirflowPressureText = "";
            return;
        }

        AirflowTuneResult result = AirflowTuneService.Analyze(
            BuildDraftConfig(_cachedSnapshot!.Config), AirflowText.CurveNames(), SensorOption.Labels(TemperatureSensors));
        Dictionary<string, string> curveNames = result.SuggestedCurves.ToDictionary(c => c.Id, c => c.Name);
        var controllableIds = ControllableFans.Select(f => f.FanId).ToHashSet(StringComparer.Ordinal);

        AirflowSummary.Clear();
        foreach (AirflowFanSuggestion s in result.Fans)
        {
            // Only controllable fans get an assignment - don't promise a curve to read-only channels.
            if (!controllableIds.Contains(s.FanId))
                continue;
            string fanName = Fans.FirstOrDefault(f => f.FanId == s.FanId)?.DisplayName ?? s.FanId;
            string curveName = s.SuggestedCurveId is { } id && curveNames.TryGetValue(id, out string? n)
                ? n
                : Localizer.Instance["CurveEditorCtrl.NoCurveHardwareAuto"];
            AirflowSummary.Add(new AirflowSuggestionRow(
                s.FanId, fanName, FanLocationOption.For(s.Location).Display, curveName,
                AirflowText.DescribeReason(s, curveName)));
        }

        // The verdict line already says what these would repeat (only one side, negative pressure); without case fans it
        // only says that there is no balance, the hint says why.
        AirflowHints.Clear();
        foreach (AirflowHint hint in result.Hints.Where(h => h is not (AirflowHint.NoIntakeFan
                     or AirflowHint.NoExhaustFan or AirflowHint.NegativePressure)))
            AirflowHints.Add(AirflowText.DescribeHint(hint));

        AirflowPressureText = Localizer.Instance.Format("Onboarding.PressureVerdict", AirflowText.DescribeVerdict(result));
    }

    /// <summary>
    /// Schließt den Assistenten, ohne Profile anzulegen. Sendet nur <c>OnboardingCompleted = true</c>,
    /// damit der Daemon nicht erneut den Assistenten triggert. Idempotent gegen Doppelaufruf: der Latch
    /// unterdrückt nur das Senden - <see cref="CloseWizard"/> läuft immer, damit auch ein Fenster-Schließen
    /// nach Finish (Done-Seite) das Localizer-Abo löst und den Besitzer zurücksetzt.
    /// </summary>
    [RelayCommand]
    private async Task Skip()
    {
        if (!_closeSent)
        {
            _closeSent = true;

            RevertAllManual(); // eine offene temporäre Manuell-Steuerung beenden, bevor der Assistent schließt

            if (IsCalibrating)
                await AbortCalibrationAsync();

            if (_cachedSnapshot is not null)
            {
                AppConfig cfg = _cachedSnapshot.Config with { OnboardingCompleted = true };
                await _sendConfig(cfg); // Fehler ignorieren - kein Crash, nur kein persistierter Status
            }
        }

        CloseWizard();
    }

    // --- Befüll-Logik -----------------------------------------------------------------------------

    private void Populate(MonitorSnapshot snapshot)
    {
        CaseType = snapshot.Config.CaseType;
        // Position-/Sichtbarkeits-Defaults aus der persistierten Config; bei Erststart leer → Defaults greifen.
        var fanCfgById = snapshot.Config.Fans.ToDictionary(f => f.FanId);
        var sensorCfgById = snapshot.Config.Sensors.ToDictionary(s => s.SensorId);

        // Alle Lüfter für die (optionale) Positionswahl; die steuerbare Teilmenge teilt sich die Instanz mit
        // der Kalibrier-Liste (eine Wahrheit pro Lüfter).
        Fans.Clear();
        ControllableFans.Clear();
        foreach (FanReading fan in snapshot.Fans)
        {
            fanCfgById.TryGetValue(fan.Id, out FanConfig? cfg);
            // Hidden fans are "not present" for the user: no position row, no calibration. Their config
            // is carried over verbatim in Finish (re-merge there), so nothing is lost.
            if (cfg?.Hidden == true)
                continue;
            FanLocation loc = cfg?.Location ?? FanLocation.Unspecified;
            var row = new OnboardingFanRow(fan.Id, cfg?.Name ?? "", loc, fan.CanControl, _sendIdentify, _sendManual, _sendAuto);
            // Lifetime equals the controller's (_populated latch) - no unsubscribe needed.
            row.PropertyChanged += OnFanRowChanged;
            Fans.Add(row);
            if (fan.CanControl)
                ControllableFans.Add(row);
        }
        HasControllableFans = ControllableFans.Count > 0;
        // Numbered over every detected fan in its order, hidden ones too - as the settings and the overview count.
        _detectedFans = snapshot.Fans
            .Select(f => (f.Id, fanCfgById.TryGetValue(f.Id, out FanConfig? cfg) ? cfg.Name ?? "" : ""))
            .ToList();
        var detected = snapshot.Fans.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        _undetectedFanNames = snapshot.Config.Fans.Where(f => !detected.Contains(f.FanId)).Select(f => f.Name).ToList();
        RefreshDefaultFanNames();

        // Temperatursensoren: speisen Primärwahl und Sichtbarkeits-Liste. Sichtbarkeit aus der Config, sonst
        // standardmäßig ausgeblendet, wenn der Sensor keinen Messwert liefert (NaN - z. B. EIO-Kanäle).
        TemperatureSensors.Clear();
        foreach (SensorReading sensor in snapshot.Sensors.Where(s => s.Kind == SensorKind.Temperature))
        {
            bool visible = sensorCfgById.TryGetValue(sensor.Id, out SensorConfig? sc)
                ? !sc.Hidden
                : !double.IsNaN(sensor.Value);
            var opt = new SensorOption(sensor.Id, sensor.Name, visible, sc?.Group, sensor.Unit, sensor.Label);
            opt.SetLive(sensor.Value);
            // A visibility change recounts the sensor's group in the devices step.
            // Lifetime equals the controller's (_populated latch) - no unsubscribe needed.
            opt.PropertyChanged += OnSensorRowChanged;
            TemperatureSensors.Add(opt);
        }
        RebuildSensorGroups(snapshot.Config.SensorGroups);

        string? bestId = SelectPrimarySensorId(TemperatureSensors, snapshot.Sensors);
        if (bestId is not null)
            SelectedPrimarySensor = TemperatureSensors.FirstOrDefault(s => s.Id == bestId);
    }

    // Every detected fan in its order with its saved name - a hidden one has no row, but takes a number all the same.
    private IReadOnlyList<(string Id, string Name)> _detectedFans = [];

    // Own names of the config's fans that are not detected right now - „Lüfter N" must not reuse them.
    private IReadOnlyList<string> _undetectedFanNames = [];

    /// <summary>
    /// Gives every row its „Lüfter N" (<see cref="DefaultFanNames"/>) - placeholder and name of an empty field. Counted
    /// like the settings and the overview (<see cref="DefaultFanNames.ApplyTo"/>): over every detected fan, so a hidden fan
    /// without a name keeps the number it has there.
    /// </summary>
    private void RefreshDefaultFanNames()
    {
        var rows = new Dictionary<string, OnboardingFanRow>(StringComparer.Ordinal);
        foreach (OnboardingFanRow row in Fans)
            rows.TryAdd(row.FanId, row);
        IReadOnlyList<string> defaults = DefaultFanNames.For(
            _detectedFans.Select(f => rows.TryGetValue(f.Id, out OnboardingFanRow? row) ? row.Name : f.Name).ToList(),
            _undetectedFanNames);
        for (int i = 0; i < _detectedFans.Count; i++)
            if (rows.TryGetValue(_detectedFans[i].Id, out OnboardingFanRow? row))
                row.DefaultName = defaults[i];
    }

    // A name typed in one row can take the number another row's placeholder showed.
    private void OnFanRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingFanRow.Name))
            RefreshDefaultFanNames();
    }

    private void OnSensorRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SensorOption.Visible) && sender is SensorOption sensor)
            RefreshSensorGroupCount(sensor);
    }

    /// <summary>
    /// Heuristik zur Auswahl des primären Temperatursensors:
    /// (1) Erster, dessen Name oder Id auf CPU-Schlüsselwörter passt;
    /// (2) Der mit der aktuell höchsten Temperatur;
    /// (3) Der erste in der Liste. Leere Liste → null.
    /// </summary>
    public static string? SelectPrimarySensorId(
        IEnumerable<SensorOption> sensors,
        IReadOnlyList<SensorReading> readings)
    {
        var all = sensors.ToList();
        // Hidden sensors are "not present" for the user - consider them only if EVERYTHING is hidden,
        // so Finish cannot dead-end without a primary sensor.
        List<SensorOption> options = all.Where(s => s.Visible).ToList();
        if (options.Count == 0)
            options = all;
        if (options.Count == 0)
            return null;

        // (1) CPU-Namens-Match
        var cpuPattern = new Regex(@"cpu|tctl|tdie|package|core", RegexOptions.IgnoreCase);
        SensorOption? cpuMatch = options.FirstOrDefault(
            s => cpuPattern.IsMatch(s.Name) || cpuPattern.IsMatch(s.Id));
        if (cpuMatch is not null)
            return cpuMatch.Id;

        // (2) Wärmster Sensor
        if (readings.Count > 0)
        {
            string? hottestId = readings
                .Where(r => r.Kind == SensorKind.Temperature && options.Any(o => o.Id == r.Id))
                .OrderByDescending(r => r.Value)
                .Select(r => r.Id)
                .FirstOrDefault();
            if (hottestId is not null)
                return hottestId;
        }

        // (3) Erster
        return options[0].Id;
    }
}
