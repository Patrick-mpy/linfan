// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>Parameter der automatischen Sensor-Kopplung.</summary>
public sealed record TachMappingOptions
{
    /// <summary>PWM, auf den der Ziel-Lüfter zum Messen hochgetrieben wird (großer Hub = klares Signal).</summary>
    public byte DrivePwm { get; init; } = 255;

    /// <summary>Wartezeit, bis sich die Drehzahl nach dem Hoch-/Runtertreiben eingependelt hat.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Mindest-Drehzahlanstieg, damit ein Sensor als „reagiert" gilt. Darunter ⇒
    /// <see cref="TachMappingOutcome.NoResponse"/> (Lüfter ohne Tacho).
    /// </summary>
    public int MinRiseRpm { get; init; } = 150;

    /// <summary>
    /// Der stärkste Sensor muss um diesen Faktor über dem zweitstärksten liegen, sonst gilt das Ergebnis als
    /// <see cref="TachMappingOutcome.Ambiguous"/> (Luft-Übersprechen). Größer = strenger.
    /// </summary>
    public double DominanceFactor { get; init; } = 2.0;

    /// <summary>Temperatur-Obergrenze: darüber wird die Kopplung abgebrochen (Fail-Safe, Watchdog).</summary>
    public double FailSafeTempC { get; init; } = AppConfig.DefaultFailSafeTempC;
}
