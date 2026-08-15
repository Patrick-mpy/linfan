// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Persistierte Kurve: Temperatur → Leistung, samt Quell-Sensoren und Hysterese.</summary>
public sealed record CurveConfig
{
    /// <summary>Default smoothing window - the single source for the property, the IPC mapping and the GUI.</summary>
    public const double DefaultSmoothingSeconds = 3.0;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>
    /// Ob die Kurve aktiv regelt. <c>false</c> = stillgelegt: die zugeordneten Lüfter fallen im
    /// <see cref="Services.ControlLoop"/> auf Hardware-Auto zurück (Firmware regelt). Fehlt das Feld in
    /// älterem JSON, deserialisiert es zu <c>true</c> (bisheriges Verhalten - kein Schema-Bump nötig).
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
    /// Sliding window (seconds) the curve's input temperature is averaged over before it is evaluated;
    /// <c>0</c> disables it. Absorbs the short, steep spikes that a hysteresis deadband cannot catch
    /// (see <see cref="Services.TemperatureSmoother"/>). Missing in older JSON, it deserializes to
    /// <see cref="DefaultSmoothingSeconds"/> - existing configurations get the smoothing without a
    /// schema bump. The over-temperature watchdog is unaffected and keeps reading raw values.
    /// </summary>
    public double SmoothingSeconds { get; init; } = DefaultSmoothingSeconds;

    /// <summary>
    /// Interpolationsart zwischen den Stützpunkten. Fehlt das Feld in älterem JSON, deserialisiert es
    /// zu <see cref="InterpolationMode.Linear"/> - dem bisherigen, sicheren Verhalten (kein Schema-Bump nötig).
    /// </summary>
    public InterpolationMode InterpolationMode { get; init; } = InterpolationMode.Linear;

    public IReadOnlyList<CurvePoint> Points { get; init; } = [];
}
