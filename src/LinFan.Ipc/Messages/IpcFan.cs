// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Ein Lüfter-Zustand über die IPC-Grenze (<paramref name="Rpm"/> ist <c>null</c>, wenn kein Tacho).
/// <paramref name="ManualOverride"/> = der Lüfter steht unter manueller GUI-Steuerung (fester PWM).
/// <para>
/// <paramref name="ControlState"/> says who controlled the fan in the daemon's last tick: the name of a
/// <c>LinFan.Core.Services.FanControlState</c> member. <paramref name="ControlCurveId"/> names the assigned curve
/// where that state comes from it (driving, holding, switched off, failed write). Both are <c>null</c> from a daemon
/// that predates them. The state travels as a name rather than as a contract enum, like <paramref name="Mode"/>:
/// an enum member the GUI does not know would fail the whole snapshot line, whereas an unknown name only reads as
/// "unknown".
/// </para>
/// <para>
/// <paramref name="ReadOnlyReason"/> says why a fan with <paramref name="CanControl"/> == <c>false</c> is read-only: the
/// name of a <c>LinFan.Core.Models.FanReadOnlyReason</c> member, by name for the same reason; <c>null</c> when the fan
/// is controllable, the backend cannot tell, or the daemon predates it.
/// </para>
/// <para>
/// <paramref name="Tachometer"/> is the id of the speed sensor <paramref name="Rpm"/> comes from: the fan's explicit
/// coupling, else the backend's pairing unless another fan is coupled to that sensor (one sensor, one fan). <c>null</c>
/// when the fan has none or the daemon predates the field.
/// </para>
/// <para>
/// <paramref name="CalibrationMismatch"/> says the fan's speed does not fit its stored calibration: the name of a
/// <c>LinFan.Core.Services.CalibrationMismatch</c> member, by name for the same reason as <paramref name="ControlState"/>.
/// A notice only - the daemon drives the fan as before. <c>null</c> when it fits, cannot be judged (yet), or the daemon
/// predates the field.
/// </para>
/// </summary>
public sealed record IpcFan(
    string Id, string Name, double? Rpm, int Pwm, string Mode, bool CanControl, bool ManualOverride = false,
    string? ControlState = null, string? ControlCurveId = null, string? ReadOnlyReason = null, string? Tachometer = null,
    string? CalibrationMismatch = null);
