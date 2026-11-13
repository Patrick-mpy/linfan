// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Ergebnis von <see cref="LinFan.Core.Services.AirflowTuneService.Analyze"/>: eine grobe Druckbilanz,
/// vorgeschlagene rollenbasierte Kurven und je Lüfter eine Zuordnung samt Begründung. Reiner Vorschlag -
/// nichts wird geschrieben; was ein Profil davon übernimmt, regelt <see cref="LinFan.Core.Services.AirflowTuneService.Propose"/>.
/// </summary>
public sealed record AirflowTuneResult
{
    /// <summary>The case type the analysis ran for - it decides which positions count and how the verdict reads.</summary>
    public CaseType CaseType { get; init; } = CaseType.Tower;

    /// <summary>The pressure balance of the visible case fans.</summary>
    public PressureVerdict Verdict { get; init; } = PressureVerdict.Unknown;

    /// <summary>Intake share of the case fans' flow weight (0..1); <c>null</c> without any case fan.</summary>
    public double? IntakeShare { get; init; }

    /// <summary>
    /// True when the weights are calibrated maximum speeds (every case fan is calibrated), false when every case fan
    /// counts as one.
    /// </summary>
    public bool ByRpm { get; init; }

    /// <summary>Number of visible intake case fans.</summary>
    public int IntakeFans { get; init; }

    /// <summary>Number of visible exhaust case fans.</summary>
    public int ExhaustFans { get; init; }

    /// <summary>The visible case fans without a calibrated maximum speed - they keep the balance on the count basis.</summary>
    public IReadOnlyList<string> UncalibratedCaseFanIds { get; init; } = [];

    /// <summary>Summiertes Einlass-„Flow-Gewicht" (kalibrierte Max-RPM oder Anzahl als Fallback).</summary>
    public double IntakeWeight { get; init; }

    /// <summary>Summiertes Auslass-„Flow-Gewicht".</summary>
    public double ExhaustWeight { get; init; }

    /// <summary>
    /// The visible fans with a position the case type does not have (a top fan in a server, a front fan in a laptop, any
    /// case fan on an open bench). They keep their role curve but stay out of the pressure balance until they get a
    /// position of the type.
    /// </summary>
    public IReadOnlyList<string> OutOfCaseFanIds { get; init; } = [];

    /// <summary>
    /// The sides with intake and exhaust fans at once (case fans of the type only) - not in a server (see
    /// <see cref="AgainstFlowFanIds"/>) nor in a laptop, which has no balance.
    /// </summary>
    public IReadOnlyList<AirflowMixedSide> MixedSides { get; init; } = [];

    /// <summary>Server only: the case fans that blow against the usual front-to-rear flow (front exhaust, rear intake).</summary>
    public IReadOnlyList<string> AgainstFlowFanIds { get; init; } = [];

    /// <summary>Hinweis-Codes (z. B. „kein Einlasslüfter", „ohne Kalibrierung geschätzt") - die GUI übersetzt.</summary>
    public IReadOnlyList<AirflowHint> Hints { get; init; } = [];

    /// <summary>Die rollenbasierten Kurven, auf die sich die Lüfter-Vorschläge beziehen (nur tatsächlich genutzte Rollen).</summary>
    public IReadOnlyList<CurveConfig> SuggestedCurves { get; init; } = [];

    /// <summary>Pro Lüfter ein Vorschlag (Richtung + Kurve + Begründung).</summary>
    public IReadOnlyList<AirflowFanSuggestion> Fans { get; init; } = [];
}
