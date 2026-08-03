// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.App.Services;

/// <summary>Momentaufnahme eines Sensorwerts für die Anzeige.</summary>
public sealed record SensorReading(string Id, string Name, SensorKind Kind, string Unit, double Value);
