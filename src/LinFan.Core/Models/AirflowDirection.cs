// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Luftstrom-Richtung eines Lüfters, abgeleitet aus seiner <see cref="FanLocation"/>. Nur
/// <see cref="Intake"/>/<see cref="Exhaust"/> zählen zur Gehäuse-Druckbilanz; <see cref="Internal"/>
/// (CPU/GPU/Radiator/Netzteil) zirkuliert intern und <see cref="Unknown"/> hat keine Position.
/// </summary>
public enum AirflowDirection
{
    /// <summary>Keine auswertbare Position (z. B. <see cref="FanLocation.Unspecified"/>/<see cref="FanLocation.Other"/>).</summary>
    Unknown = 0,

    /// <summary>Bläst Luft ins Gehäuse (Front/Boden/Seite).</summary>
    Intake,

    /// <summary>Bläst Luft aus dem Gehäuse (hinten/oben).</summary>
    Exhaust,

    /// <summary>Interner Kühler (CPU/GPU/Radiator/Netzteil) - nicht Teil der Gehäuse-Druckbilanz.</summary>
    Internal,
}
