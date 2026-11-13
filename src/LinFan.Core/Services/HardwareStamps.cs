// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Records the device behind each configured fan and sensor (<see cref="FanConfig.Device"/>,
/// <see cref="SensorConfig.Device"/>), so <see cref="HardwareChanges.Detect"/> can tell when another device answers to
/// the same id later. It only fills in what is unknown: a recorded device that differs from the current one is the
/// change to report, and only the user resolves it.
/// </summary>
public static class HardwareStamps
{
    /// <summary>
    /// Returns <paramref name="config"/> with the current device on each detected entry that records none, and the
    /// current fingerprint on each entry that recorded the same device by name without one (the backend could not read
    /// it then). Every other entry and every other field stays as it is, as does an entry whose descriptor names no
    /// device. The same instance when nothing changed.
    /// </summary>
    /// <param name="stamped">How many entries got a device or a fingerprint.</param>
    public static AppConfig Stamp(
        AppConfig config, IEnumerable<FanDescriptor> fans, IEnumerable<SensorDescriptor> sensors, out int stamped)
    {
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, HardwareDevice?> fanDevices = DevicesById(fans.Select(f => (f.Id.Value, f.Device)));
        Dictionary<string, HardwareDevice?> sensorDevices = DevicesById(sensors.Select(s => (s.Id.Value, s.Device)));
        int count = 0;

        var fanConfigs = new List<FanConfig>(config.Fans.Count);
        foreach (FanConfig f in config.Fans)
        {
            HardwareDevice? device = Stamped(f.Device, f.FanId, fanDevices);
            count += device == f.Device ? 0 : 1;
            fanConfigs.Add(device == f.Device ? f : f with { Device = device });
        }

        var sensorConfigs = new List<SensorConfig>(config.Sensors.Count);
        foreach (SensorConfig s in config.Sensors)
        {
            HardwareDevice? device = Stamped(s.Device, s.SensorId, sensorDevices);
            count += device == s.Device ? 0 : 1;
            sensorConfigs.Add(device == s.Device ? s : s with { Device = device });
        }

        stamped = count;
        return count == 0 ? config : config with { Fans = fanConfigs, Sensors = sensorConfigs };
    }

    /// <summary>
    /// The device of each discovered id; <c>null</c> where the backend names none. The first descriptor per id wins - a
    /// backend may list an id twice.
    /// </summary>
    internal static Dictionary<string, HardwareDevice?> DevicesById(IEnumerable<(string Id, HardwareDevice? Device)> descriptors)
    {
        var devices = new Dictionary<string, HardwareDevice?>(StringComparer.Ordinal);
        foreach ((string id, HardwareDevice? device) in descriptors)
            devices.TryAdd(id, device);
        return devices;
    }

    private static HardwareDevice? Stamped(HardwareDevice? recorded, string? id, Dictionary<string, HardwareDevice?> current)
    {
        if (id is null || !current.TryGetValue(id, out HardwareDevice? now) || now is null)
            return recorded;
        if (recorded is null)
            return now;
        return recorded.Fingerprint is null && now.Fingerprint is not null && recorded.Name == now.Name ? now : recorded;
    }
}
