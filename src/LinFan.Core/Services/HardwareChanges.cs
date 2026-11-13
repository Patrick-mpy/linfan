// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Finds configured entries whose hardware is no longer the one they were set up for, and resolves such a change the
/// way the user decides: keep the entries for the new device (<see cref="Accept"/>) or remove them
/// (<see cref="Forget"/>). Nothing here happens on its own - an entry is only ever removed by <see cref="Forget"/>.
/// </summary>
public static class HardwareChanges
{
    /// <summary>
    /// The changes between the devices <paramref name="config"/> records and the ones discovered now, ordered by kind,
    /// then by device:
    /// <list type="bullet">
    /// <item><see cref="HardwareChangeKind.Replaced"/> - a detected entry whose recorded and current fingerprints are both
    /// known and differ, one change per pair of devices. A missing fingerprint on either side proves nothing.</item>
    /// <item><see cref="HardwareChangeKind.Missing"/> - an entry whose id is not detected, one change per recorded device
    /// (entries without one form a change of their own). Never in a dry run: an unprivileged backend may discover less
    /// (Windows without admin rights), and its gaps would read as removed hardware.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<HardwareChange> Detect(
        AppConfig config, IEnumerable<FanDescriptor> fans, IEnumerable<SensorDescriptor> sensors, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, HardwareDevice?> fanDevices = HardwareStamps.DevicesById(fans.Select(f => (f.Id.Value, f.Device)));
        Dictionary<string, HardwareDevice?> sensorDevices =
            HardwareStamps.DevicesById(sensors.Select(s => (s.Id.Value, s.Device)));
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);

        void Add(string? id, HardwareDevice? recorded, Dictionary<string, HardwareDevice?> current, bool fan)
        {
            if (id is null)
                return;
            HardwareChangeKind kind;
            HardwareDevice? now = null;
            if (current.TryGetValue(id, out HardwareDevice? detected))
            {
                if (recorded?.Fingerprint is null || detected?.Fingerprint is null
                    || recorded.Fingerprint == detected.Fingerprint)
                    return;
                (kind, now) = (HardwareChangeKind.Replaced, detected);
            }
            else if (dryRun)
            {
                return;
            }
            else
            {
                kind = HardwareChangeKind.Missing;
            }

            string identity = Identity(kind, recorded, now);
            if (!groups.TryGetValue(identity, out Group? group))
                groups[identity] = group = new Group(kind, recorded, now);
            (fan ? group.Fans : group.Sensors).Add(id);
        }

        foreach (FanConfig f in config.Fans)
            Add(f.FanId, f.Device, fanDevices, fan: true);
        foreach (SensorConfig s in config.Sensors)
            Add(s.SensorId, s.Device, sensorDevices, fan: false);

        return groups
            .Select(g => new HardwareChange(
                KeyOf(g.Value.Kind, g.Key), g.Value.Kind, g.Value.Old, g.Value.New, g.Value.Fans.ToList(), g.Value.Sensors.ToList()))
            .OrderBy(c => c.Kind)
            .ThenBy(c => c.Old?.Name, StringComparer.Ordinal).ThenBy(c => c.Old?.Fingerprint, StringComparer.Ordinal)
            .ThenBy(c => c.New?.Name, StringComparer.Ordinal).ThenBy(c => c.New?.Fingerprint, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Keeps the entries of a <see cref="HardwareChangeKind.Replaced"/> change for the device behind them now: they
    /// record <see cref="HardwareChange.New"/> from here on, so the change is no longer reported. Calibration, limits and
    /// curves stay - whether they suit the new device is the user's call. With <paramref name="resetSensorNames"/> the
    /// change's sensors also lose their own names, so the new device's labels show; fans keep theirs. A missing device
    /// has nothing to accept: the config comes back unchanged.
    /// </summary>
    /// <returns>The resolved config, or the same instance if nothing changed.</returns>
    public static AppConfig Accept(AppConfig config, HardwareChange change, bool resetSensorNames)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(change);
        if (change.Kind != HardwareChangeKind.Replaced)
            return config;

        var fanIds = change.FanIds.ToHashSet(StringComparer.Ordinal);
        var sensorIds = change.SensorIds.ToHashSet(StringComparer.Ordinal);
        bool changed = false;

        var fans = config.Fans.Select(f =>
        {
            if (!fanIds.Contains(f.FanId) || f.Device == change.New)
                return f;
            changed = true;
            return f with { Device = change.New };
        }).ToList();
        var sensors = config.Sensors.Select(s =>
        {
            if (!sensorIds.Contains(s.SensorId))
                return s;
            string name = resetSensorNames ? "" : s.Name;
            if (s.Device == change.New && s.Name == name)
                return s;
            changed = true;
            return s with { Device = change.New, Name = name };
        }).ToList();

        return changed ? config with { Fans = fans, Sensors = sensors } : config;
    }

    /// <summary>
    /// Removes the change's fan and sensor entries and the profile assignments of those fans, so a device that is gone
    /// leaves nothing behind and one that replaced it starts out like new hardware. Curves keep their sources: a
    /// removed sensor may come back, and a curve without a readable source hands its fans to the firmware anyway.
    /// </summary>
    /// <returns>The config without the entries, or the same instance if it had none of them.</returns>
    public static AppConfig Forget(AppConfig config, HardwareChange change)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(change);

        var fanIds = change.FanIds.ToHashSet(StringComparer.Ordinal);
        var sensorIds = change.SensorIds.ToHashSet(StringComparer.Ordinal);
        var fans = config.Fans.Where(f => !fanIds.Contains(f.FanId)).ToList();
        var sensors = config.Sensors.Where(s => !sensorIds.Contains(s.SensorId)).ToList();
        bool assigned = config.Profiles.Any(p => p.Assignments.Any(a => fanIds.Contains(a.FanId)));
        if (fans.Count == config.Fans.Count && sensors.Count == config.Sensors.Count && !assigned)
            return config;

        var profiles = config.Profiles
            .Select(p => p.Assignments.Any(a => fanIds.Contains(a.FanId))
                ? p with { Assignments = p.Assignments.Where(a => !fanIds.Contains(a.FanId)).ToList() }
                : p)
            .ToList();
        return config with { Fans = fans, Sensors = sensors, Profiles = profiles };
    }

    /// <summary>
    /// A text that tells every kind and pair of devices apart - <c>null</c> from empty, and no name can pass for a
    /// fingerprint - and from which the key is hashed.
    /// </summary>
    private static string Identity(HardwareChangeKind kind, HardwareDevice? old, HardwareDevice? now) =>
        $"{kind}|{Text(old)}|{Text(now)}";

    private static string Text(HardwareDevice? device) =>
        device is null ? "-" : $"{Text(device.Name)},{Text(device.Fingerprint)}";

    private static string Text(string? value) =>
        value is null ? "-" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    /// <summary>
    /// The kind and a stable hash of the identity: deterministic across runs and machines, short and free of whatever
    /// a device name holds, so it passes as an IPC target whatever the devices are called.
    /// </summary>
    private static string KeyOf(HardwareChangeKind kind, string identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return kind.ToString().ToLowerInvariant() + "-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private sealed class Group(HardwareChangeKind kind, HardwareDevice? old, HardwareDevice? now)
    {
        public HardwareChangeKind Kind { get; } = kind;
        public HardwareDevice? Old { get; } = old;
        public HardwareDevice? New { get; } = now;
        public SortedSet<string> Fans { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Sensors { get; } = new(StringComparer.Ordinal);
    }
}
