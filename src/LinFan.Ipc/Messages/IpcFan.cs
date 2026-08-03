// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Ein Lüfter-Zustand über die IPC-Grenze (<paramref name="Rpm"/> ist <c>null</c>, wenn kein Tacho).
/// <paramref name="ManualOverride"/> = der Lüfter steht unter manueller GUI-Steuerung (fester PWM).
/// </summary>
public sealed record IpcFan(
    string Id, string Name, double? Rpm, int Pwm, string Mode, bool CanControl, bool ManualOverride = false);
