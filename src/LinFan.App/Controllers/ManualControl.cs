// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Temporäre Manuell-Steuerung eines Lüfters als Zuordnungs-/Identifikationshilfe (Geräte-Tab, Onboarding,
/// Positions-Modal). Anders als die Dashboard-Steuerung ist sie „auf Zeit" gedacht: beim Verlassen der Fläche
/// ruft die jeweilige Stelle <see cref="Revert"/> → zurück auf Kurve/Hardware-Auto. Zusätzlich verwirft der
/// Daemon manuelle Overrides ohnehin beim letzten GUI-Disconnect, sodass kein Override vergessen liegen bleibt.
/// Sendet gedrosselt über die geteilte <see cref="ManualPwmPump"/>; <see cref="LiveRpm"/> wird vom Poll-Loop gespeist.
/// </summary>
public partial class ManualControl : ObservableObject
{
    private readonly string _fanId;
    private readonly ManualPwmPump _pump;
    private readonly Func<string, Task>? _sendAuto;

    /// <summary>Nur steuerbare Lüfter zeigen den Slider (Gating in der View). Read-only-Kanäle bleiben außen vor.</summary>
    public bool CanControl { get; }

    private bool _isActive;

    // Optimistic like FanRow: after engaging, the switch holds "manual" until a snapshot confirms the override. If the
    // confirmation never comes (command refused), or the daemon drops the override later (fail-safe, restart), the
    // switch follows the daemon back - without sending anything, there is nothing to hand back.
    private bool _engagePending;
    private int _pendingStaleTicks;
    private const int MaxPendingStaleTicks = 3;

    /// <summary>
    /// Slider engagiert? <c>true</c> → manuell geregelt, <c>false</c> → zurück auf Kurve/Hardware-Auto. Engaging is
    /// refused while <see cref="EngageBlocked"/> (the binding is told the old value again); letting go always works.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value && !_isActive && EngageBlocked)
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModeIndex));
                return;
            }
            if (SetActive(value))
                OnIsActiveChanged(value);
        }
    }

    private bool SetActive(bool value)
    {
        if (!SetProperty(ref _isActive, value, nameof(IsActive)))
            return false;
        OnPropertyChanged(nameof(ModeIndex));
        OnPropertyChanged(nameof(ShownPercent));
        OnPropertyChanged(nameof(ShownPercentText));
        return true;
    }

    /// <summary>
    /// The fan may not be taken over by hand right now: the service is offline (a command would be dropped while the
    /// switch claimed "manual"), runs dry (it writes no fan), or a calibration, identification or coupling runs - the
    /// daemon would only apply the setting once that releases the fan. Set by the owning row (see DeviceActionBlock).
    /// </summary>
    [ObservableProperty] private bool _engageBlocked;

    /// <summary>Slider-Stellung in Prozent (0-100).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownPercent), nameof(ShownPercentText))]
    private double _percent;

    /// <summary>Formatierte Live-Drehzahl (reine Anzeige) - gespeist vom Poll-Loop der jeweiligen Fläche.</summary>
    [ObservableProperty] private string _liveRpm = "-";

    /// <summary>The live speed as a bare number ("1180", "n/a") for the large readout next to the test slider.</summary>
    [ObservableProperty] private string _liveRpmNumber = "-";

    /// <summary>
    /// The power the fan runs at right now, <c>null</c> while unknown. Only a power the daemon wrote itself counts (a
    /// curve or manual control): under hardware auto, the fail-safe or a running calibration the reported PWM is 0,
    /// stale or a throttled value - not what the fan does.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownPercent), nameof(ShownPercentText))]
    private double? _livePercent;

    /// <summary>Segment switch "Curve | Manual": 0 = curve (or hardware auto), 1 = manual.</summary>
    public int ModeIndex
    {
        get => IsActive ? 1 : 0;
        set => IsActive = value == 1;
    }

    /// <summary>
    /// What the test slider shows: the manual setting while manual, else the live power - so switching to manual
    /// starts where the fan already is. Writing only takes while manual.
    /// </summary>
    public double ShownPercent
    {
        get => IsActive ? Percent : LivePercent ?? 0;
        set
        {
            if (IsActive)
                Percent = value;
        }
    }

    /// <summary>The value beside the test slider: „40 %", or a dash while the live power is unknown.</summary>
    public string ShownPercentText => IsActive || LivePercent is not null
        ? string.Create(CultureInfo.InvariantCulture, $"{ShownPercent:0} %")
        : "-";

    public ManualControl(string fanId, bool canControl,
                         Func<string, byte, Task>? sendManual, Func<string, Task>? sendAuto)
    {
        _fanId = fanId;
        CanControl = canControl;
        _pump = new ManualPwmPump(fanId) { Send = sendManual };
        _sendAuto = sendAuto;
    }

    /// <summary>Test-Naht: Drossel-Intervall der Pumpe (in Tests auf <c>Zero</c>).</summary>
    internal TimeSpan Throttle { set => _pump.Throttle = value; }

    /// <summary>Test-Naht: läuft, solange die Pumpe noch sendet.</summary>
    internal Task PumpCompletion => _pump.Completion;

    private void OnIsActiveChanged(bool value)
    {
        if (!CanControl)
            return; // read-only: nie engagieren (die View zeigt den Schalter ohnehin nicht)
        if (value)
        {
            _engagePending = true;
            _pendingStaleTicks = 0;
            // Start where the fan runs now, so engaging does not change its speed by itself. Without a known power
            // start at full speed - the safe side - instead of the last (initially 0 %) setting.
            Percent = LivePercent is { } live ? Math.Round(live) : 100;
            _pump.Set(PwmScale.ToPwm(Percent));
        }
        else
        {
            _pump.Stop();
            _ = _sendAuto?.Invoke(_fanId);
        }
    }

    partial void OnPercentChanged(double value)
    {
        if (IsActive && CanControl)
            _pump.Set(PwmScale.ToPwm(value));
    }

    /// <summary>Spiegelt die Live-Drehzahl (NaN/null → „n/a").</summary>
    public void SetLiveRpm(double? rpm)
    {
        LiveRpm = RpmText.Of(rpm);
        LiveRpmNumber = RpmText.Number(rpm);
    }

    /// <summary>Mirrors the raw PWM the daemon drives the fan with (<c>null</c> = unknown) as <see cref="LivePercent"/>.</summary>
    public void SetLivePwm(byte? pwm) => LivePercent = pwm is { } p ? PwmScale.ToPercent(p) : null;

    /// <summary>
    /// Mirrors whether the daemon holds a manual override for the fan. Fed by the owning row with each <b>new</b> daemon
    /// snapshot only - the GUI re-applies the last one every second, also while the daemon ticks slower, and counting
    /// those would end the grace period before the confirmation could arrive. While the switch is on and the daemon
    /// reports none - past the grace ticks after engaging - the switch goes off.
    /// </summary>
    public void ApplyDaemonOverride(bool manualOverride)
    {
        if (!_isActive || manualOverride)
        {
            _engagePending = false;
            _pendingStaleTicks = 0;
            return;
        }
        if (_engagePending && ++_pendingStaleTicks <= MaxPendingStaleTicks)
            return; // the command may still be on its way
        _engagePending = false;
        _pendingStaleTicks = 0;
        _pump.Stop();
        SetActive(false);
    }

    /// <summary>Beim Verlassen der Fläche (Sektion/Schritt/Dialog): Manuell beenden, zurück auf Auto/Kurve.</summary>
    public void Revert()
    {
        if (IsActive)
            IsActive = false; // löst OnIsActiveChanged → Stop + sendAuto
    }
}
