// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// Who controlled a fan in the last <see cref="ControlLoop.Tick"/>: what the daemon actually did, so the GUI can
/// show it instead of inferring it from the config. A dry run has no states of its own - <see cref="Curve"/> and
/// <see cref="HardwareAuto"/> then mean "would be", nothing is written.
/// </summary>
public enum FanControlState
{
    /// <summary>The assigned curve drives the fan: its value was written this tick or, within the hysteresis, kept.</summary>
    Curve,

    /// <summary>
    /// The assigned curve is on and has driven the fan, but none of its sources is readable now. The loop leaves the
    /// fan alone, so it keeps the curve's last value. A curve that has not driven the fan since it was last reset
    /// (manual release, profile switch, config change, coordinator release, fail-safe) hands it back instead:
    /// <see cref="HardwareAuto"/>.
    /// </summary>
    CurveHolding,

    /// <summary>A fixed PWM from the GUI overrides the curve.</summary>
    Manual,

    /// <summary>
    /// No curve, an unknown one, one switched off, or one without a readable source that has not driven the fan yet
    /// (see <see cref="CurveHolding"/>): the fan is set to firmware auto. A channel without an auto mode
    /// runs at full speed instead: the loop writes it once when it hands back a fan it drove, and every
    /// <see cref="LinFan.Core.Abstractions.IFanController.RestoreDefaults"/> (daemon start, fail-safe, coordinators)
    /// sets it as well.
    /// </summary>
    HardwareAuto,

    /// <summary>The fan is being calibrated; the calibration drives it, not the loop.</summary>
    Calibrating,

    /// <summary>The fan is being identified (driven to 100 %).</summary>
    Identifying,

    /// <summary>Another fan is being identified; this one is throttled meanwhile.</summary>
    IdentifyThrottled,

    /// <summary>The fan is driven up to find its tachometer (sensor coupling).</summary>
    Coupling,

    /// <summary>Another fan's tachometer is being coupled; this one is throttled meanwhile.</summary>
    CouplingThrottled,

    /// <summary>The watchdog put the fans into the safe state (firmware auto) and nothing has driven this fan since.</summary>
    FailSafe,

    /// <summary>
    /// The loop could not carry out its decision for this fan this tick (a write or the evaluation failed); the fan
    /// keeps whatever the hardware has.
    /// </summary>
    Failed,

    /// <summary>The channel cannot be controlled (read-only); the loop never drives it.</summary>
    NotControllable,
}
