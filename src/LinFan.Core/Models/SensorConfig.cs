// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Persistierte Nutzer-Anpassung eines Sensors (v. a. der vergebene Anzeigename).</summary>
public sealed record SensorConfig
{
    public string SensorId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Frei benennbare Gruppe zum Organisieren im Dashboard, oder <c>null</c>.</summary>
    public string? Group { get; init; }

    /// <summary>Im Dashboard ausgeblendet (nur Anzeige).</summary>
    public bool Hidden { get; init; }
}
