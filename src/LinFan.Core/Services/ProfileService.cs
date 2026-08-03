// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>Wendet Profile auf eine <see cref="AppConfig"/> an (Zuordnungen → Lüfter, aktives Profil setzen).</summary>
public static class ProfileService
{
    /// <summary>
    /// Aktiviert das Profil <paramref name="profileId"/>: übernimmt dessen Kurven in die aktiven
    /// <see cref="AppConfig.Curves"/> und dessen Zuordnungen in die <see cref="FanConfig.AssignedCurveId"/>,
    /// setzt es als aktiv. Unbekanntes Profil → nur <see cref="AppConfig.ActiveProfileId"/> gesetzt.
    /// </summary>
    public static AppConfig Apply(AppConfig config, string profileId)
    {
        ArgumentNullException.ThrowIfNull(config);

        Profile? profile = config.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
            return config with { ActiveProfileId = profileId };

        Dictionary<string, string?> map = profile.Assignments.ToDictionary(a => a.FanId, a => a.CurveId);
        var fans = config.Fans
            .Select(f => map.TryGetValue(f.FanId, out string? curveId) ? f with { AssignedCurveId = curveId } : f)
            .ToList();

        return config with { Curves = profile.Curves.ToList(), Fans = fans, ActiveProfileId = profileId };
    }

    /// <summary>
    /// Stellt sicher, dass mindestens ein Profil existiert und alle Profile Kurven besitzen
    /// (Migration alter Configs mit globalen Kurven), und wendet das aktive Profil an. Idempotent.
    /// </summary>
    public static AppConfig EnsureProfiles(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Profiles.Count == 0)
        {
            // Altbestand ohne Profile → ein Default-Profil aus den aktuellen Kurven + Zuordnungen.
            var standard = new Profile
            {
                Id = "default",
                Name = "Standard",
                Curves = config.Curves,
                Assignments = config.Fans.Select(f => new ProfileAssignment(f.FanId, f.AssignedCurveId)).ToList(),
            };
            return config with { Profiles = new[] { standard }, ActiveProfileId = standard.Id };
        }

        // Profile ohne eigene Kurven (Altbestand) mit den aktuellen Kurven seeden.
        var profiles = config.Profiles
            .Select(p => p.Curves.Count == 0 ? p with { Curves = config.Curves } : p)
            .ToList();

        string activeId = profiles.Any(p => p.Id == config.ActiveProfileId)
            ? config.ActiveProfileId!
            : profiles[0].Id;

        return Apply(config with { Profiles = profiles }, activeId);
    }
}
