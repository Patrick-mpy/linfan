// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Vorschlag des Airflow-Auto-Tune für einen einzelnen Lüfter: welche Rolle/Richtung erkannt wurde
/// und welche Kurve er bekommen soll. Rein beschreibend - erst <see cref="LinFan.Core.Services.AirflowTuneService.Apply"/>
/// schreibt das in eine <see cref="AppConfig"/>.
/// </summary>
public sealed record AirflowFanSuggestion
{
    public string FanId { get; init; } = "";

    /// <summary>Die (unveränderte) Einbau-Position, aus der der Vorschlag abgeleitet wurde.</summary>
    public FanLocation Location { get; init; } = FanLocation.Unspecified;

    public AirflowDirection Direction { get; init; } = AirflowDirection.Unknown;

    /// <summary>Id der vorgeschlagenen Kurve, oder <c>null</c> = auf Hardware-Auto lassen (z. B. Netzteil).</summary>
    public string? SuggestedCurveId { get; init; }

    /// <summary>Begründungs-Code (für die GUI-Vorschau) - die GUI formatiert den Anzeigetext daraus.</summary>
    public AirflowReason Reason { get; init; } = AirflowReason.LocationBasedCurve;
}
