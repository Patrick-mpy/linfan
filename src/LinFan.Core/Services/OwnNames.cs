// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// A sensor's or fan's <c>Name</c> in the config is the user's own; empty means the hardware label applies. Older
/// versions saved the label itself as that name, and a label frozen like that stays when the hardware behind the id
/// changes - a replaced graphics card kept the old model's name. This clears the names that are just the current label
/// (or a fan's raw id), so the label follows the hardware again.
/// </summary>
public static class OwnNames
{
    /// <summary>
    /// Returns <paramref name="config"/> without own names that equal the current hardware label of the same id, or a
    /// fan's id. A name that no longer matches - the label of hardware that has been replaced - stays: it cannot be told
    /// from a name the user chose.
    /// </summary>
    /// <param name="cleared">How many names were cleared.</param>
    public static AppConfig WithoutHardwareLabels(
        AppConfig config, IEnumerable<SensorDescriptor> sensors, IEnumerable<FanDescriptor> fans, out int cleared)
    {
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, string> sensorLabels = Labels(sensors.Select(s => (s.Id.Value, s.Name)));
        Dictionary<string, string> fanLabels = Labels(fans.Select(f => (f.Id.Value, f.Name)));
        cleared = 0;

        var sensorConfigs = new List<SensorConfig>(config.Sensors.Count);
        foreach (SensorConfig s in config.Sensors)
        {
            bool label = IsLabel(s.Name, s.SensorId, sensorLabels, orId: false);
            cleared += label ? 1 : 0;
            sensorConfigs.Add(label ? s with { Name = "" } : s);
        }

        var fanConfigs = new List<FanConfig>(config.Fans.Count);
        foreach (FanConfig f in config.Fans)
        {
            bool label = IsLabel(f.Name, f.FanId, fanLabels, orId: true);
            cleared += label ? 1 : 0;
            fanConfigs.Add(label ? f with { Name = "" } : f);
        }

        return cleared == 0 ? config : config with { Sensors = sensorConfigs, Fans = fanConfigs };
    }

    // First descriptor per id wins; a backend may list an id twice.
    private static Dictionary<string, string> Labels(IEnumerable<(string Id, string Label)> descriptors)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string id, string label) in descriptors)
            if (!string.IsNullOrWhiteSpace(label))
                labels.TryAdd(id, label.Trim());
        return labels;
    }

    private static bool IsLabel(string? name, string? id, Dictionary<string, string> labels, bool orId)
    {
        if (string.IsNullOrWhiteSpace(name) || id is null)
            return false;
        string trimmed = name.Trim();
        return (labels.TryGetValue(id, out string? label) && trimmed == label) || (orId && trimmed == id);
    }
}
