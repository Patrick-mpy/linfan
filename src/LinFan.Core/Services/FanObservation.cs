// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// One fan as a tick saw it, for <see cref="CalibrationPlausibility"/>: who controlled it (<paramref name="State"/>), the
/// PWM read back from the hardware and whether its mode was readable (a read that timed out gives PWM 0), its speed
/// (<c>null</c> without a reading) from <paramref name="Tachometer"/> - the sensor its speed is read from now - and its
/// stored calibration.
/// </summary>
public sealed record FanObservation(
    string FanId,
    FanControlState State,
    int Pwm,
    bool ModeKnown,
    double? Rpm,
    string? Tachometer,
    FanCalibration? Calibration);
