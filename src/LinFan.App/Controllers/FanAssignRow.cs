// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// Zuordnung einer Kurve zu einem Lüfter (oder keine), plus Einbau-Position und Gruppe.
/// PWM-Grenzen/Kalibrierung bleiben über <c>_base</c> erhalten. Steuerbare Lüfter lassen sich von hier
/// aus kalibrieren (der Befehl läuft über den injizierten Callback an den Daemon).
/// </summary>
public partial class FanAssignRow : ObservableObject
{
    private readonly FanConfig _base;
    private readonly Func<string, Task>? _sendCalibrate;
    private readonly Func<string, Task>? _sendIdentify;
    private readonly Func<string, Task>? _sendTachMapping;   // StartTachMapping(fanId)
    private readonly Func<Task>? _cancelTachMapping;         // CancelTachMapping()
    private readonly Func<string, string?, Task>? _sendSetTach; // SetFanTachometer(fanId, sensorId|null)
    private readonly TimeSpan _calibrationHold;
    private CancellationTokenSource? _holdCts; // hält die finale Done/Error-Meldung; gecancelt bei neuem Lauf
    // Eigenes Halten für die Identify-Meldung — unabhängig vom Kalibrier-Halten (kein gegenseitiges Überschreiben).
    private CancellationTokenSource? _identifyHoldCts;
    // Eigenes Halten für die Kopplungs-Meldung — unabhängig von Kalibrier-/Identify-Halten.
    private CancellationTokenSource? _tachHoldCts;
    // Zuletzt vom Daemon gespiegelte Tacho-Zuordnung; verhindert, dass ein Snapshot mit unverändertem
    // Wert eine gerade getätigte (noch nicht bestätigte) Nutzer-Auswahl im Dropdown zurücksetzt.
    private string? _lastDaemonRpmSource;
    private bool _applyingRpmSource; // true, während das Dropdown programmatisch aus dem Snapshot gesetzt wird
    // Zuletzt vom Daemon gemeldeter Anlaufpunkt (MinPwm) — gleiches Muster wie _lastDaemonRpmSource: nur eine
    // echte Änderung dort (Kalibrier-Ergebnis) zieht den Wert nach, ein unveränderter Snapshot lässt eine
    // gerade getippte, noch nicht gespeicherte Eingabe in Ruhe.
    private int _lastDaemonMinPwm;
    // Hardware label as resolved by the daemon (from the live fan list). Shown when the fan carries no own
    // name — and for exactly that reason must never be written back as one (see ToConfig).
    private readonly string _hardwareLabel;

    public string FanId => _base.FanId;

    /// <summary>Hardware-Name (read-only, als Hinweis neben dem editierbaren Namen).</summary>
    public string HardwareName => _base.FanId;

    /// <summary>
    /// Placeholder of the name field: the hardware label as resolved by the daemon — exactly what is shown
    /// everywhere while the fan has no own name. A placeholder rather than a value, so an untouched field
    /// stays empty instead of being persisted as a user-defined name. Falls back to the generic hint when
    /// there is no label.
    /// </summary>
    public string NamePlaceholder => string.IsNullOrWhiteSpace(_hardwareLabel)
        ? Localizer.Instance["MainWindow.NameRequired"]
        : _hardwareLabel;

    /// <summary>Ob der Lüfter steuerbar ist (nur dann ist Kalibrierung möglich). Laufzeit-Info aus dem Snapshot.</summary>
    public bool CanControl { get; }

    /// <summary>Temporäre Manuell-Steuerung (Slider in der Erweitert-Sektion) — erleichtert die Zuordnung;
    /// beim Einklappen der Sektion automatisch zurück auf Kurve/Hardware-Auto.</summary>
    public ManualControl Manual { get; }

    /// <summary>Verfügbare Kurven (gemeinsame Liste des Controllers) für den ComboBox.</summary>
    public ObservableCollection<CurveEditRow> AvailableCurves { get; }

    /// <summary>Verfügbare Drehzahl-Sensoren fürs manuelle Tacho-Dropdown (geteilte Controller-Liste; „keiner"-Eintrag zuerst).</summary>
    public ObservableCollection<TachSensorOption> AvailableTachSensors { get; }

