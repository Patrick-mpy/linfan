// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Ein Stützpunkt einer Lüfterkurve: bei <paramref name="TemperatureC"/> °C → <paramref name="Percent"/> % Leistung.</summary>
public readonly record struct CurvePoint(double TemperatureC, double Percent);
