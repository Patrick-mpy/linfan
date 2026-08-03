// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Ergebnis von <see cref="LinFan.Core.Services.AirflowTuneService.Analyze"/>: eine grobe Druckbilanz,
/// vorgeschlagene rollenbasierte Kurven und je Lüfter eine Zuordnung samt Begründung. Reiner Vorschlag –
/// nichts wird geschrieben, bis die GUI ihn über <see cref="LinFan.Core.Services.AirflowTuneService.Apply"/>
/// übernimmt.
/// </summary>
public sealed record AirflowTuneResult
{
    public PressureBalance Pressure { get; init; } = PressureBalance.Unknown;

    /// <summary>Summiertes Einlass-„Flow-Gewicht" (kalibrierte Max-RPM oder Anzahl als Fallback).</summary>
    public double IntakeWeight { get; init; }

    /// <summary>Summiertes Auslass-„Flow-Gewicht".</summary>
    public double ExhaustWeight { get; init; }

    /// <summary>Menschenlesbare Hinweise/Warnungen (z. B. „kein Einlasslüfter", „ohne Kalibrierung geschätzt").</summary>
    public IReadOnlyList<string> Hints { get; init; } = [];

    /// <summary>Die rollenbasierten Kurven, auf die sich die Lüfter-Vorschläge beziehen (nur tatsächlich genutzte Rollen).</summary>
    public IReadOnlyList<CurveConfig> SuggestedCurves { get; init; } = [];

    /// <summary>Pro Lüfter ein Vorschlag (Richtung + Kurve + Begründung).</summary>
    public IReadOnlyList<AirflowFanSuggestion> Fans { get; init; } = [];
}
