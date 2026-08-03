// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Wie mehrere Quell-Sensoren einer Kurve zu einem Eingangswert zusammengefasst werden.
/// <see cref="Max"/> ist der sichere Default für Kühlung (der heißeste Sensor bestimmt die Drehzahl).
/// </summary>
public enum SensorAggregation
{
    /// <summary>Heißester Sensor gewinnt (sicher für Kühlung).</summary>
    Max = 0,

    /// <summary>Mittelwert über alle lesbaren Sensoren.</summary>
    Avg = 1,
}
