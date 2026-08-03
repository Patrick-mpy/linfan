// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LinFan.App.Controllers;

/// <summary>
/// Eine aktive Kurve im Dashboard-Panel „Aktive Kurven": verbindet sichtbar Quell-Sensor → Kurve →
/// geregelte Lüfter und lässt die Kurve live an-/ausschalten. Liest live über die referenzierte
/// <see cref="CurveEditRow"/> (Name, Arbeitstemperatur) und die zugeordneten <see cref="FanRow"/>
/// (Drehzahl/PWM) — wird vom <see cref="MainController"/> neu aufgebaut, wenn sich Zuordnung/Quelle ändert.
/// Reine Presentation-Mechanik; das Schalten delegiert an den Controller (IPC).
/// </summary>
public sealed partial class DashboardCurveRow : ObservableObject
{
    /// <summary>Die zugrundeliegende Kurve (Single Source of Truth für Name &amp; Live-Arbeitspunkt).</summary>
    public CurveEditRow Curve { get; }

    /// <summary>Kurz-Beschreibung der Quell-Sensoren (Name bzw. „n Sensoren").</summary>
    public string SourceSummary { get; }

    /// <summary>Die von dieser Kurve geregelten (sichtbaren) Lüfter — für Live-Drehzahl/PWM.</summary>
    public IReadOnlyList<FanRow> Fans { get; }

    private readonly Action<bool> _onToggle;
    private readonly Action _onEdit;

    /// <summary>An/Aus-Zustand für den Toggle; eine Nutzer-Umschaltung löst <see cref="_onToggle"/> aus.</summary>
    [ObservableProperty] private bool _enabled;

    public DashboardCurveRow(CurveEditRow curve, IReadOnlyList<FanRow> fans, Action<bool> onToggle, Action onEdit)
    {
        Curve = curve;
        Fans = fans;
        _onToggle = onToggle;
        _onEdit = onEdit;
        _enabled = curve.Enabled;
        SourceSummary = curve.Sources.Count switch
        {
            0 => "—",
            1 => curve.Sources[0].Name,
            int n => $"{n} Sensoren",
        };
    }

    partial void OnEnabledChanged(bool value) => _onToggle(value);

    /// <summary>Wechselt zur Kurve im Kurven-Tab (Auswahl + Tab-Wechsel im Controller).</summary>
    [RelayCommand]
    private void Edit() => _onEdit();
}
