// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.App.Services;

/// <summary>Momentaufnahme eines Sensorwerts für die Anzeige.</summary>
/// <param name="Name">The name to show: the user's own, or else the hardware label.</param>
/// <param name="Label">The hardware label, also while an own name is set; <c>null</c> = not known (older daemon).</param>
public sealed record SensorReading(string Id, string Name, SensorKind Kind, string Unit, double Value, string? Label = null);
