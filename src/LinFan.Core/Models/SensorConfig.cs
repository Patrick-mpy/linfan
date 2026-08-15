// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Persistierte Nutzer-Anpassung eines Sensors (v. a. der vergebene Anzeigename).</summary>
public sealed record SensorConfig
{
    public string SensorId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Free-form group for organizing (dashboard blocks and curve sensor pickers), or <c>null</c>.</summary>
    public string? Group { get; init; }

    /// <summary>Hidden app-wide except in the settings' device lists (display/selection only - the sensor keeps being measured).</summary>
    public bool Hidden { get; init; }
}
