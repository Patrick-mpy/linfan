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
/// Testbar via Delegates — kein direkter IpcLiveMonitor-Typ.
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

    [ObservableProperty] private OnboardingStep _currentStep = OnboardingStep.Welcome;
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

    /// <summary>Steuerbare Lüfter — Grundlage der Kalibrierung. Teilmenge von <see cref="Fans"/> (gleiche Instanzen).</summary>
    public ObservableCollection<OnboardingFanRow> ControllableFans { get; } = new();

    /// <summary>Alle erkannten Lüfter — Grundlage der (optionalen) Positions-Auswahl im Geräte-Schritt.</summary>
    public ObservableCollection<OnboardingFanRow> Fans { get; } = new();

    /// <summary>
    /// Temperatursensoren — speisen sowohl die Primärsensor-Wahl als auch die Sichtbarkeits-Auswahl
    /// im Geräte-Schritt (gleiches Scope wie der Geräte-Tab, der nur Temperatursensoren persistiert).
    /// </summary>
    public ObservableCollection<SensorOption> TemperatureSensors { get; } = new();

    [ObservableProperty] private SensorOption? _selectedPrimarySensor;
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

        // Bei Sprachwechsel die Profil-Optionen neu aufbauen: die ListBox bindet DisplayName/Description
        // direkt, daher müssen frische Instanzen mit übersetzten Texten nachgereicht werden. Die Auswahl
        // wird über die Id rekonstruiert (nicht die alte Instanz gehalten). CalibrationHeadline ist ein
        // berechneter Localizer-Lookup und muss ebenfalls neu gelesen werden.
        // Benannter Handler + Unsubscribe beim Schließen (CloseWizard): dieser Controller wird pro
        // Onboarding-Durchlauf neu erzeugt (Erststart und „Einstellungen → Onboarding"), ein anonymes
        // Dauer-Abo am app-lebenslangen Localizer-Singleton würde jede alte Instanz am Leben halten.
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        ProfileOptions = BuildProfileOptions();
        OnPropertyChanged(nameof(ProfileOptions));
        SelectedProfile = ProfileOptions.FirstOrDefault(p => p.Id == SelectedProfileId);
        OnPropertyChanged(nameof(CalibrationHeadline));
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
        _cachedSnapshot = snapshot;

        if (!_populated && (snapshot.Fans.Count > 0 || snapshot.Sensors.Count > 0))
        {
            _populated = true;
            Populate(snapshot);
        }

        // Live-Drehzahl je Lüfter in den Manuell-Slider spiegeln (reine Anzeige; läuft auch, während das
        // Positions-Modal offen ist, da es denselben Row teilt).
        var rpmById = snapshot.Fans.ToDictionary(f => f.Id, f => f.Rpm);
        foreach (OnboardingFanRow fan in Fans)
            fan.SetLiveRpm(rpmById.TryGetValue(fan.FanId, out double? r) ? r : null);

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
            string fanName = row?.Name ?? fanId;
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

                // Aktuellen Lüfter als abgeschlossen werten — Gesamtfortschritt auf den Anteil der fertigen zählen.
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
    partial void OnCurrentStepChanged(OnboardingStep value) => RevertAllManual();

    private void RevertAllManual()
    {
        foreach (OnboardingFanRow fan in Fans)
            fan.Manual.Revert();
    }

    [RelayCommand]
    private void Next()
    {
        CurrentStep = CurrentStep switch
        {
            OnboardingStep.Welcome => OnboardingStep.Calibration,
            OnboardingStep.Calibration => OnboardingStep.Devices,
            OnboardingStep.Devices => OnboardingStep.ChooseProfile,
            OnboardingStep.ChooseProfile => OnboardingStep.Done,
            _ => CurrentStep,
        };
    }

    [RelayCommand]
    private async Task Back()
    {
        // Zurück aus dem Kalibrierschritt bricht eine laufende Kalibrierung ab
        if (CurrentStep == OnboardingStep.Calibration && IsCalibrating)
            await AbortCalibrationAsync();

        CurrentStep = CurrentStep switch
        {
            OnboardingStep.Calibration => OnboardingStep.Welcome,
            OnboardingStep.Devices => OnboardingStep.Calibration,
            OnboardingStep.ChooseProfile => OnboardingStep.Devices,
            OnboardingStep.Done => OnboardingStep.ChooseProfile,
            _ => CurrentStep,
        };
    }

    // --- Kalibrierung -----------------------------------------------------------------------------

    [RelayCommand]
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
                CalibrationFanName = fan.Name;
                CalibrationFanProgress = 0;

                // Phase 1: Tacho automatisch koppeln (Vorstufe — die Kalibrierung braucht den Tacho). Ist der
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

                // Phase 2: Kalibrieren (nutzt den gerade gekoppelten Tacho automatisch — daemon-seitig via RpmSource).
                fan.CalibrationState = OnboardingCalibrationState.Running;
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CalibrationStarting", i + 1, total);

                _calibrationDoneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitingForFanId = fan.FanId;

                await _sendStartCalibration(fan.FanId);

                // Warten bis Done – mit defensivem Timeout (60 s)
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
    /// (Kalibrierung kann folgen); <c>false</c> bei kein Tacho / nicht eindeutig / Fehler / Timeout — dann ist
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
            return true; // weiter zur Kalibrierung — der Daemon persistiert die Zuordnung selbst

        // Nicht eindeutig gekoppelt (kein Signal / mehrdeutig — z. B. zwei Tacho-Kanäle für denselben Lüfter,
        // die gemeinsam ansteigen). Dreht der Lüfter aber messbar, existiert bereits ein brauchbarer Tacho
        // (Backend-Heuristik oder frühere Zuordnung, vom Daemon bei nicht-eindeutigem Ergebnis unangetastet):
        // dann NICHT überspringen, sondern damit kalibrieren — die Kopplung soll eine funktionierende Paarung
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
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CouplingNoTacho", fan.Name);
                break;

            case TachMappingPhase.Ambiguous:
                fan.CalibrationState = OnboardingCalibrationState.Ambiguous;
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CouplingAmbiguous", fan.Name);
                break;

            default: // Failed (Übertemp / kein Watchdog / Unknown) oder unerwartet null
                fan.CalibrationState = OnboardingCalibrationState.Failed;
                string reason = result?.FailReason is { } r
                    ? IpcStatusText.Fail(r, result.OverTempC, result.OverLimitC)
                    : "";
                CalibrationProgress = Localizer.Instance.Format("OnboardingCtrl.CouplingFailed", fan.Name, reason);
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

        AppConfig baseConfig = _cachedSnapshot.Config;
        string? primarySensorId = SelectedPrimarySensor?.Id
            ?? SelectPrimarySensorId(TemperatureSensors, _cachedSnapshot.Sensors);

        if (string.IsNullOrEmpty(primarySensorId))
        {
            StatusMessage = Localizer.Instance["OnboardingCtrl.NoTemperatureSensor"];
            return;
        }

        // Lüfter aus der Live-Discovery (nicht nur aus der persistierten Config — die ist bei übersprungener
        // Kalibrierung leer): bestehende Einträge je Id behalten (Kalibrierung/PWM-Grenzen bleiben erhalten),
        // gewählte Position einsetzen. ALLE Lüfter werden persistiert (auch read-only) — die Position zählt in
        // der Airflow-Druckbilanz.
        var baseFanById = baseConfig.Fans.ToDictionary(f => f.FanId);
        var fans = Fans
            .Select((row, i) =>
            {
                FanConfig baseFan = baseFanById.TryGetValue(row.FanId, out FanConfig? existing)
                    ? existing
                    : new FanConfig { FanId = row.FanId };
                // Erststart: noch kein eigener Name → durchnummerieren statt Hardware-Pfad zeigen
                // (der Pfad bleibt als Unterzeile im Geräte-Tab/Tooltip sichtbar). Ein bereits
                // gesetzter Name bleibt erhalten.
                string name = string.IsNullOrWhiteSpace(baseFan.Name) ? Localizer.Instance.Format("OnboardingCtrl.FanDefaultName", i + 1) : baseFan.Name;
                return baseFan with { Name = name, Location = row.Location.Value };
            })
            .ToList();

        // Sensor-Sichtbarkeit (Temperatursensoren — gleiches Scope wie der Geräte-Tab).
        var sensors = TemperatureSensors.Select(s => s.ToConfig()).ToList();

        // Profilkurven nur den steuerbaren Lüftern zuordnen — read-only Kanäle würden sonst je Tick nur
        // eine erfolglose SetPwm-Aktion erzeugen. So bekommen auch ohne Kalibrierung alle steuerbaren Lüfter
        // eine Zuordnung (Quelle ist die Live-Discovery, nicht die ggf. leere Config).
        var controllableIds = ControllableFans.Select(f => f.FanId).ToHashSet(StringComparer.Ordinal);
        var controllableFans = fans.Where(f => controllableIds.Contains(f.FanId)).ToList();
        // Anzeigenamen lokalisiert durchreichen — die persistierten Profil-/Kurven-Namen folgen der
        // UI-Sprache beim Onboarding (Core bleibt sprachneutral).
        var profiles = DefaultProfiles.Build(
            controllableFans,
            primarySensorId!,
            silentName: Localizer.Instance["OnboardingCtrl.ProfileSilentName"],
            balancedName: Localizer.Instance["OnboardingCtrl.ProfileBalancedName"],
            performanceName: Localizer.Instance["OnboardingCtrl.ProfilePerformanceName"]);
        AppConfig withProfiles = baseConfig with
        {
            Fans = fans,
            Sensors = sensors,
            Profiles = profiles.ToList(),
            ActiveProfileId = SelectedProfileId,
            OnboardingCompleted = true,
        };

        AppConfig result = ProfileService.Apply(withProfiles, SelectedProfileId);

        bool sent = await _sendConfig(result);
        if (!sent)
        {
            StatusMessage = Localizer.Instance["OnboardingCtrl.SaveFailed"];
            return;
        }

        // Latch setzen, BEVOR geschlossen wird: das Schließen löst OnClosing→Skip aus, das sonst die
        // gerade gesendete Profil-Config mit einer profillosen (nur OnboardingCompleted) überschreiben würde.
        _closeSent = true;
        CloseWizard();
    }

    /// <summary>
    /// Schließt den Assistenten, ohne Profile anzulegen. Sendet nur <c>OnboardingCompleted = true</c>,
    /// damit der Daemon nicht erneut den Assistenten triggert. Idempotent gegen Doppelaufruf.
    /// </summary>
    [RelayCommand]
    private async Task Skip()
    {
        if (_closeSent)
            return;
        _closeSent = true;

        RevertAllManual(); // eine offene temporäre Manuell-Steuerung beenden, bevor der Assistent schließt

        if (IsCalibrating)
            await AbortCalibrationAsync();

        if (_cachedSnapshot is not null)
        {
            AppConfig cfg = _cachedSnapshot.Config with { OnboardingCompleted = true };
            await _sendConfig(cfg); // Fehler ignorieren — kein Crash, nur kein persistierter Status
        }

        CloseWizard();
    }

    // --- Befüll-Logik -----------------------------------------------------------------------------

    private void Populate(MonitorSnapshot snapshot)
    {
        // Position-/Sichtbarkeits-Defaults aus der persistierten Config; bei Erststart leer → Defaults greifen.
        var fanLocationById = snapshot.Config.Fans.ToDictionary(f => f.FanId, f => f.Location);
        var sensorCfgById = snapshot.Config.Sensors.ToDictionary(s => s.SensorId);

        // Alle Lüfter für die (optionale) Positionswahl; die steuerbare Teilmenge teilt sich die Instanz mit
        // der Kalibrier-Liste (eine Wahrheit pro Lüfter).
        Fans.Clear();
        ControllableFans.Clear();
        foreach (FanReading fan in snapshot.Fans)
        {
            FanLocation loc = fanLocationById.TryGetValue(fan.Id, out FanLocation l) ? l : FanLocation.Unspecified;
            var row = new OnboardingFanRow(fan.Id, fan.Name, loc, fan.CanControl, _sendIdentify, _sendManual, _sendAuto);
            Fans.Add(row);
            if (fan.CanControl)
                ControllableFans.Add(row);
        }
        HasControllableFans = ControllableFans.Count > 0;

        // Temperatursensoren: speisen Primärwahl und Sichtbarkeits-Liste. Sichtbarkeit aus der Config, sonst
        // standardmäßig ausgeblendet, wenn der Sensor keinen Messwert liefert (NaN — z. B. EIO-Kanäle).
        TemperatureSensors.Clear();
        foreach (SensorReading sensor in snapshot.Sensors.Where(s => s.Kind == SensorKind.Temperature))
        {
            bool visible = sensorCfgById.TryGetValue(sensor.Id, out SensorConfig? sc)
                ? !sc.Hidden
                : !double.IsNaN(sensor.Value);
            var opt = new SensorOption(sensor.Id, sensor.Name, visible, sc?.Group, sensor.Unit);
            opt.SetLive(sensor.Value);
            TemperatureSensors.Add(opt);
        }

        string? bestId = SelectPrimarySensorId(TemperatureSensors, snapshot.Sensors);
        if (bestId is not null)
            SelectedPrimarySensor = TemperatureSensors.FirstOrDefault(s => s.Id == bestId);
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
        var options = sensors.ToList();
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
