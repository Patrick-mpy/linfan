// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>Wird ausgelöst, wenn ein PWM-Vorgang (z. B. Kalibrierung) wegen Übertemperatur abbricht.</summary>
public sealed class OverTemperatureException : Exception
{
    public double TemperatureC { get; }
    public double LimitC { get; }

    public OverTemperatureException(double temperatureC, double limitC)
        : base($"Übertemperatur {temperatureC:0.0} °C ≥ {limitC:0.0} °C - Fail-Safe ausgelöst.")
    {
        TemperatureC = temperatureC;
        LimitC = limitC;
    }
}
