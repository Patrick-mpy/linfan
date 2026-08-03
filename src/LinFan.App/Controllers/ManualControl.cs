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

    /// <summary>Slider engagiert? <c>true</c> → manuell geregelt, <c>false</c> → zurück auf Kurve/Hardware-Auto.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Slider-Stellung in Prozent (0–100).</summary>
    [ObservableProperty] private double _percent;

    /// <summary>Formatierte Live-Drehzahl (reine Anzeige) — gespeist vom Poll-Loop der jeweiligen Fläche.</summary>
    [ObservableProperty] private string _liveRpm = "—";

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

    partial void OnIsActiveChanged(bool value)
    {
        if (!CanControl)
            return; // read-only: nie engagieren (die View zeigt den Schalter ohnehin nicht)
        if (value)
            _pump.Set(PwmScale.ToPwm(Percent));
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
    public void SetLiveRpm(double? rpm) =>
        LiveRpm = rpm is { } r && !double.IsNaN(r)
            ? string.Create(CultureInfo.InvariantCulture, $"{r:0} RPM")
            : "n/a";

    /// <summary>Beim Verlassen der Fläche (Sektion/Schritt/Dialog): Manuell beenden, zurück auf Auto/Kurve.</summary>
    public void Revert()
    {
        if (IsActive)
            IsActive = false; // löst OnIsActiveChanged → Stop + sendAuto
    }
}
