// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>Parameter der Lüfter-Kalibrierung.</summary>
public sealed record CalibrationOptions
{
    /// <summary>PWM-Schrittweite der Rampe (0..255).</summary>
    public int StepSize { get; init; } = 32;

    /// <summary>Wartezeit nach jedem Schritt, bis sich die Drehzahl eingependelt hat.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the fan may run out at PWM 0 before the first sample - it comes from hardware auto, possibly at full
    /// speed. Longer than the tach coupling's baseline settle (6 s): a large fan takes a while to stop.
    /// </summary>
    public TimeSpan RunOutTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Vorgabe der Temperatur-Obergrenze, wenn keine aus der Config durchgereicht wird.</summary>
    public const double DefaultFailSafeTempC = AppConfig.DefaultFailSafeTempC; // zentrale Quelle: AppConfig

    /// <summary>Temperatur-Obergrenze: darüber wird die Kalibrierung abgebrochen (Fail-Safe).</summary>
    public double FailSafeTempC { get; init; } = DefaultFailSafeTempC;

    /// <summary>
    /// The configured fans, for their speed-sensor couplings: the calibration measures with the sensor
    /// <see cref="FanTachometers"/> gives the fan - its own coupling, else the backend's pairing unless another fan is
    /// coupled to that sensor. Empty = no couplings, the backend's pairing counts.
    /// </summary>
    public IReadOnlyList<FanConfig> Couplings { get; init; } = [];
}
