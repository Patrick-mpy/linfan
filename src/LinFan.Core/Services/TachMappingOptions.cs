// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>Parameter der automatischen Sensor-Kopplung.</summary>
public sealed record TachMappingOptions
{
    /// <summary>PWM, auf den der Ziel-Lüfter zum Messen hochgetrieben wird (großer Hub = klares Signal).</summary>
    public byte DrivePwm { get; init; } = 255;

    /// <summary>Wartezeit, bis sich die Drehzahl nach dem Hochtreiben eingependelt hat.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Wait before the <b>baseline</b> reading (every fan at PWM 0). Deliberately longer than
    /// <see cref="SettleTime"/>: coasting down takes far longer than spinning up, and an inert fan (a large
    /// CPU cooler coming from full speed) is barely slower after 3 s. Its baseline would then sit near the
    /// final speed and the measured rise near 0 - the fan would be misreported as having no tachometer.
    /// </summary>
    public TimeSpan BaselineSettleTime { get; init; } = TimeSpan.FromSeconds(6);

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

    /// <summary>
    /// Safety margin below <see cref="FailSafeTempC"/> required to <b>start</b> at all. The run parks every
    /// controllable fan near PWM 0 for the whole measurement - the longest near-zero-airflow window in the
    /// product. Starting at just under the limit would spend it climbing straight into the watchdog, so the
    /// entry check is stricter than the abort check. Once running, the abort still uses the full limit.
    /// </summary>
    public double StartMarginC { get; init; } = 10;
}
