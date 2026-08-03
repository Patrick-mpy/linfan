// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Persistierte Kurve: Temperatur → Leistung, samt Quell-Sensoren und Hysterese.</summary>
public sealed record CurveConfig
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>
    /// Ob die Kurve aktiv regelt. <c>false</c> = stillgelegt: die zugeordneten Lüfter fallen im
    /// <see cref="Services.ControlLoop"/> auf Hardware-Auto zurück (Firmware regelt). Fehlt das Feld in
    /// älterem JSON, deserialisiert es zu <c>true</c> (bisheriges Verhalten — kein Schema-Bump nötig).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Altes Einzel-Quellfeld (Schema 1). Nur noch für die Migration vorhanden: ist es gesetzt und
    /// <see cref="SourceSensorIds"/> leer, wird es in <see cref="SourceSensorIds"/> überführt.
    /// </summary>
    public string? SourceSensorId { get; init; }

    /// <summary>Ids der Temperatursensoren, die diese Kurve speisen (per <see cref="Aggregation"/> zusammengefasst).</summary>
    public IReadOnlyList<string> SourceSensorIds { get; init; } = [];

    /// <summary>Wie mehrere Quell-Sensoren zu einem Eingangswert zusammengefasst werden.</summary>
    public SensorAggregation Aggregation { get; init; } = SensorAggregation.Max;

    /// <summary>Mindest-Temperaturänderung (°C), bevor der PWM-Wert nachgeführt wird (gegen Pendeln).</summary>
    public double HysteresisC { get; init; } = 2.0;

    /// <summary>
    /// Interpolationsart zwischen den Stützpunkten. Fehlt das Feld in älterem JSON, deserialisiert es
    /// zu <see cref="InterpolationMode.Linear"/> – dem bisherigen, sicheren Verhalten (kein Schema-Bump nötig).
    /// </summary>
    public InterpolationMode InterpolationMode { get; init; } = InterpolationMode.Linear;

    public IReadOnlyList<CurvePoint> Points { get; init; } = [];
}
