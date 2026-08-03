// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

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
    public string Name { get; }

    /// <summary>Ob der Lüfter steuerbar ist (nur dann ist Identifizieren möglich). Laufzeit-Info aus der Discovery.</summary>
    public bool CanControl { get; }

    /// <summary>Temporäre Manuell-Steuerung (Slider neben dem Identifizieren) — erleichtert die Zuordnung;
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

    public OnboardingFanRow(string fanId, string name, FanLocation location = FanLocation.Unspecified,
                            bool canControl = false, Func<string, Task>? sendIdentify = null,
                            Func<string, byte, Task>? sendManual = null, Func<string, Task>? sendAuto = null)
    {
        FanId = fanId;
        Name = name;
        CanControl = canControl;
        _sendIdentify = sendIdentify;
        Manual = new ManualControl(fanId, canControl, sendManual, sendAuto);
        _location = FanLocationOption.For(location);
    }

    /// <summary>Spiegelt die Live-Drehzahl in den Manuell-Slider (reine Anzeige; pro Tick vom Controller gesetzt).</summary>
    public void SetLiveRpm(double? rpm) => Manual.SetLiveRpm(rpm);

    [RelayCommand]
    private Task Identify() => CanControl ? _sendIdentify?.Invoke(FanId) ?? Task.CompletedTask : Task.CompletedTask;
}
