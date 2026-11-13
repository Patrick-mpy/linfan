// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// „Lüfter N" for every fan without an own name - its name everywhere in the app (<see cref="ApplyTo"/>), the placeholder
/// of its name field in the setup assistant and the settings, and what saving writes when the field stays empty. A fan's
/// hardware label is a chip register or a raw path, which reads poorly as a name. N is the lowest number no other fan's
/// name takes, given in the fans' order, so a fan added later does not get a name that is already in use.
/// </summary>
public static class DefaultFanNames
{
    /// <param name="ownNames">The fans' own names in their order; empty = none.</param>
    /// <param name="taken">Further names in use - fans the list does not show (hidden, or not detected right now).</param>
    /// <returns>Per fan its default name; empty for a fan that has an own name.</returns>
    public static IReadOnlyList<string> For(IReadOnlyList<string> ownNames, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in ownNames.Concat(taken))
            if (!string.IsNullOrWhiteSpace(name))
                used.Add(name.Trim());

        var defaults = new string[ownNames.Count];
        int number = 1;
        for (int i = 0; i < ownNames.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(ownNames[i]))
            {
                defaults[i] = "";
                continue;
            }
            string name, previous = "";
            // A translation without „{0}" would yield the same name for every number - stop rather than loop forever.
            while (!used.Add(name = Localizer.Instance.Format("DefaultFanNames.Name", number++)) && name != previous)
                previous = name;
            defaults[i] = name;
        }
        return defaults;
    }

    /// <summary>
    /// <paramref name="snapshot"/> with every fan that has no own name in its config called by its „Lüfter N" - in the fan
    /// list and in a calibration's status. The daemon names such a fan by its hardware label; the overview and the messages
    /// would then call it something else than the settings, until a save writes the name down.
    /// </summary>
    public static MonitorSnapshot ApplyTo(MonitorSnapshot snapshot)
    {
        var own = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FanConfig f in snapshot.Config.Fans)
            own.TryAdd(f.FanId, f.Name ?? "");
        var listed = snapshot.Fans.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> defaults = For(
            snapshot.Fans.Select(f => own.GetValueOrDefault(f.Id, "")).ToList(),
            snapshot.Config.Fans.Where(f => !listed.Contains(f.FanId)).Select(f => f.Name ?? ""));
        if (defaults.All(d => d.Length == 0))
            return snapshot;

        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var fans = new List<FanReading>(snapshot.Fans.Count);
        for (int i = 0; i < snapshot.Fans.Count; i++)
        {
            FanReading fan = snapshot.Fans[i];
            if (defaults[i].Length > 0)
            {
                named.TryAdd(fan.Id, defaults[i]);
                fan = fan with { Name = defaults[i] };
            }
            fans.Add(fan);
        }
        CalibrationStatus? calibration = snapshot.Calibration is { } c && named.TryGetValue(c.FanId, out string? name)
            ? c with { FanName = name }
            : snapshot.Calibration;
        return snapshot with { Fans = fans, Calibration = calibration };
    }
}
