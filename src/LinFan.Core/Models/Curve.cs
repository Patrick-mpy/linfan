// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Lüfterkurve: Temperatur → Leistung in Prozent. Punkte müssen nicht sortiert übergeben werden.</summary>
public sealed record Curve(
    string Name,
    IReadOnlyList<CurvePoint> Points,
    InterpolationMode InterpolationMode = InterpolationMode.Linear);
