// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Result of <see cref="SensorGroupOrder.Plan"/>: the group list in display order and the group of every sensor.
/// </summary>
/// <param name="Names">The group names in display order - what <c>AppConfig.SensorGroups</c> should hold.</param>
/// <param name="GroupById">The group of every planned sensor, by sensor id.</param>
/// <param name="HasSuggestions">Whether at least one sensor had no group and got a suggested one.</param>
public sealed record SensorGroupPlan(
    IReadOnlyList<string> Names,
    IReadOnlyDictionary<string, string> GroupById,
    bool HasSuggestions);
