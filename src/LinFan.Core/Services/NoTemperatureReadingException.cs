// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// Watchdog-Abbruch: während einer PWM-Rampe oder eines Identifikations-Pulses war keine Temperatur
/// lesbar, daher ist kein Übertemperatur-Watchdog möglich - die Aktion wird abgebrochen (Fail-Safe:
/// keine Rampe ohne Watchdog). Parallel zu <see cref="OverTemperatureException"/>; typisiert (statt
/// generischer <see cref="InvalidOperationException"/> mit deutschem Text), damit der Daemon die Ursache
/// codifiziert über IPC überträgt und die GUI sie lokalisiert.
/// </summary>
public sealed class NoTemperatureReadingException : Exception
{
    public NoTemperatureReadingException(string message) : base(message) { }
}
