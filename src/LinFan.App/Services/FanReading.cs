// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.App.Services;

/// <summary>
/// Momentaufnahme eines Lüfters für die Anzeige (<paramref name="Rpm"/> ist <c>null</c>, wenn kein
/// Tacho/lesbar). <paramref name="ManualOverride"/> = unter manueller GUI-Steuerung.
/// </summary>
public sealed record FanReading(
    string Id, string Name, double? Rpm, byte Pwm, FanMode Mode, bool CanControl, bool ManualOverride = false);
