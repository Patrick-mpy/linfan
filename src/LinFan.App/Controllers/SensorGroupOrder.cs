// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Order and suggestions of sensor groups - pure presentation, hence in the App. A group's identity is its name,
/// compared case-insensitively as everywhere else (<c>ConfigSanitizer</c>, the dashboard grouping): the first
/// spelling met names the group.
/// </summary>
public static class SensorGroupOrder
{
    /// <summary>
    /// The configured list first, in its order, then the names sensors use that the list lacks, alphabetically -
    /// the order the app showed unlisted groups in before the list existed. "Ungrouped" is not a name here; callers
    /// put it last.
    /// </summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string> configured, IEnumerable<string> used)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in configured)
            if (seen.Add(name))
                names.Add(name);
        foreach (string name in used.Where(n => !seen.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Order(StringComparer.OrdinalIgnoreCase))
            names.Add(name);
        return names;
    }

    /// <summary>
    /// The group name suggested for a sensor category, in the UI language. It is saved as the group name, so the
    /// "CPU"/"GPU" texts stay untranslated: the airflow analysis finds those sensors by these words.
    /// </summary>
    public static string SuggestedName(SensorCategory category) => Localizer.Instance[$"SensorCategory.{category}"];

    /// <summary>
    /// Groups the sensors: one that has a group keeps it; every other one gets the group
    /// <see cref="SensorClassifier"/> suggests for its id. The names are <see cref="Merge"/> of the configured list
    /// and the existing groups, followed by the suggested groups in <see cref="SensorCategory"/> order - a suggestion
    /// that already exists in another spelling joins that group instead.
    /// </summary>
    public static SensorGroupPlan Plan(IEnumerable<string> configured, IEnumerable<(string Id, string? Group)> sensors)
    {
        var groupById = new Dictionary<string, string>(StringComparer.Ordinal);
        var suggestedById = new Dictionary<string, SensorCategory>(StringComparer.Ordinal);
        foreach ((string id, string? group) in sensors)
        {
            if (string.IsNullOrWhiteSpace(group))
                suggestedById[id] = SensorClassifier.Classify(id);
            else
                groupById[id] = group.Trim();
        }

        var names = Merge(configured, groupById.Values).ToList();
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
            spelling.TryAdd(name, name);

        foreach (SensorCategory category in Enum.GetValues<SensorCategory>())
        {
            List<string> ids = suggestedById.Where(p => p.Value == category).Select(p => p.Key).ToList();
            if (ids.Count == 0)
                continue;
            string suggested = SuggestedName(category);
            if (spelling.TryAdd(suggested, suggested))
                names.Add(suggested);
            foreach (string id in ids)
                groupById[id] = spelling[suggested];
        }

        return new SensorGroupPlan(names, groupById, HasSuggestions: suggestedById.Count > 0);
    }
}
