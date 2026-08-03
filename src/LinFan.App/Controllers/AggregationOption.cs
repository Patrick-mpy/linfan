// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>Anzeige-Option für <see cref="SensorAggregation"/> im Editor (Enum + lokalisierter Klartext).</summary>
public sealed record AggregationOption(SensorAggregation Value, string Key)
{
    public static readonly IReadOnlyList<AggregationOption> All = new[]
    {
        new AggregationOption(SensorAggregation.Max, "AggregationOption.Max"),
        new AggregationOption(SensorAggregation.Avg, "AggregationOption.Avg"),
    };

    /// <summary>Berechneter Lookup, damit ein Sprachwechsel den Anzeigetext live aktualisiert.</summary>
    public string Display => Localizer.Instance[Key];

    public static AggregationOption For(SensorAggregation value) =>
        All.FirstOrDefault(o => o.Value == value) ?? All[0];

    public override string ToString() => Display;
}
