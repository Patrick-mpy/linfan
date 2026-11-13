// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// What differs between the saved configuration and the editor's: drives the unsaved-changes summary and the
/// dirty markers on tabs, profiles and curves. Curves are keyed by (profile id, curve id) - duplicating a profile
/// copies the curve ids along, so the id alone would mark a curve in both profiles.
/// </summary>
public sealed class ConfigDiff
{
    /// <summary>No difference at all (clean editor).</summary>
    public static readonly ConfigDiff None = new(new HashSet<(string?, string)>(), new HashSet<string>(), false, false, false, false);

    private readonly HashSet<(string? ProfileId, string CurveId)> _curves;
    private readonly HashSet<string> _profiles;

    private ConfigDiff(HashSet<(string?, string)> curves, HashSet<string> profiles,
                       bool profilesChanged, bool fansChanged, bool sensorsChanged, bool caseTypeChanged)
    {
        _curves = curves;
        _profiles = profiles;
        ProfilesChanged = profilesChanged;
        FansChanged = fansChanged;
        SensorsChanged = sensorsChanged;
        CaseTypeChanged = caseTypeChanged;
    }

    /// <summary>Number of new or changed curves, counted per profile.</summary>
    public int ChangedCurveCount => _curves.Count;

    /// <summary>Profiles added, removed, reordered, renamed, or with a changed curve list.</summary>
    public bool ProfilesChanged { get; }

    /// <summary>Fan settings (name, position, visibility, PWM limits). Curve assignments count as curve changes.</summary>
    public bool FansChanged { get; }

    /// <summary>Sensor settings or the sensor group list.</summary>
    public bool SensorsChanged { get; }

    /// <summary>The case type (Settings › Airflow).</summary>
    public bool CaseTypeChanged { get; }

    /// <summary>Anything that belongs to the curves tab: curves and profile metadata.</summary>
    public bool CurvesAreaChanged => ChangedCurveCount > 0 || ProfilesChanged;

    /// <summary>Anything that belongs to the settings tab: fans, sensors, groups, the case type.</summary>
    public bool SettingsAreaChanged => FansChanged || SensorsChanged || CaseTypeChanged;

    /// <param name="profileId">The curve's profile; <c>null</c> for a legacy configuration without profiles.</param>
    public bool IsCurveChanged(string? profileId, string curveId) => _curves.Contains((profileId, curveId));

    public bool IsProfileChanged(string profileId) => _profiles.Contains(profileId);

    public static ConfigDiff Compute(AppConfig saved, AppConfig current)
    {
        var curves = new HashSet<(string?, string)>();
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        bool profilesChanged = !saved.Profiles.Select(p => p.Id).SequenceEqual(current.Profiles.Select(p => p.Id));

        if (saved.Profiles.Count == 0 && current.Profiles.Count == 0)
        {
            // Legacy configuration without profiles: the editor works on the running curves directly.
            AddChangedCurves(null, saved.Curves, AssignmentsOf(saved.Fans), current.Curves, AssignmentsOf(current.Fans), curves);
        }
        else
        {
            // First entry per id wins, as in ConfigSanitizer - a hand-edited duplicate must not throw here.
            var savedById = new Dictionary<string, Profile>(StringComparer.Ordinal);
            foreach (Profile p in saved.Profiles)
                savedById.TryAdd(p.Id, p);

            foreach (Profile profile in current.Profiles)
            {
                Profile? before = savedById.GetValueOrDefault(profile.Id);
                bool meta = before is null || before.Name != profile.Name
                            || !before.Curves.Select(c => c.Id).SequenceEqual(profile.Curves.Select(c => c.Id));
                int changedBefore = curves.Count;
                AddChangedCurves(profile.Id, before?.Curves ?? Array.Empty<CurveConfig>(),
                                 before?.Assignments ?? Array.Empty<ProfileAssignment>(),
                                 profile.Curves, profile.Assignments, curves);
                if (meta || curves.Count > changedBefore)
                    profiles.Add(profile.Id);
                profilesChanged |= meta;
            }
        }

        // The assignment of a fan is a curve change (see above), not a fan setting.
        bool fansChanged = Json(saved.Fans.Select(f => f with { AssignedCurveId = null }))
                           != Json(current.Fans.Select(f => f with { AssignedCurveId = null }));
        bool sensorsChanged = Json(saved.Sensors) != Json(current.Sensors)
                              || !saved.SensorGroups.SequenceEqual(current.SensorGroups, StringComparer.Ordinal);

        return new ConfigDiff(curves, profiles, profilesChanged, fansChanged, sensorsChanged, saved.CaseType != current.CaseType);
    }

    /// <summary>A curve counts as changed when its own settings differ or a different set of fans follows it.</summary>
    private static void AddChangedCurves(
        string? profileId,
        IReadOnlyList<CurveConfig> savedCurves, IReadOnlyList<ProfileAssignment> savedAssignments,
        IReadOnlyList<CurveConfig> currentCurves, IReadOnlyList<ProfileAssignment> currentAssignments,
        HashSet<(string?, string)> changed)
    {
        var savedJson = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CurveConfig c in savedCurves)
            savedJson.TryAdd(c.Id, Json(c));

        foreach (CurveConfig curve in currentCurves)
        {
            bool same = savedJson.TryGetValue(curve.Id, out string? before) && before == Json(curve)
                        && FansOf(savedAssignments, curve.Id).SequenceEqual(FansOf(currentAssignments, curve.Id));
            if (!same)
                changed.Add((profileId, curve.Id));
        }
    }

    private static IEnumerable<string> FansOf(IReadOnlyList<ProfileAssignment> assignments, string curveId) =>
        assignments.Where(a => a.CurveId == curveId).Select(a => a.FanId).Distinct().Order(StringComparer.Ordinal);

    private static IReadOnlyList<ProfileAssignment> AssignmentsOf(IReadOnlyList<FanConfig> fans) =>
        fans.Select(f => new ProfileAssignment(f.FanId, f.AssignedCurveId)).ToList();

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
