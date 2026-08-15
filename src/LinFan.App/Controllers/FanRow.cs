// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _rpm = "-";
    [ObservableProperty] private string _pwm = "-";
    [ObservableProperty] private string _control = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private bool _isManual;
    [ObservableProperty] private double _sliderPercent;

    /// <summary>True, sobald für diesen Lüfter eine Kalibrierung vorliegt - steuert das „bereits kalibriert"-Badge
    /// im Dashboard. Wird pro Tick aus der Config gespiegelt (nach Neustart aus der persistierten Kalibrierung).</summary>
    [ObservableProperty] private bool _isCalibrated;

    /// <summary>Tooltip des Kalibrier-Badges - Anlaufpunkt in % (bzw. Hinweis, falls keiner gefunden wurde).</summary>
    [ObservableProperty] private string _calibrationBadgeHint = "";

    /// <summary>True, solange für diesen Lüfter gerade eine Kalibrierung läuft - zeigt einen Lauf-Indikator und
    /// sperrt den Kalibrier-Button (der Gesamtfortschritt erscheint im Kalibrier-Banner über den Tabs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCalibrate))]
    private bool _isCalibrating;

    /// <summary>Kalibrieren ist gesperrt, solange für diesen Lüfter bereits eine Kalibrierung läuft.</summary>
    public bool CanCalibrate => !IsCalibrating;

    /// <summary>Gruppenschlüssel fürs Dashboard (Gruppe, sonst Position, sonst „Ungruppiert").</summary>
    public string GroupKey { get; private set; } = FanGroup.Ungrouped;

    /// <summary>Rollender Drehzahl-Verlauf für die Sparkline.</summary>
    public ObservableCollection<double> RpmHistory { get; } = new();

    private readonly ManualPwmPump _pump;
    private Func<string, Task>? _sendAuto;
    private Func<string, Task>? _sendCalibrate;
    private bool _applyingSnapshot; // unterdrückt das Command-Senden, wenn ein Snapshot den Wert setzt

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
    public void BindCommands(
        Func<string, byte, Task>? sendManual, Func<string, Task>? sendAuto, Func<string, Task>? sendCalibrate)
    {
        _pump.Send = sendManual;
        _sendAuto = sendAuto;
        _sendCalibrate = sendCalibrate;
    }

    [RelayCommand]
    private Task Calibrate() => _sendCalibrate?.Invoke(FanId) ?? Task.CompletedTask;

    public void Update(FanReading f)
    {
        Name = f.Name; // Custom-Name kann sich nach dem Speichern ändern → live übernehmen
        Rpm = f.Rpm is { } r && !double.IsNaN(r) ? $"{r:0} RPM" : "n/a";
        Pwm = $"pwm {f.Pwm} · {PwmScale.ToPercent(f.Pwm)}%";
        Control = f.CanControl ? "steuerbar" : "read-only";
        CanControl = f.CanControl;

        bool applyMode = true;
        if (_pendingManual is { } expected)
        {
            if (f.ManualOverride == expected)
            {
                _pendingManual = null; // Daemon hat den Umschaltbefehl bestätigt
                _pendingStaleTicks = 0;
            }
            else if (++_pendingStaleTicks <= MaxPendingStaleTicks)
            {
                applyMode = false; // veralteter Snapshot - optimistischen Modus halten, nicht zurückspringen
            }
            else
            {
                _pendingManual = null; // Bestätigung blieb aus → Realität akzeptieren statt sie zu verstecken
                _pendingStaleTicks = 0;
            }
        }

        _applyingSnapshot = true;
        if (applyMode)
        {
            IsManual = f.ManualOverride;
            if (!f.ManualOverride)
                SliderPercent = f.Pwm * 100.0 / 255; // im Auto-Modus folgt der Slider dem Live-Wert (nur Anzeige)
        }
        _applyingSnapshot = false;

        if (f.Rpm is { } rpm && !double.IsNaN(rpm))
        {
            RpmHistory.Add(rpm);
            while (RpmHistory.Count > MaxHistory)
                RpmHistory.RemoveAt(0);
        }
    }

    /// <summary>Spiegelt das persistierte Kalibrier-Ergebnis ins Badge (null → nicht kalibriert).</summary>
    public void SetCalibration(FanCalibration? calibration)
    {
        IsCalibrated = calibration is not null;
        CalibrationBadgeHint = calibration is { } cal ? CalibrationBadge.Hint(cal.StartPwm) : "";
    }

    /// <summary>Setzt die Position aus der Konfiguration (für Anzeige &amp; Gruppierung).</summary>
    public void SetPlacement(FanLocation location)
    {
        Location = FanLocationOption.DisplayFor(location);
        GroupKey = location != FanLocation.Unspecified
            ? FanLocationOption.GroupNameFor(location)
            : FanGroup.Ungrouped;
    }

    partial void OnIsManualChanged(bool value)
    {
        if (_applyingSnapshot)
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
        if (_applyingSnapshot || !IsManual)
            return;
        _pump.Set(PwmScale.ToPwm(value)); // nur den Zielwert merken - die Pumpe sendet gedrosselt
    }
}
