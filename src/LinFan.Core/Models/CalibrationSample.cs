// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Eine Messung während der Kalibrierung: bei Rohwert <paramref name="Pwm"/> drehte der Lüfter <paramref name="Rpm"/>.</summary>
public readonly record struct CalibrationSample(byte Pwm, int Rpm);
