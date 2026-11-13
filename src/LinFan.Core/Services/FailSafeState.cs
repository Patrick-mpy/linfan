// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// The fail-safe as the watchdog of the last <see cref="ControlLoop.Tick"/> sees it. Set in the tick that trips it
/// and kept while no temperature is readable since (nothing has taken the fans out of the safe state then); the
/// first tick with a readable temperature below the limit clears it.
/// </summary>
public enum FailSafeState
{
    /// <summary>No fail-safe in force.</summary>
    None,

    /// <summary>A temperature reached the fail-safe limit; the fans were put on firmware auto.</summary>
    OverTemperature,

    /// <summary>
    /// No temperature was readable for several ticks while fans were controlled, or a sensor that last read near the
    /// limit became unreadable; the fans were put on firmware auto.
    /// </summary>
    Blind,

    /// <summary>
    /// The tick itself failed (an internal daemon error), so the watchdog did not run; the daemon put the fans on
    /// firmware auto. Set by the daemon, never by <see cref="ControlLoop.Tick"/>.
    /// </summary>
    Error,
}
