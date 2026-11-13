// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Ergebnis der Lüfter-Kalibrierung: Anlaufpunkt, Drehzahlbereich und Roh-Messreihe.</summary>
public sealed record FanCalibration
{
    /// <summary>The <see cref="StartPwm"/> a calibration stores when the fan never started.</summary>
    public const byte NoSafeStart = 255;

    /// <summary>The lowest raw PWM at which the fan starts reliably (<see cref="NoSafeStart"/>: none found).</summary>
    public byte StartPwm { get; init; }

    public int MinRpm { get; init; }
    public int MaxRpm { get; init; }

    /// <summary>
    /// The raw series in ramp order, the run-out samples before the first standing one included
    /// (<c>CalibrationService.DrivenSamples</c> leaves them out).
    /// </summary>
    public IReadOnlyList<CalibrationSample> Samples { get; init; } = [];

    /// <summary>
    /// The speed sensor the calibration measured with, <c>null</c> for a calibration from before it was recorded. If the
    /// fan's speed is read from another sensor now, the series describes that sensor's fan, not necessarily this one.
    /// </summary>
    public string? Tachometer { get; init; }
}
