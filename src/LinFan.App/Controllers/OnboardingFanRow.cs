// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Leichte Zeile für einen Lüfter im Onboarding-Assistenten: trägt die Hardware-Id, den Anzeigenamen
/// und die (optionale) Einbau-Position. Die Position ist beobachtbar, damit das Positions-Modal sie
/// live zurückschreiben kann. Steuerbare Lüfter teilen dieselbe Instanz mit der Kalibrier-Liste und
/// lassen sich von hier aus identifizieren (kurz auf 100 %, andere gedrosselt).
/// </summary>
public partial class OnboardingFanRow : ObservableObject
{
    private readonly Func<string, Task>? _sendIdentify;

    public string FanId { get; }

    /// <summary>The fan's own name - empty while it has none; then <see cref="DefaultName"/> is what Finish saves.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    /// <summary>„Lüfter N" (<see cref="DefaultFanNames"/>), set by the controller: placeholder of the name field.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _defaultName = "";

    /// <summary>The name every step shows: the own one, or the one Finish saves for an empty field.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DefaultName : Name.Trim();

    /// <summary>The limit the daemon checks a name against - it rejects the whole config over a longer one.</summary>
    public int MaxNameLength => ConfigLimits.MaxTextLength;

    /// <summary>Ob der Lüfter steuerbar ist (nur dann ist Identifizieren möglich). Laufzeit-Info aus der Discovery.</summary>
    public bool CanControl { get; }

    /// <summary>Temporäre Manuell-Steuerung (Slider neben dem Identifizieren) - erleichtert die Zuordnung;
    /// beim Verlassen des Geräte-Schritts setzt der Controller sie zurück.</summary>
    public ManualControl Manual { get; }

    /// <summary>Gewählte Einbau-Position (optional; Default <see cref="FanLocation.Unspecified"/>).</summary>
    [ObservableProperty] private FanLocationOption _location;

    /// <summary>Status für die Anzeige im Einrichtungs-Schritt (koppeln/kalibrieren → Icon + Kurztext).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationStateText))]
    private OnboardingCalibrationState _calibrationState;

    /// <summary>
    /// Lokalisierter Kurztext zum aktuellen <see cref="CalibrationState"/> (neben dem Namen). Bleibt auch nach
    /// der Sequenz stehen, damit übersprungene Lüfter (kein Tacho / nicht eindeutig) sichtbar bleiben. Pending → leer.
    /// </summary>
    public string CalibrationStateText => CalibrationState switch
    {
        OnboardingCalibrationState.Coupling => Localizer.Instance["OnboardingFanRow.Coupling"],
        OnboardingCalibrationState.Running => Localizer.Instance["OnboardingFanRow.Calibrating"],
        OnboardingCalibrationState.Done => Localizer.Instance["OnboardingFanRow.Done"],
        OnboardingCalibrationState.NoTacho => Localizer.Instance["OnboardingFanRow.NoTacho"],
        OnboardingCalibrationState.Ambiguous => Localizer.Instance["OnboardingFanRow.Ambiguous"],
        OnboardingCalibrationState.Failed => Localizer.Instance["OnboardingFanRow.Failed"],
        _ => "",
    };

    /// <param name="name">The fan's own name from the config; empty if it has none.</param>
    public OnboardingFanRow(string fanId, string name, FanLocation location = FanLocation.Unspecified,
                            bool canControl = false, Func<string, Task>? sendIdentify = null,
                            Func<string, byte, Task>? sendManual = null, Func<string, Task>? sendAuto = null)
    {
        FanId = fanId;
        _name = name;
        CanControl = canControl;
        _sendIdentify = sendIdentify;
        Manual = new ManualControl(fanId, canControl, sendManual, sendAuto);
        _location = FanLocationOption.For(location);
    }

    /// <summary>Mirrors live speed, power and the daemon's manual override into the manual slider (every tick).</summary>
    /// <param name="manualOverride">Whether the daemon holds a manual override for the fan; <c>null</c> = not known.</param>
    public void SetLive(double? rpm, byte? pwm, bool? manualOverride = null)
    {
        Manual.SetLiveRpm(rpm);
        Manual.SetLivePwm(pwm);
        if (manualOverride is { } held)
            Manual.ApplyDaemonOverride(held);
    }

    /// <summary>
    /// Why the daemon would refuse to identify the fan or take it over by hand right now (see
    /// <see cref="DeviceActionBlock"/>); empty when it would not. Locks both, the reason is the identify tooltip. Set by
    /// the controller every tick.
    /// </summary>
    public string DeviceActionsBlockedReason
    {
        get => _deviceActionsBlockedReason;
        set
        {
            if (!SetProperty(ref _deviceActionsBlockedReason, value))
                return;
            Manual.EngageBlocked = value.Length > 0;
            OnPropertyChanged(nameof(IdentifyTooltip));
            IdentifyCommand.NotifyCanExecuteChanged();
        }
    }

    private string _deviceActionsBlockedReason = "";

    public string IdentifyTooltip => DeviceActionsBlockedReason.Length > 0
        ? DeviceActionsBlockedReason
        : Localizer.Instance["Onboarding.IdentifyTooltip"];

    [RelayCommand(CanExecute = nameof(CanIdentify))]
    private Task Identify() => CanIdentify() ? _sendIdentify?.Invoke(FanId) ?? Task.CompletedTask : Task.CompletedTask;

    private bool CanIdentify() => CanControl && DeviceActionsBlockedReason.Length == 0;

    /// <summary>The language changed: the texts built here have to be read again.</summary>
    internal void NotifyLanguageChanged() => OnPropertyChanged(nameof(IdentifyTooltip));
}
