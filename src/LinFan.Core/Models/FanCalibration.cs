// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Ergebnis der Lüfter-Kalibrierung: Anlaufpunkt, Drehzahlbereich und Roh-Messreihe.</summary>
public sealed record FanCalibration
{
    /// <summary>Kleinster Rohwert, bei dem der Lüfter sicher anläuft.</summary>
    public byte StartPwm { get; init; }

    public int MinRpm { get; init; }
    public int MaxRpm { get; init; }

    public IReadOnlyList<CalibrationSample> Samples { get; init; } = [];
}
