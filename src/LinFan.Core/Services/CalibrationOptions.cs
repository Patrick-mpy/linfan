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

    /// <summary>Ab dieser Drehzahl gilt der Lüfter als „dreht" (für den Anlaufpunkt).</summary>
    public int SpinThresholdRpm { get; init; } = 100;

    /// <summary>Vorgabe der Temperatur-Obergrenze, wenn keine aus der Config durchgereicht wird.</summary>
    public const double DefaultFailSafeTempC = AppConfig.DefaultFailSafeTempC; // zentrale Quelle: AppConfig

    /// <summary>Temperatur-Obergrenze: darüber wird die Kalibrierung abgebrochen (Fail-Safe).</summary>
    public double FailSafeTempC { get; init; } = DefaultFailSafeTempC;

    /// <summary>
    /// Übersteuert den vom Backend gepaarten Tacho für die RPM-Messung (aus <see cref="Models.FanConfig.RpmSource"/>).
    /// <c>null</c> = den Tacho des <c>FanDescriptor</c> verwenden.
    /// </summary>
    public SensorId? TachometerOverride { get; init; }
}
