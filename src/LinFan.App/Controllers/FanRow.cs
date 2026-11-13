// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// Zeilen-Controller für einen Lüfter; Drehzahl/PWM/Modus und der RPM-Verlauf aktualisieren sich live.
/// Steuerbare Lüfter lassen sich manuell setzen (Slider) - die Befehle laufen über die per
/// <see cref="BindCommands"/> injizierten Callbacks an den Daemon (IPC). Reine Presentation-Mechanik.
/// </summary>
public partial class FanRow : ObservableObject
{
    private const int MaxHistory = 60;

    public string FanId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreName), nameof(ChipTooltip))]
    private string _name;

    [ObservableProperty] private string _rpm = "-";

    /// <summary>The speed without its unit, for the table's number column ("n/a" without a reading).</summary>
    [ObservableProperty] private string _rpmNumber = "-";

    /// <summary>"RPM", or empty while there is no reading.</summary>
    [ObservableProperty] private string _rpmUnit = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipTooltip))]
    private string _pwm = "-";

    /// <summary>The current output in percent of full PWM - the power bar.</summary>
    [ObservableProperty] private double _percentNum;

    [ObservableProperty] private string _percentText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate), nameof(IsReadOnly))]
    private bool _canControl;

    /// <summary>Why the fan is read-only, as the daemon reported it (<c>null</c> = controllable or unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadOnlyNote))]
    private FanReadOnlyReason? _readOnlyReason;

    /// <summary>The note under a read-only fan, with the reason where it is known.</summary>
    public string ReadOnlyNote => ReadOnlyReasonText.Note(Localizer.Instance["Overview.ReadOnlyNote"], ReadOnlyReason);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverrideNote), nameof(CanMoveSlider), nameof(ChipTooltip))]
    private bool _isManual;

    [ObservableProperty] private double _sliderPercent;

    /// <summary>A calibration result exists for this fan (mirrored from the config every tick).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationState))]
    private bool _isCalibrated;

    /// <summary>True, solange für diesen Lüfter gerade eine Kalibrierung läuft - zeigt einen Lauf-Indikator und
    /// sperrt den Kalibrier-Button (der Gesamtfortschritt erscheint im Kalibrier-Banner über den Tabs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate), nameof(CalibrationState))]
    private bool _isCalibrating;

    /// <summary>
    /// Offline the row shows the last state it knew, frozen: nothing can be sent, so the switch, the slider and
    /// "Back to curve" are locked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate), nameof(CanMoveSlider))]
    private bool _isOffline;

    /// <summary>The row's controls (the "…" button) are open.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Who controls the fan right now (see <see cref="FanControlResolver"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ControlLabel), nameof(ControlTooltip), nameof(ControlOpensCurve), nameof(ControlName),
        nameof(ControlIsMuted), nameof(ControlIsAccent), nameof(ControlIsWarning), nameof(ControlIsDanger))]
    private FanControl _control = new(null, "", "", ControlTone.Muted);

    /// <summary>The curve a manual override overrides (assigned and switched on), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverrideNote), nameof(OverrideNote))]
    private string? _overriddenCurveName;

    // Calibrate waits for the service and for any calibration, identification or tach coupling to end, and needs a
    // speed sensor to measure with - the same rules as in Settings › Fans (DeviceActionBlock.CalibrateReason). The reason
    // is the tooltip.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate), nameof(CalibrateTooltip))]
    private string _calibrateBlockedReason = "";

    /// <summary>Where the fan's position sorts (see <see cref="FanLocationOption.Rank"/>) - the table's order.</summary>
    public int LocationRank { get; private set; } = FanLocationOption.Rank(FanLocation.Unspecified);

    public bool CanCalibrate => !IsCalibrating && CalibrateBlockedReason.Length == 0;

    public string CalibrateTooltip => CalibrateBlockedReason.Length > 0
        ? CalibrateBlockedReason
        : Localizer.Instance["MainWindow.CalibrateTooltip"];

    /// <summary>The daemon runs dry: it writes no fan, a manual value included.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate), nameof(CanMoveSlider))]
    private bool _isDryRun;

    /// <summary>Switch and slider are usable: a controllable fan while the daemon is reachable and writes.</summary>
    public bool CanOperate => CanControl && !IsOffline && !IsDryRun;

    public bool CanMoveSlider => CanOperate && IsManual;

    public bool IsReadOnly => !CanControl;

    public string ControlLabel => Control.Label;
    public string ControlTooltip => Control.Tooltip;

    /// <summary>Accessible name of the "Controlled by" entry.</summary>
    public string ControlName => Localizer.Instance.Format("Overview.ControlName", Control.Label);
    public bool ControlOpensCurve => Control.OpensCurve;
    public bool ControlIsMuted => Control.Tone == ControlTone.Muted;
    public bool ControlIsAccent => Control.Tone == ControlTone.Accent;
    public bool ControlIsWarning => Control.Tone == ControlTone.Warning;
    public bool ControlIsDanger => Control.Tone == ControlTone.Danger;

    public bool ShowOverrideNote => IsManual && OverriddenCurveName is not null;

    public string OverrideNote => OverriddenCurveName is { } curve
        ? Localizer.Instance.Format("Overview.OverrideNote", curve)
        : "";

    /// <summary>State line next to "Calibrate": running, calibrated with its start-up point, or not yet.</summary>
    public string CalibrationState => IsCalibrating
        ? Localizer.Instance["MainWindow.CalibrationRunning"]
        : _calibration is { } cal
            ? !CalibrationBadge.HasSafeStart(cal.StartPwm)
                ? Localizer.Instance["Overview.CalibratedNoSafeStart"]
                : Localizer.Instance.Format("Overview.CalibratedStartAt", PwmScale.ToPercent(cal.StartPwm))
            : Localizer.Instance["Overview.NotCalibrated"];

    /// <summary>Tooltip of the calibration line: the start-up point in % (or that none was found).</summary>
    public string CalibrationBadgeHint => _calibration is { } cal ? CalibrationBadge.Hint(cal.StartPwm) : "";

    /// <summary>The position in the UI language; empty without one.</summary>
    public string Location => FanLocationOption.DisplayFor(_placement);

    /// <summary>Tooltip of the fan's chip in a control loop: name, PWM and whether a manual value overrides the curve.</summary>
    public string ChipTooltip => IsManual
        ? $"{Name} · {Pwm} · {Localizer.Instance["Overview.ManualOverridden"]}"
        : $"{Name} · {Pwm}";

    /// <summary>Accessible name of the "…" button.</summary>
    public string MoreName => Localizer.Instance.Format("Overview.MoreName", Name);

    /// <summary>Rollender Drehzahl-Verlauf für die Sparkline.</summary>
    public ObservableCollection<double> RpmHistory { get; } = new();

    private readonly ManualPwmPump _pump;
    private Func<string, Task>? _sendAuto;
    private Func<string, Task>? _sendCalibrate;
    private Action<string>? _openCurve;
    private bool _applyingSnapshot; // unterdrückt das Command-Senden, wenn ein Snapshot den Wert setzt
    private bool _confirmedManual;  // the mode the daemon reported last
    private FanCalibration? _calibration; // kept as data, so its texts follow a language switch
    private FanLocation _placement = FanLocation.Unspecified;

    /// <summary>Mindestabstand zwischen zwei Manual-Sends während eines Zugs. Test-Naht: in Tests auf <c>Zero</c>.</summary>
    internal TimeSpan ManualThrottle { get => _pump.Throttle; set => _pump.Throttle = value; }

    /// <summary>Läuft, solange die Manual-Pumpe noch sendet - Test-Naht zum deterministischen Abwarten.</summary>
    internal Task ManualPumpCompletion => _pump.Completion;

    // Optimistisches Umschalten: nach einem Nutzer-Toggle hält die UI den gewählten Modus, bis ein
    // Snapshot ihn bestätigt. Sonst spränge der Schalter zurück, sobald ein noch unterwegs befindlicher
    // Snapshot die alte ManualOverride-Bestätigung trägt. Bleibt die Bestätigung aus (Befehl abgelehnt,
    // z. B. Hardware-Fehler), wird nach MaxPendingStaleTicks die Snapshot-Wahrheit akzeptiert - der
    // Fehlschlag wird sichtbar statt versteckt.
    private bool? _pendingManual;
    private int _pendingStaleTicks;
    private const int MaxPendingStaleTicks = 3;

    public FanRow(string fanId, string name)
    {
        FanId = fanId;
        _name = name;
        _pump = new ManualPwmPump(fanId);
    }

    /// <summary>Verdrahtet die IPC-Steuerbefehle (vom Controller gesetzt). Ohne Bindung bleibt der Lüfter read-only.</summary>
    /// <param name="openCurve">Opens a curve in the editor - the "Controlled by" chip.</param>
    public void BindCommands(
        Func<string, byte, Task>? sendManual, Func<string, Task>? sendAuto, Func<string, Task>? sendCalibrate,
        Action<string>? openCurve = null)
    {
        _pump.Send = sendManual;
        _sendAuto = sendAuto;
        _sendCalibrate = sendCalibrate;
        _openCurve = openCurve;
    }

    [RelayCommand]
    private Task Calibrate() => CanCalibrate ? _sendCalibrate?.Invoke(FanId) ?? Task.CompletedTask : Task.CompletedTask;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Ends the manual override: the curve (or hardware auto without one) takes over again.</summary>
    [RelayCommand(CanExecute = nameof(CanBackToCurve))]
    private void BackToCurve() => IsManual = false;

    private bool CanBackToCurve() => !IsOffline;

    [RelayCommand]
    private void OpenControlCurve()
    {
        if (Control.CurveId is { } curveId)
            _openCurve?.Invoke(curveId);
    }

    /// <param name="fresh">
    /// The reading comes from a new daemon snapshot - the GUI applies the last one every second, also while the daemon
    /// ticks slower, and only a new one may count against a pending switch.
    /// </param>
    public void Update(FanReading f, bool fresh = true)
    {
        Name = f.Name; // Custom-Name kann sich nach dem Speichern ändern → live übernehmen
        bool hasRpm = f.Rpm is { } r && !double.IsNaN(r);
        Rpm = hasRpm ? $"{f.Rpm:0} RPM" : "n/a";
        RpmNumber = hasRpm ? $"{f.Rpm:0}" : "n/a";
        RpmUnit = hasRpm ? "RPM" : "";
        int percent = PwmScale.ToPercent(f.Pwm);
        Pwm = $"pwm {f.Pwm} · {percent}%";
        PercentNum = percent;
        PercentText = $"{percent} %";
        CanControl = f.CanControl;
        ReadOnlyReason = f.ReadOnlyReason;

        bool applyMode = true;
        if (_pendingManual is { } expected)
        {
            if (f.ManualOverride == expected)
            {
                _pendingManual = null; // Daemon hat den Umschaltbefehl bestätigt
                _pendingStaleTicks = 0;
            }
            else if (!fresh || ++_pendingStaleTicks <= MaxPendingStaleTicks)
            {
                applyMode = false; // veralteter Snapshot - optimistischen Modus halten, nicht zurückspringen
            }
            else
            {
                _pendingManual = null; // Bestätigung blieb aus → Realität akzeptieren statt sie zu verstecken
                _pendingStaleTicks = 0;
            }
        }

        _confirmedManual = f.ManualOverride;
        _applyingSnapshot = true;
        if (applyMode)
        {
            // Switched to manual from elsewhere (settings, another window): the slider starts at the PWM being held
            // instead of at a stale value - the next drag would otherwise jump the fan there.
            bool becameManual = f.ManualOverride && !IsManual;
            IsManual = f.ManualOverride;
            if (!f.ManualOverride || becameManual)
                SliderPercent = f.Pwm * 100.0 / 255; // im Auto-Modus folgt der Slider dem Live-Wert (nur Anzeige)
        }
        _applyingSnapshot = false;

        if (hasRpm)
        {
            RpmHistory.Add(f.Rpm!.Value);
            while (RpmHistory.Count > MaxHistory)
                RpmHistory.RemoveAt(0);
        }
    }

    /// <summary>Mirrors the saved calibration result (null = not calibrated).</summary>
    public void SetCalibration(FanCalibration? calibration)
    {
        bool changed = !Equals(_calibration, calibration);
        _calibration = calibration;
        IsCalibrated = calibration is not null;
        if (changed)
        {
            OnPropertyChanged(nameof(CalibrationState));
            OnPropertyChanged(nameof(CalibrationBadgeHint));
        }
    }

    /// <summary>Takes the position from the config (display and the table's order).</summary>
    public void SetPlacement(FanLocation location)
    {
        if (_placement == location)
            return;
        _placement = location;
        LocationRank = FanLocationOption.Rank(location);
        OnPropertyChanged(nameof(Location));
    }

    /// <summary>Re-reads the texts built from the UI language (called every tick, so a language switch reaches them).</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(OverrideNote));
        OnPropertyChanged(nameof(MoreName));
        OnPropertyChanged(nameof(CalibrateTooltip));
        OnPropertyChanged(nameof(ChipTooltip));
        OnPropertyChanged(nameof(CalibrationState));
        OnPropertyChanged(nameof(CalibrationBadgeHint));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(ControlName));
        OnPropertyChanged(nameof(ReadOnlyNote));
    }

    partial void OnIsManualChanged(bool value)
    {
        if (_applyingSnapshot || IsOffline)
            return;
        _pendingManual = value; // erwarteter Zustand, bis ein Snapshot ihn bestätigt
        _pendingStaleTicks = 0;
        if (value)
            _pump.Set(PwmScale.ToPwm(SliderPercent));
        else
        {
            _pump.Stop();
            _ = _sendAuto?.Invoke(FanId);
        }
    }

    partial void OnSliderPercentChanged(double value)
    {
        if (_applyingSnapshot || IsOffline || !IsManual)
            return;
        _pump.Set(PwmScale.ToPwm(value)); // nur den Zielwert merken - die Pumpe sendet gedrosselt
    }

    partial void OnIsOfflineChanged(bool value)
    {
        if (value)
        {
            // A toggle or drag that was still on its way can no longer arrive: drop it and show the mode the daemon
            // confirmed last instead of an optimistic one.
            _pendingManual = null;
            _pendingStaleTicks = 0;
            _pump.Stop();
            _applyingSnapshot = true;
            IsManual = _confirmedManual;
            _applyingSnapshot = false;
        }
        BackToCurveCommand.NotifyCanExecuteChanged();
    }
}