    /// <summary>Auswählbare Einbau-Positionen.</summary>
    public IReadOnlyList<FanLocationOption> Locations => FanLocationOption.All;

    /// <summary>Gruppenschlüssel für die Auto-Gruppierung in der Kurven-Zuordnung (Position › „Ungruppiert") — wie im Dashboard.</summary>
    public string GroupKey => Location.Value != FanLocation.Unspecified
        ? FanLocationOption.GroupNameFor(Location.Value)
        : FanGroup.Ungrouped;

    [ObservableProperty] private string _name;
    [ObservableProperty] private CurveEditRow? _selected;
    [ObservableProperty] private FanLocationOption _location;
    [ObservableProperty] private bool _visible;

    // Wahrheit bleibt der rohe PWM-Wert (0–255, so wird gespeichert); MinPercent/MaxPercent sind nur die
    // Anzeige in Prozent (konsistent mit Dashboard-Slider & Kalibrierung). Beide Felder benachrichtigen die
    // jeweilige Prozent-Property, damit das nachgezogene Min/Max in der UI sofort mitläuft.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinPercent))]
    private int _minPwm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxPercent))]
    private int _maxPwm;

    /// <summary>PWM-Untergrenze als Prozent (0–100). Setzen rundet auf den nächsten rohen PWM-Wert; ein
    /// unangetasteter Wert bleibt beim Speichern exakt der geladene Hardware-Wert (kein stiller Drift).</summary>
    public int MinPercent
    {
        get => PwmScale.ToPercent((byte)Math.Clamp(MinPwm, 0, 255));
        set => MinPwm = PwmScale.ToPwm(value);
    }

    /// <summary>PWM-Obergrenze als Prozent (0–100). Siehe <see cref="MinPercent"/>.</summary>
    public int MaxPercent
    {
        get => PwmScale.ToPercent((byte)Math.Clamp(MaxPwm, 0, 255));
        set => MaxPwm = PwmScale.ToPwm(value);
    }

    /// <summary>Läuft für diesen Lüfter gerade eine Kalibrierung? (Button deaktivieren, Fortschritt zeigen.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate), nameof(CanIdentify), nameof(CanCoupleSensor))]
    private bool _isCalibrating;

    /// <summary>Kurzer Inline-Fortschrittstext während/nach der Kalibrierung dieses Lüfters.</summary>
    [ObservableProperty] private string _calibrationProgress = "";

    /// <summary>Fortschritt der laufenden Kalibrierung dieses Lüfters in Prozent (0..100), aus dem PWM-Rampenwert.</summary>
    [ObservableProperty] private double _calibrationFanProgress;

    /// <summary>True, wenn die letzte Kalibrierung dieses Lüfters fehlschlug/abgebrochen wurde (Alert-Indikator).</summary>
    [ObservableProperty] private bool _calibrationFailed;

    /// <summary>Läuft für diesen Lüfter gerade die Identifikation? (Button deaktivieren, Fortschritt zeigen.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate), nameof(CanIdentify), nameof(CanCoupleSensor))]
    private bool _isIdentifying;

    /// <summary>Kurzer Inline-Fortschrittstext während/nach der Identifikation dieses Lüfters.</summary>
    [ObservableProperty] private string _identifyProgress = "";

    /// <summary>Läuft für diesen Lüfter gerade die automatische Tacho-Kopplung? (Button deaktivieren, Fortschritt/Abbruch zeigen.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate), nameof(CanIdentify), nameof(CanCoupleSensor))]
    private bool _isTachMapping;

    /// <summary>Kurzer Inline-Fortschritts-/Ergebnistext während/nach der Tacho-Kopplung dieses Lüfters.</summary>
    [ObservableProperty] private string _tachMappingProgress = "";

    /// <summary>Aktuell im Dropdown gewählter Drehzahl-Sensor (bzw. der „keiner"-Eintrag). Zwei-Wege-Ziel des ComboBox.</summary>
    [ObservableProperty] private TachSensorOption? _selectedTach;

    /// <summary>Kalibrieren ist nur erlaubt, solange weder Identifikation noch Tacho-Kopplung dieses Lüfters läuft.</summary>
    public bool CanCalibrate => !IsCalibrating && !IsIdentifying && !IsTachMapping;

    /// <summary>Identifizieren ist nur erlaubt, solange weder Kalibrierung noch Tacho-Kopplung dieses Lüfters läuft.</summary>
    public bool CanIdentify => !IsCalibrating && !IsIdentifying && !IsTachMapping;

    /// <summary>Sensor koppeln ist nur erlaubt, solange weder Kalibrierung noch Identifikation dieses Lüfters läuft (und keine Kopplung).</summary>
    public bool CanCoupleSensor => !IsCalibrating && !IsIdentifying && !IsTachMapping;

    /// <summary>
    /// Spiegelt die letzte automatische Min/Max-Korrektur (Min &gt; Max → der jeweils andere Wert wird
    /// nachgezogen). Leer, sobald eine Änderung ohne Nachziehen passiert. Kein Timer — rein deterministisch.
    /// </summary>
    [ObservableProperty] private string _pwmAdjustHint = "";

    /// <summary>True, während gerade der eine PWM-Wert vom anderen nachgezogen wird — der nachgezogene
    /// Setter soll den Hinweis dann nicht wieder leeren, den der auslösende Setter eben gesetzt hat.</summary>
    private bool _autoAdjusting;

    /// <summary>Formatierte Live-Drehzahl für den Geräte-Tab (reine Anzeige, fließt nicht in die Config).</summary>
    [ObservableProperty] private string _liveRpm = "—";

    /// <summary>Ob die „Erweitert"-Sektion (Grenzwerte + Kalibrierung) aufgeklappt ist — reiner View-Zustand pro Zeile.</summary>
    [ObservableProperty] private bool _showAdvanced;

    /// <summary>True, sobald für diesen Lüfter eine Kalibrierung vorliegt (aus der Config geladen oder in dieser
    /// Sitzung erfolgreich gelaufen). Steuert das „bereits kalibriert"-Badge in der Lüfterzeile.</summary>
    [ObservableProperty] private bool _isCalibrated;

    /// <summary>Tooltip des Kalibrier-Badges — Anlaufpunkt in % (bzw. Hinweis, falls keiner gefunden wurde).</summary>
    [ObservableProperty] private string _calibrationBadgeHint = "";

    /// <param name="calibrationHold">Wie lange die finale Done/Error-Meldung sichtbar bleibt (Default 4 s;
    /// injizierbar, damit Tests eine kurze Dauer setzen können).</param>
    public FanAssignRow(FanConfig baseFan, CurveEditRow? selected, ObservableCollection<CurveEditRow> availableCurves,
                        bool canControl = false, Func<string, Task>? sendCalibrate = null,
                        TimeSpan? calibrationHold = null,
                        Func<string, Task>? sendIdentify = null,
                        Func<string, byte, Task>? sendManual = null, Func<string, Task>? sendAuto = null,
                        Func<string, Task>? sendTachMapping = null, Func<Task>? cancelTachMapping = null,
                        Func<string, string?, Task>? sendSetTach = null,
                        ObservableCollection<TachSensorOption>? availableTachSensors = null,
                        string? hardwareLabel = null)
    {
        _base = baseFan;
        _hardwareLabel = hardwareLabel ?? "";
        _name = baseFan.Name;
        _selected = selected;
        AvailableCurves = availableCurves;
        AvailableTachSensors = availableTachSensors ?? new();
        CanControl = canControl;
        _sendCalibrate = sendCalibrate;
        _sendIdentify = sendIdentify;
        _sendTachMapping = sendTachMapping;
        _cancelTachMapping = cancelTachMapping;
        _sendSetTach = sendSetTach;
        Manual = new ManualControl(baseFan.FanId, canControl, sendManual, sendAuto);
        _calibrationHold = calibrationHold ?? TimeSpan.FromSeconds(4);
        _location = FanLocationOption.For(baseFan.Location);
        _visible = !baseFan.Hidden;
        _minPwm = baseFan.MinPwm;
        _maxPwm = baseFan.MaxPwm;
        _lastDaemonMinPwm = baseFan.MinPwm;

        // Aktuelle Tacho-Zuordnung aus der Config spiegeln (Feld-Zuweisung → kein SetFanTachometer-Command).
        _lastDaemonRpmSource = baseFan.RpmSource;
        _selectedTach = AvailableTachSensors.FirstOrDefault(o => o.Id == baseFan.RpmSource);

        if (baseFan.Calibration is { } cal)
        {
            _isCalibrated = true;
            _calibrationBadgeHint = CalibrationBadge.Hint(cal.StartPwm);
        }
    }

    [RelayCommand]
    private Task Calibrate() => CanControl ? _sendCalibrate?.Invoke(FanId) ?? Task.CompletedTask : Task.CompletedTask;

    [RelayCommand]
    private Task Identify() => CanControl ? _sendIdentify?.Invoke(FanId) ?? Task.CompletedTask : Task.CompletedTask;

    /// <summary>Startet die automatische Tacho-Kopplung (nur für steuerbare Lüfter — sonst kann nichts angetrieben werden).</summary>
    [RelayCommand]
    private Task CoupleSensor() => CanControl ? _sendTachMapping?.Invoke(FanId) ?? Task.CompletedTask : Task.CompletedTask;

    /// <summary>Bricht die laufende automatische Tacho-Kopplung ab (bzw. quittiert deren Abschluss-Status).</summary>
    [RelayCommand]
    private Task CancelCoupleSensor() => _cancelTachMapping?.Invoke() ?? Task.CompletedTask;

    /// <summary>Schaltet die Dashboard-Sichtbarkeit um (Augen-Button im Geräte-Tab).</summary>
    [RelayCommand]
    private void ToggleVisible() => Visible = !Visible;

    /// <summary>Übernimmt die Live-Drehzahl (kein Tacho/lesbar bzw. NaN → „n/a") — für die Zeile und den Manuell-Slider.</summary>
    public void SetLiveRpm(double? rpm)
    {
        LiveRpm = rpm is { } r && !double.IsNaN(r)
            ? string.Create(CultureInfo.InvariantCulture, $"{r:0} RPM")
            : "n/a";
        Manual.SetLiveRpm(rpm);
    }

    /// <summary>Erweitert-Sektion zugeklappt → eine ggf. laufende temporäre Manuell-Steuerung beenden (zurück auf Auto/Kurve).</summary>
    partial void OnShowAdvancedChanged(bool value)
    {
        if (!value)
            Manual.Revert();
    }

    /// <summary>
    /// Spiegelt den Kalibrier-Status aus dem Snapshot in diese Zeile — aber nur, wenn er diesen Lüfter
    /// betrifft. Die finale Done/Error-Meldung wird „gelatcht" und für <see cref="_calibrationHold"/> gehalten,
    /// auch wenn zwischendurch ein Snapshot ohne Kalibrierung für diesen Lüfter kommt (sonst nur einen Tick lesbar).
    /// Ein neu startender Lauf für diesen Lüfter bricht das Halten ab und zeigt wieder Live-Fortschritt.
    /// </summary>
    public void ApplyCalibration(CalibrationStatus? status)
    {
        if (status is null || status.FanId != FanId)
        {
            if (_holdCts is not null)
                return; // finale Meldung wird gerade gehalten — nicht durch fremden/leeren Snapshot löschen

            IsCalibrating = false;
            CalibrationProgress = "";
            CalibrationFanProgress = 0;
            CalibrationFailed = false;
            return;
        }

        if (status.Running)
        {
            // Ein (neu) laufender Lauf für diesen Lüfter: ein evtl. laufendes Halten abbrechen, Live-Fortschritt zeigen.
            CancelHold();
            IsCalibrating = true;
            CalibrationFailed = false;
            CalibrationFanProgress = Math.Clamp(status.CurrentPwm / 255.0, 0, 1) * 100;
            CalibrationProgress = $"{IpcStatusText.Phase(status.Phase, status.CurrentPwm)} · pwm {status.CurrentPwm} · {status.CurrentRpm} RPM";
            return;
        }

        // Done oder Error für diesen Lüfter → finale Meldung latchen und für eine Weile halten.
        IsCalibrating = false;
        if (status.FailReason is { } reason)
        {
            CalibrationProgress = Localizer.Instance.Format(
                "FanAssignRow.Error", IpcStatusText.Fail(reason, status.OverTempC, status.OverLimitC));
            CalibrationFailed = true;
        }
        else
        {
            CalibrationProgress = Localizer.Instance.Format("FanAssignRow.CalibDone", status.StartPwm);
            CalibrationFanProgress = 100;
            CalibrationFailed = false;
            IsCalibrated = true; // ab jetzt „bereits kalibriert" — auch ohne Reload/Neustart
            if (status.StartPwm is { } sp)
                CalibrationBadgeHint = CalibrationBadge.Hint((byte)Math.Clamp(sp, 0, 255));
        }
        StartHold();
    }

    /// <summary>
    /// Spiegelt den Identify-Status aus dem Snapshot in diese Zeile — analog zu <see cref="ApplyCalibration"/>,
    /// aber mit eigenem Halten (<see cref="_identifyHoldCts"/>), damit Identify und Kalibrierung sich nicht
    /// gegenseitig die gehaltene Meldung wegräumen. Bei Erfolg sendet der Daemon <c>Identify=null</c> → der
    /// „fremde/leere"-Zweig leert wieder (keine Erfolgsmeldung nötig); nur ein Fehler/Abbruch wird gelatcht.
    /// </summary>
    public void ApplyIdentify(IdentifyStatus? status)
    {
        if (status is null || status.FanId != FanId)
        {
            if (_identifyHoldCts is not null)
                return; // finale (Abbruch-)Meldung wird gerade gehalten — nicht durch fremden/leeren Snapshot löschen

            IsIdentifying = false;
            IdentifyProgress = "";
            return;
        }

        if (status.Running)
        {
            CancelIdentifyHold();
            IsIdentifying = true;
            IdentifyProgress = Localizer.Instance["FanAssignRow.Identifying"];
            return;
        }

        // Abbruch/Fehler für diesen Lüfter → kurz latchen. (Erfolg kommt als Identify=null, nicht hier an.)
        IsIdentifying = false;
        if (status.FailReason is { } reason)
        {
            IdentifyProgress = Localizer.Instance.Format(
                "FanAssignRow.Aborted", IpcStatusText.Fail(reason, status.OverTempC, status.OverLimitC));
            StartIdentifyHold();
        }
        else
        {
            IdentifyProgress = "";
        }
    }

    /// <summary>
    /// Spiegelt den Tacho-Kopplungs-Status aus dem Snapshot in diese Zeile — analog zu <see cref="ApplyIdentify"/>,
    /// aber mit <b>eigenem</b> Halten (<see cref="_tachHoldCts"/>), damit sich Kopplung, Identify und Kalibrierung
    /// nicht gegenseitig die gehaltene Meldung wegräumen. Anders als bei Identify werden hier ALLE Abschluss-Phasen
    /// (Matched/NoResponse/Ambiguous/Failed) als Ergebnistext gelatcht — der Daemon hält den Status bis zur Quittung.
    /// </summary>
    public void ApplyTachMapping(TachMappingStatus? status)
    {
        if (status is null || status.FanId != FanId)
        {
            if (_tachHoldCts is not null)
                return; // finale Ergebnis-Meldung wird gerade gehalten — nicht durch fremden/leeren Snapshot löschen

            IsTachMapping = false;
            TachMappingProgress = "";
            return;
        }

        if (status.Running)
        {
            CancelTachHold();
            IsTachMapping = true;
            TachMappingProgress = Localizer.Instance["FanAssignRow.Coupling"];
            return;
        }

        // Abschluss (Matched/NoResponse/Ambiguous/Failed) → Ergebnistext latchen und kurz halten.
        IsTachMapping = false;
        TachMappingProgress = IpcStatusText.TachMapping(status);
        StartTachHold();
    }

    /// <summary>
    /// Spiegelt die aktuell im Daemon zugeordnete Tacho-Quelle ins Dropdown. Ein Snapshot mit unverändertem Wert
    /// wird ignoriert, damit eine gerade getätigte (noch nicht bestätigte) Nutzer-Auswahl nicht zurückspringt.
    /// Die Zuweisung läuft mit <see cref="_applyingRpmSource"/>-Gate, damit sie kein <c>SetFanTachometer</c> auslöst.
    /// </summary>
    public void ApplyRpmSource(string? rpmSource)
    {
        if (rpmSource == _lastDaemonRpmSource)
            return;
        _lastDaemonRpmSource = rpmSource;
        _applyingRpmSource = true;
        SelectedTach = AvailableTachSensors.FirstOrDefault(o => o.Id == rpmSource);
        _applyingRpmSource = false;
    }

    /// <summary>
    /// Spiegelt den vom Daemon persistierten Anlaufpunkt (<see cref="FanConfig.MinPwm"/>) in die Zeile. Er ändert
    /// sich dort <b>ohne Zutun des Editors</b>, sobald eine Kalibrierung durchläuft — ohne dieses Nachziehen bliebe
    /// die Zeile auf dem alten Wert und das nächste Speichern schriebe das Kalibrier-Ergebnis stillschweigend
    /// wieder weg. Ein Snapshot mit unverändertem Wert wird ignoriert (schützt eine laufende Nutzer-Eingabe, wie
    /// bei <see cref="ApplyRpmSource"/>). Liefert, ob der Wert übernommen wurde — dann muss der Controller die
    /// Dirty-Baseline nachziehen, denn eine Kalibrierung ist keine ungespeicherte Nutzer-Änderung.
    /// </summary>
    public bool ApplyDaemonMinPwm(int minPwm)
    {
        if (minPwm == _lastDaemonMinPwm)
            return false;
        _lastDaemonMinPwm = minPwm;

        if (minPwm == MinPwm)
            return false; // eigener, gerade gespeicherter Wert kommt zurück — nichts zu tun
        MinPwm = minPwm;
        return true;
    }

    /// <summary>Nutzer-Auswahl im Tacho-Dropdown → dem Daemon die Zuordnung senden (Id <c>null</c> ⇒ löschen). Programmatische Änderungen (Snapshot) sind gegated.</summary>
    partial void OnSelectedTachChanged(TachSensorOption? value)
    {
        if (_applyingRpmSource)
            return;
        _ = _sendSetTach?.Invoke(FanId, value?.Id);
    }

    private void StartHold()
    {
        CancelHold();
        _holdCts = new CancellationTokenSource();
        _ = ClearProgressAfterHoldAsync(_holdCts);
    }

    private void CancelHold()
    {
        _holdCts?.Cancel();
        _holdCts = null;
    }

    private async Task ClearProgressAfterHoldAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_calibrationHold, cts.Token);
            // Nur leeren, wenn dieses Halten noch das aktuelle ist: ein bereits abgelaufener (aber nicht
            // gecancelter) Timer eines früheren Laufs darf eine inzwischen frisch gelatchte Folge-Meldung
            // nicht löschen — sonst verschwände bei zwei dicht aufeinanderfolgenden Done/Error die zweite sofort.
            if (ReferenceEquals(_holdCts, cts))
            {
                CalibrationProgress = "";
                CalibrationFanProgress = 0;
                CalibrationFailed = false;
            }
        }
        catch (OperationCanceledException)
        {
            // durch einen neuen Lauf oder ein neueres Halten abgelöst — die neue Meldung steht schon.
        }
        finally
        {
            // Feld freigeben, falls es noch auf genau dieses (jetzt entsorgte) CTS zeigt — sonst würde der
            // nächste CancelHold ein disposed CTS canceln. Single-threaded UI-Dispatch: ReferenceEquals genügt.
            if (ReferenceEquals(_holdCts, cts))
                _holdCts = null;
            cts.Dispose();
        }
    }

    private void StartIdentifyHold()
    {
        CancelIdentifyHold();
        _identifyHoldCts = new CancellationTokenSource();
        _ = ClearIdentifyProgressAfterHoldAsync(_identifyHoldCts);
    }

    private void CancelIdentifyHold()
    {
        _identifyHoldCts?.Cancel();
        _identifyHoldCts = null;
    }

    private async Task ClearIdentifyProgressAfterHoldAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_calibrationHold, cts.Token);
            if (ReferenceEquals(_identifyHoldCts, cts))
                IdentifyProgress = "";
        }
        catch (OperationCanceledException)
        {
            // durch einen neuen Lauf oder ein neueres Halten abgelöst — die neue Meldung steht schon.
        }
        finally
        {
            if (ReferenceEquals(_identifyHoldCts, cts))
                _identifyHoldCts = null;
            cts.Dispose();
        }
    }

    private void StartTachHold()
    {
        CancelTachHold();
        _tachHoldCts = new CancellationTokenSource();
        _ = ClearTachProgressAfterHoldAsync(_tachHoldCts);
    }

    private void CancelTachHold()
    {
        _tachHoldCts?.Cancel();
        _tachHoldCts = null;
    }

    private async Task ClearTachProgressAfterHoldAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_calibrationHold, cts.Token);
            if (ReferenceEquals(_tachHoldCts, cts))
                TachMappingProgress = "";
        }
        catch (OperationCanceledException)
        {
            // durch einen neuen Lauf oder ein neueres Halten abgelöst — die neue Meldung steht schon.
        }
        finally
        {
            if (ReferenceEquals(_tachHoldCts, cts))
                _tachHoldCts = null;
            cts.Dispose();
        }
    }

    partial void OnMaxPwmChanged(int value)
    {
        if (_autoAdjusting)
            return; // nachgezogen vom Min-Setter — Hinweis dort schon gesetzt, nicht überschreiben

        if (value < MinPwm)
        {
            _autoAdjusting = true;
            MinPwm = value; // Min auf Max senken (muss ≤ Max sein)
            _autoAdjusting = false;
            PwmAdjustHint = Localizer.Instance.Format("FanAssignRow.MinClamped", PwmScale.ToPercent((byte)value));
        }
        else
        {
            PwmAdjustHint = "";
        }
    }

    partial void OnMinPwmChanged(int value)
    {
        if (_autoAdjusting)
            return; // nachgezogen vom Max-Setter — Hinweis dort schon gesetzt, nicht überschreiben

        if (value > MaxPwm)
        {
            _autoAdjusting = true;
            MaxPwm = value; // Max auf Min anheben (muss ≥ Min sein)
            _autoAdjusting = false;
            PwmAdjustHint = Localizer.Instance.Format("FanAssignRow.MaxClamped", PwmScale.ToPercent((byte)value));
        }
        else
        {
            PwmAdjustHint = "";
        }
    }

    [RelayCommand]
    private void Clear() => Selected = null;

    /// <summary>
    /// Setzt den editierbaren View-Zustand (Name/Position/Sichtbarkeit/PWM-Grenzen) aus der Config
    /// zurück — für „Verwerfen". <see cref="Selected"/> bleibt außen vor; die Kurven-Zuordnung stellt der
    /// Controller über das Neuladen der Kurven her. Null → die Discovery-Basis (<c>_base</c>).
    /// </summary>
    public void ApplyConfig(FanConfig? config)
    {
        FanConfig c = config ?? _base;
        Name = c.Name;
        Location = FanLocationOption.For(c.Location);
        Visible = !c.Hidden;
        MinPwm = c.MinPwm;
        MaxPwm = c.MaxPwm;
        PwmAdjustHint = ""; // die Min/Max-Setter können beim Zurücksetzen transient anschlagen → Hinweis leeren
    }

    public FanConfig ToConfig() => _base with
    {
        // Empty/whitespace name → keep the previous display name (no silent data loss). If that was empty
        // too, it stays empty: empty means "no own name", and the hardware label applies everywhere (that is
        // how the daemon creates new fans). The last-resort fallback used to be the FanId, which turned the
        // raw hardware path into a supposedly user-defined name.
        Name = string.IsNullOrWhiteSpace(Name) ? _base.Name : Name.Trim(),
        AssignedCurveId = Selected?.Id,
        Location = Location.Value,
        Hidden = !Visible,
        MinPwm = (byte)Math.Clamp(MinPwm, 0, 255),
        MaxPwm = (byte)Math.Clamp(MaxPwm, 0, 255),
    };
}
