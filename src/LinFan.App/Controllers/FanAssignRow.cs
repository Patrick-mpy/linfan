// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;

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
    private readonly Func<Task>? _cancelCalibration;            // CancelCalibration()
    private readonly TimeSpan _calibrationHold;
    private CancellationTokenSource? _holdCts; // hält die finale Done/Error-Meldung; gecancelt bei neuem Lauf
    // Eigenes Halten für die Identify-Meldung - unabhängig vom Kalibrier-Halten (kein gegenseitiges Überschreiben).
    private CancellationTokenSource? _identifyHoldCts;
    // Eigenes Halten für die Kopplungs-Meldung - unabhängig von Kalibrier-/Identify-Halten.
    private CancellationTokenSource? _tachHoldCts;
    // Zuletzt vom Daemon gespiegelte Tacho-Zuordnung; verhindert, dass ein Snapshot mit unverändertem
    // Wert eine gerade getätigte (noch nicht bestätigte) Nutzer-Auswahl in der Tacho-Auswahl zurücksetzt.
    private string? _lastDaemonRpmSource;
    private bool _applyingRpmSource; // true, während die Tacho-Auswahl programmatisch aus dem Snapshot gesetzt wird
    private string? _rpmSourceOwnName; // the config's own name for _lastDaemonRpmSource (shown while it is not detected)
    // Zuletzt vom Daemon gemeldeter Anlaufpunkt (MinPwm) - gleiches Muster wie _lastDaemonRpmSource: nur eine
    // echte Änderung dort (Kalibrier-Ergebnis) zieht den Wert nach, ein unveränderter Snapshot lässt eine
    // gerade getippte, noch nicht gespeicherte Eingabe in Ruhe.
    private int _lastDaemonMinPwm;
    // Whether the last live reading had a speed at all (null = no tach signal) - for the tach status.
    private bool _hasRpm = true;

    public string FanId => _base.FanId;

    /// <summary>Hardware-Name (read-only, als Hinweis neben dem editierbaren Namen).</summary>
    public string HardwareName => _base.FanId;

    /// <summary>
    /// „Lüfter N" (<see cref="DefaultFanNames"/>) - the placeholder of an empty name field and what saving writes for it,
    /// as in the setup assistant. Set by the controller, which knows the other fans' names.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(SelectName))]
    private string _defaultName = "";

    /// <summary>The limit the daemon checks a name against (<see cref="ConfigLimits.MaxTextLength"/>).</summary>
    public int MaxNameLength => ConfigLimits.MaxTextLength;

    /// <summary>Ob der Lüfter steuerbar ist (nur dann ist Kalibrierung möglich). Laufzeit-Info aus dem Snapshot.</summary>
    public bool CanControl { get; }

    private readonly FanReadOnlyReason? _readOnlyReason;

    /// <summary>The note under a read-only fan, with the reason the daemon reported where it is known.</summary>
    public string ReadOnlyNote => ReadOnlyReasonText.Note(Localizer.Instance["FanAssignRow.ReadOnlyNote"], _readOnlyReason);

    /// <summary>Temporäre Manuell-Steuerung (Slider in der Erweitert-Sektion) - erleichtert die Zuordnung;
    /// beim Einklappen der Sektion automatisch zurück auf Kurve/Hardware-Auto.</summary>
    public ManualControl Manual { get; }

    /// <summary>Verfügbare Kurven (gemeinsame Liste des Controllers) für den ComboBox.</summary>
    public ObservableCollection<CurveEditRow> AvailableCurves { get; }

    /// <summary>The speed sensors to choose from (list shared by the controller; the automatic entry first).</summary>
    public ObservableCollection<TachSensorOption> AvailableTachSensors { get; }

    /// <summary>Auswählbare Einbau-Positionen.</summary>
    public IReadOnlyList<FanLocationOption> Locations => FanLocationOption.All;

    /// <summary>Gruppenschlüssel für die Auto-Gruppierung in der Kurven-Zuordnung (Position › „Ungruppiert") - wie in der Übersicht.</summary>
    public string GroupKey => Location.Value != FanLocation.Unspecified
        ? FanLocationOption.GroupNameFor(Location.Value)
        : GroupLabels.Ungrouped;

    /// <summary>
    /// The name to show: the own one, else what an empty field is saved as, else the raw id. <see cref="Name"/> stays empty
    /// while the fan has no own name (<see cref="DefaultName"/> is the field's placeholder) - a list bound to it directly
    /// would show an empty line.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(DefaultName) ? DefaultName
        : HardwareName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(SelectName))]
    private string _name;

    [ObservableProperty] private CurveEditRow? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationLabel), nameof(PositionChipName))]
    private FanLocationOption _location;

    [ObservableProperty] private bool _visible;

    /// <summary>The position as the row's chip shows it: the full name, or „nicht gesetzt".</summary>
    public string LocationLabel => Location.Value == FanLocation.Unspecified
        ? Localizer.Instance["FanSections.PositionNotSet"]
        : Location.Display;

    /// <summary>
    /// Ticked in the settings list for a bulk action. View state only: never part of <see cref="ToConfig"/>, the
    /// history or the unsaved-changes check.
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>The row is part of a running drag (shown dimmed). View state only, like <see cref="IsSelected"/>.</summary>
    [ObservableProperty] private bool _isDragged;

    /// <summary>The entries of the row's „Position ▾" menu - built by the controller, which records the pick as an edit.</summary>
    [ObservableProperty] private IReadOnlyList<MenuChoice> _positionMenu = Array.Empty<MenuChoice>();

    /// <summary>
    /// The service is offline, runs dry, or runs a calibration, identification or tach coupling (of any fan) - it would
    /// refuse another one without a word, so calibrate, identify, couple and the manual test must not pretend they work.
    /// Fed by the controller through <see cref="BlockDeviceActions"/> (see <see cref="DeviceActionBlock"/>).
    /// </summary>
    public bool DeviceActionsBlocked => _deviceActionsBlockedReason.Length > 0;

    private string _deviceActionsBlockedReason = "";

    /// <summary>Tooltip of the calibrate button: what it does - or, while it is locked, why.</summary>
    public string CalibrateTooltip =>
        DeviceActionsBlocked ? _deviceActionsBlockedReason
        : _lacksSpeedSensor ? Localizer.Instance["FanSections.BlockedNoTach"]
        : Localizer.Instance["MainWindow.CalibrateTooltip"];

    // No speed sensor to measure with (DeviceActionBlock.LacksSpeedSensor): locks calibrate only - identify works, and
    // coupling is how the fan gets one.
    private bool _lacksSpeedSensor;

    /// <summary>Tooltip of the identify chip: what it does - or, while it is locked, why.</summary>
    public string IdentifyTooltip => DeviceActionsBlocked ? _deviceActionsBlockedReason : Localizer.Instance["MainWindow.IdentifyTooltip"];

    /// <summary>Tooltip of the couple button: what it does - or, while it is locked, why.</summary>
    public string CoupleSensorTooltip => DeviceActionsBlocked ? _deviceActionsBlockedReason : Localizer.Instance["MainWindow.CoupleSensorTooltip"];

    /// <summary>Accessible name of the row's selection check („Front oben auswählen").</summary>
    public string SelectName => Localizer.Instance.Format("FanSections.SelectFan", DisplayName);

    /// <summary>Accessible name of the position chip, which shows the value only as text inside („Position: Netzteil").</summary>
    public string PositionChipName => Localizer.Instance.Format("FanSections.PositionChipName", LocationLabel);

    // Wahrheit bleibt der rohe PWM-Wert (0-255, so wird gespeichert); MinPercent/MaxPercent sind nur die
    // Anzeige in Prozent (konsistent mit dem Übersicht-Slider & der Kalibrierung). Beide Felder benachrichtigen die
    // jeweilige Prozent-Property, damit das nachgezogene Min/Max in der UI sofort mitläuft.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinPercent), nameof(MinPwmText), nameof(BelowStart), nameof(HasStartHint), nameof(MaxFloorPercent))]
    private int _minPwm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxPercent), nameof(MaxPwmText))]
    private int _maxPwm;

    /// <summary>PWM-Untergrenze als Prozent (0-100). Setzen rundet auf den nächsten rohen PWM-Wert; ein
    /// unangetasteter Wert bleibt beim Speichern exakt der geladene Hardware-Wert (kein stiller Drift).</summary>
    public int MinPercent
    {
        get => PwmScale.ToPercent((byte)Math.Clamp(MinPwm, 0, 255));
        set => MinPwm = PwmScale.ToPwm(value);
    }

    /// <summary>PWM-Obergrenze als Prozent (0-100). Siehe <see cref="MinPercent"/>.</summary>
    public int MaxPercent
    {
        get => PwmScale.ToPercent((byte)Math.Clamp(MaxPwm, 0, 255));
        set => MaxPwm = PwmScale.ToPwm(value);
    }

    /// <summary>Läuft für diesen Lüfter gerade eine Kalibrierung? (Button deaktivieren, Fortschritt zeigen.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate), nameof(CanIdentify), nameof(CanCoupleSensor))]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand), nameof(IdentifyCommand), nameof(CoupleSensorCommand))]
    [NotifyPropertyChangedFor(nameof(CalibrationChipText), nameof(CalibrationChipTone), nameof(HasCalibrationResult))]
    [NotifyPropertyChangedFor(nameof(HasCalibrationMismatch), nameof(ShowsCalibratedBadge))]
    private bool _isCalibrating;

    /// <summary>Kurzer Inline-Fortschrittstext während/nach der Kalibrierung dieses Lüfters.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalibrationResult))]
    private string _calibrationProgress = "";

    /// <summary>Fortschritt der laufenden Kalibrierung dieses Lüfters in Prozent (0..100), aus dem PWM-Rampenwert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationChipText), nameof(CancelCalibrationLabel))]
    private double _calibrationFanProgress;

    /// <summary>How the held outcome of the last calibration reads: done, cancelled or failed.</summary>
    [ObservableProperty] private StatusTone _calibrationResultTone;

    /// <summary>True, wenn die letzte Kalibrierung dieses Lüfters fehlschlug/abgebrochen wurde (Alert-Indikator).</summary>
    [ObservableProperty] private bool _calibrationFailed;

    /// <summary>Läuft für diesen Lüfter gerade die Identifikation? (Button deaktivieren, Fortschritt zeigen.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate), nameof(CanIdentify), nameof(CanCoupleSensor))]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand), nameof(IdentifyCommand), nameof(CoupleSensorCommand))]
    private bool _isIdentifying;

    /// <summary>Kurzer Inline-Fortschrittstext während/nach der Identifikation dieses Lüfters.</summary>
    [ObservableProperty] private string _identifyProgress = "";

    /// <summary>Läuft für diesen Lüfter gerade die automatische Tacho-Kopplung? (Button deaktivieren, Fortschritt/Abbruch zeigen.)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate), nameof(CanIdentify), nameof(CanCoupleSensor))]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand), nameof(IdentifyCommand), nameof(CoupleSensorCommand))]
    [NotifyPropertyChangedFor(nameof(TachChipText), nameof(TachChipTone), nameof(HasTachMappingResult))]
    private bool _isTachMapping;

    /// <summary>Kurzer Inline-Fortschritts-/Ergebnistext während/nach der Tacho-Kopplung dieses Lüfters.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTachMappingResult))]
    private string _tachMappingProgress = "";

    /// <summary>How the held outcome of the last coupling reads: matched, cancelled, or not found / not clear.</summary>
    [ObservableProperty] private StatusTone _tachMappingResultTone;

    /// <summary>The coupled speed sensor, or the automatic entry. Target of the speed-sensor choice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TachName), nameof(TachChipText), nameof(TachChipTone), nameof(CoupleLabel), nameof(SelectedTachChoice))]
    private TachSensorOption? _selectedTach;

    // Calibrate, identify and couple each need the fan free (none of the three running on it) and the service
    // reachable and free (DeviceActionsBlocked) - the daemon runs only one of them at a time. Calibrate also needs a
    // speed sensor to measure with.

    public bool CanCalibrate => !IsCalibrating && !IsIdentifying && !IsTachMapping && !DeviceActionsBlocked && !_lacksSpeedSensor;

    public bool CanIdentify => !IsCalibrating && !IsIdentifying && !IsTachMapping && !DeviceActionsBlocked;

    public bool CanCoupleSensor => !IsCalibrating && !IsIdentifying && !IsTachMapping && !DeviceActionsBlocked;

    /// <summary>
    /// Locks calibrate, identify and couple with <paramref name="reason"/> as their tooltip, or unlocks them (empty) -
    /// see <see cref="DeviceActionsBlocked"/>.
    /// </summary>
    internal void BlockDeviceActions(string reason)
    {
        if (reason == _deviceActionsBlockedReason)
            return;
        _deviceActionsBlockedReason = reason;
        SyncManualBlock();
        OnPropertyChanged(nameof(DeviceActionsBlocked));
        OnPropertyChanged(nameof(CanCalibrate));
        OnPropertyChanged(nameof(CanIdentify));
        OnPropertyChanged(nameof(CanCoupleSensor));
        OnPropertyChanged(nameof(CalibrateTooltip));
        OnPropertyChanged(nameof(IdentifyTooltip));
        OnPropertyChanged(nameof(CoupleSensorTooltip));
        CalibrateCommand.NotifyCanExecuteChanged();
        IdentifyCommand.NotifyCanExecuteChanged();
        CoupleSensorCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Locks calibrate while the fan has no speed sensor to measure with (see <see cref="DeviceActionBlock.LacksSpeedSensor"/>).</summary>
    internal void ApplyLacksSpeedSensor(bool lacks)
    {
        if (lacks == _lacksSpeedSensor)
            return;
        _lacksSpeedSensor = lacks;
        OnPropertyChanged(nameof(CanCalibrate));
        OnPropertyChanged(nameof(CalibrateTooltip));
        CalibrateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Formatierte Live-Drehzahl für Einstellungen › Lüfter (reine Anzeige, fließt nicht in die Config).</summary>
    [ObservableProperty] private string _liveRpm = "-";

    /// <summary>Ob die „Erweitert"-Sektion (Grenzwerte + Kalibrierung) aufgeklappt ist - reiner View-Zustand pro Zeile.</summary>
    [ObservableProperty] private bool _showAdvanced;

    /// <summary>True, sobald für diesen Lüfter eine Kalibrierung vorliegt (aus der Config geladen oder in dieser
    /// Sitzung erfolgreich gelaufen).</summary>
    public bool IsCalibrated => StartPwm is not null;

    /// <summary>The „calibrated" badge in the row's head - a calibration the fan no longer fits gets the warning instead.</summary>
    public bool ShowsCalibratedBadge => IsCalibrated && !HasCalibrationMismatch;

    /// <summary>Tooltip des Kalibrier-Badges - Anlaufpunkt in % (bzw. Hinweis, falls keiner gefunden wurde).</summary>
    public string CalibrationBadgeHint => StartPwm is { } s ? CalibrationBadge.Hint(s) : "";

    /// <summary>
    /// The calibrated start point (raw PWM): the lowest power at which the fan starts reliably. <c>null</c> = not
    /// calibrated; 255 = the calibration found no safe start point.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalibrated), nameof(ShowsCalibratedBadge), nameof(CalibrationBadgeHint),
        nameof(StartPercent), nameof(HasSafeStart), nameof(StartMarker), nameof(StartMarkerLabel),
        nameof(StartText), nameof(CalibrationChipText), nameof(CalibrationChipTone), nameof(CalibrateLabel),
        nameof(BelowStart), nameof(HasStartHint), nameof(StartHintText), nameof(StartHintAction), nameof(MaxFloorPercent))]
    [NotifyCanExecuteChangedFor(nameof(RaiseToStartCommand))]
    private byte? _startPwm;

    /// <summary>The calibrated maximum speed (the airflow balance weighs case fans by it); <c>null</c> = not calibrated.</summary>
    [ObservableProperty] private int? _maxRpm;

    // The calibration as last stored by the daemon - written back unchanged by ToConfig, so a save after an in-session
    // calibration does not resend the one the row was built with.
    private FanCalibration? _calibration;

    /// <summary>Whether „assign by hand" is open (view state; the choices are built only then).</summary>
    [ObservableProperty] private bool _showTachChoices;

    /// <summary>The entries of „assign by hand": every speed sensor and who holds it. Empty while closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTachChoice))]
    private IReadOnlyList<TachChoice> _tachChoices = Array.Empty<TachChoice>();

    /// <summary>
    /// Who else holds a speed sensor explicitly: (this row, sensor id) → that fan's name, or empty. Set by the
    /// controller, which knows all rows.
    /// </summary>
    internal Func<FanAssignRow, string, string>? TachOwner { get; init; }

    /// <summary>
    /// Which other fan the hardware reads from a speed sensor while it has no coupling of its own: (this row, sensor
    /// id) → that fan's name, or empty. Set by the controller, which knows all rows.
    /// </summary>
    internal Func<FanAssignRow, string, string>? TachAutoOwner { get; init; }

    /// <summary>The speed sensor the daemon reads this fan from (<see cref="FanReading.Tachometer"/>), <c>null</c> = none or unknown.</summary>
    private string? _daemonTach;

    // --- Power range -----------------------------------------------------------------------------

    public string MinPwmText => Localizer.Instance.Format("FanAssignRow.PwmValue", MinPwm);

    public string MaxPwmText => Localizer.Instance.Format("FanAssignRow.PwmValue", MaxPwm);

    public int StartPercent => StartPwm is { } s ? PwmScale.ToPercent(s) : 0;

    /// <summary>A start point exists and is not the „none found" value.</summary>
    public bool HasSafeStart => StartPwm is { } s && CalibrationBadge.HasSafeStart(s);

    /// <summary>The start point on the range slider (percent), or <c>null</c> for no marker.</summary>
    public double? StartMarker => HasSafeStart ? StartPercent : null;

    public string StartMarkerLabel => Localizer.Instance.Format("FanAssignRow.StartMark", StartPercent);

    /// <summary>
    /// Min lies between off and the start point: the curve may ask for a power at which the fan stalls. Min 0 is a
    /// deliberate „may stop" and does not count.
    /// </summary>
    public bool BelowStart => CanControl && HasSafeStart && MinPwm > 0 && MinPwm < StartPwm;

    /// <summary>Min lies below the start point - the hint under the range says so and offers to raise it.</summary>
    public bool HasStartHint => BelowStart;

    public string StartHintText => Localizer.Instance.Format("FanAssignRow.BelowStart", StartPercent);

    public string StartHintAction => Localizer.Instance.Format("FanAssignRow.SetMinToStart", StartPercent);

    /// <summary>
    /// The lowest Max: the start point, and never below 1 %. Below it every curve value would hold the fan still while
    /// the overview shows it on its curve - the daemon raises such a Max as well (ConfigSanitizer), so the GUI keeps to
    /// (at least) the same bound instead of showing a value the daemon does not use.
    /// </summary>
    private int MaxFloorPwm => Math.Max(HasSafeStart ? StartPwm!.Value : 0, PwmScale.ToPwm(1));

    /// <summary>The Max stepper's minimum: the floor, or Min above it.</summary>
    public int MaxFloorPercent => Math.Max(MinPercent, PwmScale.ToPercent((byte)MaxFloorPwm));

    // --- Calibration -----------------------------------------------------------------------------

    /// <summary>The start point as the status grid shows it: „18 % · pwm 46", „none found" or a dash.</summary>
    public string StartText => StartPwm is not { } s ? "-"
        : !HasSafeStart ? Localizer.Instance["FanAssignRow.StartNone"]
        : Localizer.Instance.Format("FanAssignRow.StartValue", StartPercent, s);

    public string CalibrationChipText =>
        IsCalibrating ? Localizer.Instance.Format("FanAssignRow.ChipCalibrating", (int)Math.Round(CalibrationFanProgress))
        : HasCalibrationMismatch ? Localizer.Instance["FanAssignRow.ChipCalibrationMismatch"]
        : StartPwm is null ? Localizer.Instance["FanAssignRow.ChipNotCalibrated"]
        : !HasSafeStart ? Localizer.Instance["FanAssignRow.ChipNoSafeStart"]
        : Localizer.Instance["FanAssignRow.ChipCalibrated"];

    public StatusTone CalibrationChipTone =>
        IsCalibrating ? StatusTone.Busy
        : HasCalibrationMismatch ? StatusTone.Warn
        : StartPwm is null ? StatusTone.Muted
        : !HasSafeStart ? StatusTone.Warn
        : StatusTone.Ok;

    public string CalibrateLabel => Localizer.Instance[StartPwm is null ? "MainWindow.Calibrate" : "FanAssignRow.Recalibrate"];

    /// <summary>„Abbrechen · 40 %" - the cancel button carries the progress.</summary>
    public string CancelCalibrationLabel =>
        Localizer.Instance.Format("FanAssignRow.CancelCalibration", (int)Math.Round(CalibrationFanProgress));

    /// <summary>The held outcome of the last calibration is shown (not while one runs).</summary>
    public bool HasCalibrationResult => !IsCalibrating && CalibrationProgress.Length > 0;

    /// <summary>What the daemon's calibration check finds for this fan (<see cref="FanReading.CalibrationMismatch"/>).</summary>
    private CalibrationMismatch? _calibrationMismatch;

    /// <summary>
    /// The daemon finds the fan off its calibration - the overview's hint, here next to the button that recalibrates.
    /// Not while a calibration runs (it replaces the one the finding is about) and not for a fan that cannot be
    /// calibrated, which the overview leaves out as well.
    /// </summary>
    public bool HasCalibrationMismatch => CanControl && !IsCalibrating && _calibrationMismatch is not null;

    public string CalibrationMismatchText => _calibrationMismatch switch
    {
        CalibrationMismatch.Stalls => Localizer.Instance["FanAssignRow.MismatchStalls"],
        CalibrationMismatch.Slower => Localizer.Instance["FanAssignRow.MismatchSlower"],
        CalibrationMismatch.Faster => Localizer.Instance["FanAssignRow.MismatchFaster"],
        CalibrationMismatch.OtherSensor => Localizer.Instance["FanAssignRow.MismatchOtherSensor"],
        _ => "",
    };

    // --- Speed sensor ----------------------------------------------------------------------------

    private bool HasOwnTach => SelectedTach?.Id is not null;

    /// <summary>
    /// The daemon couples the fan to a speed sensor this list does not offer (any more - e.g. a swapped card). The
    /// coupling still holds there, so the row must not read „automatic".
    /// </summary>
    private bool TachMissing => SelectedTach is null && !string.IsNullOrWhiteSpace(_lastDaemonRpmSource);

    /// <summary>
    /// The sensor the hardware's pairing reads this fan from while it has no coupling of its own - <c>null</c> without
    /// one, with an own coupling, or from a daemon that does not report it.
    /// </summary>
    internal string? AutoTachId => HasOwnTach || TachMissing ? null : _daemonTach;

    /// <summary>
    /// The coupled speed sensor, a coupled sensor that is not there, „Automatisch: Fan 1" (the hardware's pairing), or
    /// „Automatisch (Hardware)" while the daemon names none.
    /// </summary>
    public string TachName =>
        SelectedTach is { Id: not null } t ? t.Name
        : TachMissing ? Localizer.Instance.Format("FanAssignRow.TachMissing",
            string.IsNullOrWhiteSpace(_rpmSourceOwnName) ? _lastDaemonRpmSource! : _rpmSourceOwnName)
        : AutoTachId is { } auto ? Localizer.Instance.Format("FanAssignRow.TachAutoNamed",
            AvailableTachSensors.FirstOrDefault(o => o.Id == auto)?.Name ?? auto)
        : Localizer.Instance["FanAssignRow.TachAuto"];

    private bool TachShared => SelectedTach?.Id is { } id && (TachOwner?.Invoke(this, id) ?? "").Length > 0;

    public string TachChipText =>
        IsTachMapping ? Localizer.Instance["FanAssignRow.ChipCoupling"]
        : TachMissing ? Localizer.Instance["FanAssignRow.ChipTachMissing"]
        : TachShared ? Localizer.Instance["FanAssignRow.ChipTachShared"]
        : !_hasRpm ? Localizer.Instance["FanAssignRow.ChipNoSignal"]
        : HasOwnTach ? Localizer.Instance["FanAssignRow.ChipCoupled"]
        : Localizer.Instance["FanAssignRow.ChipTachAuto"];

    public StatusTone TachChipTone =>
        IsTachMapping ? StatusTone.Busy
        : TachMissing || TachShared || !_hasRpm ? StatusTone.Warn
        : StatusTone.Ok;

    public string CoupleLabel => Localizer.Instance[HasOwnTach ? "FanAssignRow.Recouple" : "FanAssignRow.Couple"];

    /// <summary>The held outcome of the last coupling is shown (not while one runs).</summary>
    public bool HasTachMappingResult => !IsTachMapping && TachMappingProgress.Length > 0;

    /// <summary>
    /// List selection of „assign by hand": the entry of the current sensor. Picking a sensor another fan holds is
    /// refused - the list shows such entries disabled anyway.
    /// </summary>
    public TachChoice? SelectedTachChoice
    {
        // A missing sensor is no entry of the list - not even the automatic one.
        get => SelectedTach is null ? null : TachChoices.FirstOrDefault(c => c.Option.Id == SelectedTach.Id);
        set
        {
            if (value is { IsEnabled: true } && !ReferenceEquals(value.Option, SelectedTach))
                SelectedTach = value.Option;
            else
                OnPropertyChanged();
        }
    }

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
                        Func<Task>? cancelCalibration = null,
                        FanReadOnlyReason? readOnlyReason = null)
    {
        _base = baseFan;
        _name = baseFan.Name;
        _selected = selected;
        AvailableCurves = availableCurves;
        AvailableTachSensors = availableTachSensors ?? new();
        CanControl = canControl;
        _readOnlyReason = readOnlyReason;
        _sendCalibrate = sendCalibrate;
        _sendIdentify = sendIdentify;
        _sendTachMapping = sendTachMapping;
        _cancelTachMapping = cancelTachMapping;
        _sendSetTach = sendSetTach;
        _cancelCalibration = cancelCalibration;
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
            _startPwm = cal.StartPwm;
            _maxRpm = cal.MaxRpm;
            _calibration = cal;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCalibrate))]
    private Task Calibrate() => StartDeviceAction(_sendCalibrate);

    [RelayCommand(CanExecute = nameof(CanIdentify))]
    private Task Identify() => StartDeviceAction(_sendIdentify);

    /// <summary>Startet die automatische Tacho-Kopplung (nur für steuerbare Lüfter - sonst kann nichts angetrieben werden).</summary>
    [RelayCommand(CanExecute = nameof(CanCoupleSensor))]
    private Task CoupleSensor() => StartDeviceAction(_sendTachMapping);

    // The daemon keeps a manual override through a device action, so a test value left running (the action can start
    // from another page, e.g. the airflow hint) would take the fan over again afterwards - let go of it first.
    private Task StartDeviceAction(Func<string, Task>? send)
    {
        if (!CanControl)
            return Task.CompletedTask;
        Manual.Revert();
        return send?.Invoke(FanId) ?? Task.CompletedTask;
    }

    /// <summary>Bricht die laufende automatische Tacho-Kopplung ab (bzw. quittiert deren Abschluss-Status).</summary>
    [RelayCommand]
    private Task CancelCoupleSensor() => _cancelTachMapping?.Invoke() ?? Task.CompletedTask;

    // Manual control may not start while the service is offline, dry or busy with a device action, nor while one runs
    // on this fan (see ManualControl.EngageBlocked); letting go stays possible.
    private void SyncManualBlock() =>
        Manual.EngageBlocked = DeviceActionsBlocked || IsCalibrating || IsIdentifying || IsTachMapping;

    partial void OnIsCalibratingChanged(bool value) => SyncManualBlock();

    partial void OnIsIdentifyingChanged(bool value) => SyncManualBlock();

    partial void OnIsTachMappingChanged(bool value) => SyncManualBlock();

    /// <summary>Cancels the calibration running on this fan (the daemon runs one at a time).</summary>
    [RelayCommand]
    private Task CancelCalibration() => _cancelCalibration?.Invoke() ?? Task.CompletedTask;

    /// <summary>Raises Min, which the start-point hint names, to the calibrated start point.</summary>
    [RelayCommand(CanExecute = nameof(HasSafeStart))]
    private void RaiseToStart()
    {
        if (StartPwm is { } s)
            MinPwm = s;
    }

    /// <summary>Schaltet die Sichtbarkeit in der Übersicht um (Augen-Button in Einstellungen › Lüfter).</summary>
    [RelayCommand]
    private void ToggleVisible() => Visible = !Visible;

    /// <summary>
    /// Takes the live speed (no tach / unreadable / NaN → „n/a") and power - for the row, the tach status and the test
    /// slider, which starts manual control from the power the fan runs at.
    /// </summary>
    /// <param name="manualOverride">Whether the daemon holds a manual override for the fan; <c>null</c> = not known.</param>
    public void SetLive(double? rpm, byte? pwm, bool? manualOverride = null)
    {
        bool hasRpm = RpmText.IsKnown(rpm);
        LiveRpm = RpmText.Of(rpm);
        Manual.SetLiveRpm(rpm);
        Manual.SetLivePwm(pwm);
        if (manualOverride is { } held)
            Manual.ApplyDaemonOverride(held);
        if (hasRpm != _hasRpm)
        {
            _hasRpm = hasRpm;
            OnPropertyChanged(nameof(TachChipText));
            OnPropertyChanged(nameof(TachChipTone));
        }
    }

    /// <summary>Follows the daemon's calibration check for this fan; <c>null</c> = it fits (or an older daemon).</summary>
    internal void ApplyCalibrationMismatch(CalibrationMismatch? mismatch)
    {
        if (mismatch == _calibrationMismatch)
            return;
        _calibrationMismatch = mismatch;
        foreach (string property in new[]
                 {
                     nameof(HasCalibrationMismatch), nameof(CalibrationMismatchText), nameof(ShowsCalibratedBadge),
                     nameof(CalibrationChipText), nameof(CalibrationChipTone),
                 })
            OnPropertyChanged(property);
    }

    /// <summary>
    /// The calibration the daemon has stored for this fan (it changes there when a run finishes). <c>null</c> leaves
    /// the row alone: a snapshot from before the daemon stored a fresh result must not take it back. Returns whether
    /// the row took a new one - the editor then carries it into its baseline, since <see cref="ToConfig"/> writes it.
    /// </summary>
    public bool ApplyStoredCalibration(FanCalibration? calibration)
    {
        if (calibration is null
            || (_calibration is { } known && known.StartPwm == calibration.StartPwm && known.MinRpm == calibration.MinRpm
                && known.MaxRpm == calibration.MaxRpm))
            return false;
        _calibration = calibration;
        StartPwm = calibration.StartPwm;
        MaxRpm = calibration.MaxRpm;
        return true;
    }

    /// <summary>A row's speed sensor changed: who holds which sensor may have changed with it.</summary>
    internal void RefreshTach()
    {
        OnPropertyChanged(nameof(TachChipText));
        OnPropertyChanged(nameof(TachChipTone));
        foreach (TachChoice choice in TachChoices)
        {
            choice.TakenBy = OwnerOf(choice.Option);
            choice.AutoAt = AutoOwnerOf(choice.Option);
        }
    }

    partial void OnShowTachChoicesChanged(bool value) =>
        TachChoices = value
            ? AvailableTachSensors.Select(o => new TachChoice(o, OwnerOf(o), AutoOwnerOf(o))).ToList()
            : Array.Empty<TachChoice>();

    private string OwnerOf(TachSensorOption option) => option.Id is { } id ? TachOwner?.Invoke(this, id) ?? "" : "";

    private string AutoOwnerOf(TachSensorOption option) => option.Id is { } id ? TachAutoOwner?.Invoke(this, id) ?? "" : "";

    /// <summary>
    /// Follows the speed sensor the daemon reads this fan from. Returns whether it changed - then every row's list may
    /// name another fan for the hardware's pairing.
    /// </summary>
    internal bool ApplyDaemonTachometer(string? sensorId)
    {
        if (sensorId == _daemonTach)
            return false;
        _daemonTach = sensorId;
        OnPropertyChanged(nameof(TachName));
        return true;
    }

    /// <summary>Erweitert-Sektion zugeklappt → eine ggf. laufende temporäre Manuell-Steuerung beenden (zurück auf Auto/Kurve).</summary>
    partial void OnShowAdvancedChanged(bool value)
    {
        if (!value)
            Manual.Revert();
    }

    /// <summary>
    /// Spiegelt den Kalibrier-Status aus dem Snapshot in diese Zeile - aber nur, wenn er diesen Lüfter
    /// betrifft. Die finale Done/Error-Meldung wird „gelatcht" und für <see cref="_calibrationHold"/> gehalten,
    /// auch wenn zwischendurch ein Snapshot ohne Kalibrierung für diesen Lüfter kommt (sonst nur einen Tick lesbar).
    /// Ein neu startender Lauf für diesen Lüfter bricht das Halten ab und zeigt wieder Live-Fortschritt.
    /// </summary>
    public void ApplyCalibration(CalibrationStatus? status)
    {
        if (status is null || status.FanId != FanId)
        {
            if (_holdCts is not null)
                return; // finale Meldung wird gerade gehalten - nicht durch fremden/leeren Snapshot löschen

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
            CalibrationProgress = Localizer.Instance.Format("FanAssignRow.CalibrationDetail",
                IpcStatusText.Phase(status.Phase, status.CurrentPwm), status.CurrentPwm, status.CurrentRpm);
            return;
        }

        // Done oder Error für diesen Lüfter → finale Meldung latchen und für eine Weile halten.
        IsCalibrating = false;
        if (status.FailReason is { } reason)
        {
            CalibrationProgress = Localizer.Instance.Format(
                "FanAssignRow.Error", IpcStatusText.Fail(reason, status.OverTempC, status.OverLimitC));
            CalibrationFailed = true;
            CalibrationResultTone = reason == CalibrationFailReason.Canceled ? StatusTone.Muted : StatusTone.Warn;
        }
        else
        {
            // From now on "already calibrated" - without waiting for the daemon's stored result.
            byte? start = status.StartPwm is { } sp ? (byte)Math.Clamp(sp, 0, 255) : null;
            if (start is not null)
                StartPwm = start;
            // A run that finds no start point (the fan never spun) is done, but nothing to be pleased about.
            bool safe = start is { } st && CalibrationBadge.HasSafeStart(st);
            CalibrationProgress = start is not null && !safe
                ? Localizer.Instance["FanAssignRow.CalibDoneNoSafeStart"]
                : Localizer.Instance.Format("FanAssignRow.CalibDone", status.StartPwm);
            CalibrationFanProgress = 100;
            CalibrationFailed = false;
            CalibrationResultTone = start is not null && !safe ? StatusTone.Warn : StatusTone.Ok;
        }
        StartHold();
    }

    /// <summary>
    /// Spiegelt den Identify-Status aus dem Snapshot in diese Zeile - analog zu <see cref="ApplyCalibration"/>,
    /// aber mit eigenem Halten (<see cref="_identifyHoldCts"/>), damit Identify und Kalibrierung sich nicht
    /// gegenseitig die gehaltene Meldung wegräumen. Bei Erfolg sendet der Daemon <c>Identify=null</c> → der
    /// „fremde/leere"-Zweig leert wieder (keine Erfolgsmeldung nötig); nur ein Fehler/Abbruch wird gelatcht.
    /// </summary>
    public void ApplyIdentify(IdentifyStatus? status)
    {
        if (status is null || status.FanId != FanId)
        {
            if (_identifyHoldCts is not null)
                return; // finale (Abbruch-)Meldung wird gerade gehalten - nicht durch fremden/leeren Snapshot löschen

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
    /// Spiegelt den Tacho-Kopplungs-Status aus dem Snapshot in diese Zeile - analog zu <see cref="ApplyIdentify"/>,
    /// aber mit <b>eigenem</b> Halten (<see cref="_tachHoldCts"/>), damit sich Kopplung, Identify und Kalibrierung
    /// nicht gegenseitig die gehaltene Meldung wegräumen. Anders als bei Identify werden hier ALLE Abschluss-Phasen
    /// (Matched/NoResponse/Ambiguous/Failed) als Ergebnistext gelatcht - der Daemon hält den Status bis zur Quittung.
    /// </summary>
    public void ApplyTachMapping(TachMappingStatus? status)
    {
        if (status is null || status.FanId != FanId)
        {
            if (_tachHoldCts is not null)
                return; // finale Ergebnis-Meldung wird gerade gehalten - nicht durch fremden/leeren Snapshot löschen

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
        TachMappingResultTone = status.Phase switch
        {
            TachMappingPhase.Matched => StatusTone.Ok,
            TachMappingPhase.Failed when status.FailReason == TachMappingFailReason.Canceled => StatusTone.Muted,
            _ => StatusTone.Warn,
        };
        StartTachHold();
    }

    /// <summary>
    /// Spiegelt die aktuell im Daemon zugeordnete Tacho-Quelle in die Auswahl. Ein Snapshot mit unverändertem Wert
    /// wird ignoriert, damit eine gerade getätigte (noch nicht bestätigte) Nutzer-Auswahl nicht zurückspringt.
    /// Die Zuweisung läuft mit <see cref="_applyingRpmSource"/>-Gate, damit sie kein <c>SetFanTachometer</c> auslöst.
    /// </summary>
    /// <param name="ownName">The user's own name for the sensor from the config, if any - the row names a sensor that is not
    /// detected right now by it instead of its id.</param>
    public void ApplyRpmSource(string? rpmSource, string? ownName = null)
    {
        if (rpmSource == _lastDaemonRpmSource && ownName == _rpmSourceOwnName)
            return;
        _rpmSourceOwnName = ownName;
        if (rpmSource != _lastDaemonRpmSource)
        {
            _lastDaemonRpmSource = rpmSource;
            _applyingRpmSource = true;
            SelectedTach = AvailableTachSensors.FirstOrDefault(o => o.Id == rpmSource);
            _applyingRpmSource = false;
        }
        // From one missing sensor to another leaves SelectedTach null - the texts change anyway.
        OnPropertyChanged(nameof(TachName));
        OnPropertyChanged(nameof(TachChipText));
        OnPropertyChanged(nameof(TachChipTone));
    }

    /// <summary>
    /// The start point the daemon reported last - the base a save's MinPwm is compared with: a value still equal to it is
    /// one the user left alone, and the daemon keeps its own (a calibration may have set it meanwhile).
    /// </summary>
    public byte DaemonMinPwm => (byte)Math.Clamp(_lastDaemonMinPwm, 0, 255);

    /// <summary>
    /// Spiegelt den vom Daemon persistierten Anlaufpunkt (<see cref="FanConfig.MinPwm"/>) in die Zeile. Er ändert
    /// sich dort <b>ohne Zutun des Editors</b>, sobald eine Kalibrierung durchläuft - ohne dieses Nachziehen bliebe
    /// die Zeile auf dem alten Wert und das nächste Speichern schriebe das Kalibrier-Ergebnis stillschweigend
    /// wieder weg. Ein Snapshot mit unverändertem Wert wird ignoriert (schützt eine laufende Nutzer-Eingabe, wie
    /// bei <see cref="ApplyRpmSource"/>).
    /// </summary>
    /// <returns>
    /// Whether the daemon reported a new value - not whether the row changed: a draft that already shows the
    /// calibrated value leaves the row as it is, yet the controller still has to rebase baseline and history,
    /// which hold the old, lower start point. The controller tells a calibration from the echo of its own save.
    /// </returns>
    public bool ApplyDaemonMinPwm(int minPwm)
    {
        if (minPwm == _lastDaemonMinPwm)
            return false;
        _lastDaemonMinPwm = minPwm;

        if (minPwm != MinPwm)
            MinPwm = minPwm;
        return true;
    }

    /// <summary>Nutzer-Auswahl des Tachos → dem Daemon die Zuordnung senden (Id <c>null</c> ⇒ löschen). Programmatische Änderungen (Snapshot) sind gegated.</summary>
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
            // nicht löschen - sonst verschwände bei zwei dicht aufeinanderfolgenden Done/Error die zweite sofort.
            if (ReferenceEquals(_holdCts, cts))
            {
                CalibrationProgress = "";
                CalibrationFanProgress = 0;
                CalibrationFailed = false;
            }
        }
        catch (OperationCanceledException)
        {
            // durch einen neuen Lauf oder ein neueres Halten abgelöst - die neue Meldung steht schon.
        }
        finally
        {
            // Feld freigeben, falls es noch auf genau dieses (jetzt entsorgte) CTS zeigt - sonst würde der
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
            // durch einen neuen Lauf oder ein neueres Halten abgelöst - die neue Meldung steht schon.
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
            // durch einen neuen Lauf oder ein neueres Halten abgelöst - die neue Meldung steht schon.
        }
        finally
        {
            if (ReferenceEquals(_tachHoldCts, cts))
                _tachHoldCts = null;
            cts.Dispose();
        }
    }

    // Min ≤ Max: the slider's thumbs and the steppers cannot cross, so this only takes on other paths (a start point
    // the daemon adopted above Max, a percent that rounds one raw step past the other bound) - and silently pulls the
    // other bound along. Max below its floor goes back to it, so the slider snaps there.
    partial void OnMaxPwmChanged(int value)
    {
        if (value < MaxFloorPwm)
        {
            MaxPwm = MaxFloorPwm;
            return;
        }
        if (value < MinPwm)
            MinPwm = value;
    }

    partial void OnMinPwmChanged(int value)
    {
        if (value > MaxPwm)
            MaxPwm = value;
    }

    [RelayCommand]
    private void Clear() => Selected = null;

    /// <summary>The language changed: the texts built here have to be read again (the lock reason comes from the controller).</summary>
    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(LocationLabel));
        OnPropertyChanged(nameof(PositionChipName));
        OnPropertyChanged(nameof(SelectName));
        OnPropertyChanged(nameof(CalibrateTooltip));
        OnPropertyChanged(nameof(IdentifyTooltip));
        OnPropertyChanged(nameof(CoupleSensorTooltip));
        foreach (string text in new[]
                 {
                     nameof(MinPwmText), nameof(MaxPwmText), nameof(StartMarkerLabel), nameof(StartHintText),
                     nameof(StartHintAction), nameof(StartText), nameof(CalibrationChipText), nameof(CalibrateLabel),
                     nameof(CancelCalibrationLabel), nameof(TachName), nameof(TachChipText), nameof(CoupleLabel),
                     nameof(CalibrationBadgeHint), nameof(ReadOnlyNote), nameof(CalibrationMismatchText),
                 })
            OnPropertyChanged(text);
        foreach (TachChoice choice in TachChoices)
            choice.NotifyLanguageChanged();
    }

    /// <summary>
    /// Setzt den editierbaren View-Zustand (Name/Position/Sichtbarkeit/PWM-Grenzen) aus der Config
    /// zurück - für „Verwerfen". <see cref="Selected"/> bleibt außen vor; die Kurven-Zuordnung stellt der
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
    }

    public FanConfig ToConfig() => _base with
    {
        // An empty field is no own name here, so clearing a name is a change and switching the language is none. Saving
        // fills it with DefaultName first (CurveEditorController.FillEmptyNames) - what the placeholder showed.
        Name = string.IsNullOrWhiteSpace(Name) ? "" : Name.Trim(),
        AssignedCurveId = Selected?.Id,
        Location = Location.Value,
        Hidden = !Visible,
        MinPwm = (byte)Math.Clamp(MinPwm, 0, 255),
        MaxPwm = (byte)Math.Clamp(MaxPwm, 0, 255),
        Calibration = _calibration,
    };
}
