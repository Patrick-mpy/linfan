// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>Ergebnis-Art einer automatischen Sensor-Kopplung (<see cref="TachometerMappingService"/>).</summary>
public enum TachMappingOutcome
{
    /// <summary>Genau ein Drehzahl-Sensor reagierte dominant auf den angetriebenen Lüfter → zugeordnet.</summary>
    Matched,

    /// <summary>Kein Sensor reagierte spürbar (Lüfter ohne Tacho, z. B. AIO-Pumpe) - kein Fehler.</summary>
    NoResponse,

    /// <summary>Mehrere Sensoren reagierten ähnlich stark (Luft-Übersprechen) - nicht eindeutig zuordenbar.</summary>
    Ambiguous,
}
