// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Benanntes Set von Lüfter→Kurve-Zuordnungen (z. B. „Silent" / „Performance"). Beim Aktivieren
/// werden die <see cref="Assignments"/> in die <see cref="FanConfig.AssignedCurveId"/> übernommen,
/// sodass der Regel-Loop unverändert nur die Lüfter-Zuordnung liest.
/// </summary>
public sealed record Profile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Die Kurven dieses Profils (beim Aktivieren werden sie zu den aktiven <c>config.Curves</c>).</summary>
    public IReadOnlyList<CurveConfig> Curves { get; init; } = [];

    public IReadOnlyList<ProfileAssignment> Assignments { get; init; } = [];
}

/// <summary>Zuordnung Lüfter → Kurve innerhalb eines Profils (<c>CurveId == null</c> = ungeregelt).</summary>
public sealed record ProfileAssignment(string FanId, string? CurveId);
