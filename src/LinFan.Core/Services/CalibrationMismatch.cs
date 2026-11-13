// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// How a fan disagrees with its stored calibration (<see cref="CalibrationPlausibility"/>) - a sign that the calibration
/// belongs to another fan, such as one swapped on the same header. A notice for the user, never an input to control.
/// </summary>
public enum CalibrationMismatch
{
    /// <summary>
    /// The fan stands at a PWM where the calibration says it turns - a fan that does not start at the old start point.
    /// </summary>
    Stalls,

    /// <summary>The fan turns markedly slower than the calibration says it does at this PWM.</summary>
    Slower,

    /// <summary>The fan turns markedly faster than the calibration says it does at this PWM.</summary>
    Faster,

    /// <summary>
    /// The calibration measured with another speed sensor than the one the fan's speed is read from now, so its series
    /// may describe another fan.
    /// </summary>
    OtherSensor,
}
