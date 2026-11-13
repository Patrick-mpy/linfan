// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Services;

/// <summary>
/// Momentaufnahme eines Lüfters für die Anzeige (<paramref name="Rpm"/> ist <c>null</c>, wenn kein
/// Tacho/lesbar). <paramref name="ManualOverride"/> = unter manueller GUI-Steuerung.
/// </summary>
/// <param name="ControlState">Who controlled the fan in the daemon's last tick; <c>null</c> = unknown (a daemon that
/// does not report it, or a state this GUI does not know).</param>
/// <param name="ControlCurveId">The assigned curve the state comes from (see <see cref="FanAction"/>), else <c>null</c>.</param>
/// <param name="ReadOnlyReason">Why a fan that cannot be controlled is read-only; <c>null</c> = controllable or unknown.</param>
/// <param name="Tachometer">The speed sensor <paramref name="Rpm"/> comes from; <c>null</c> = none, or a daemon that does
/// not report it.</param>
/// <param name="CalibrationMismatch">How the fan's speed disagrees with its stored calibration; <c>null</c> = it fits,
/// cannot be judged (yet), or a daemon or finding this GUI does not know.</param>
public sealed record FanReading(
    string Id, string Name, double? Rpm, byte Pwm, bool CanControl, bool ManualOverride = false,
    FanControlState? ControlState = null, string? ControlCurveId = null, FanReadOnlyReason? ReadOnlyReason = null,
    string? Tachometer = null, CalibrationMismatch? CalibrationMismatch = null);
