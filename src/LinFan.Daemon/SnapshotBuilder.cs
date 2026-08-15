// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;

namespace LinFan.Daemon;

/// <summary>Baut aus dem aktuellen Hardware-Zustand eine <see cref="IpcSnapshot"/> für den IPC-Broadcast.</summary>
internal static class SnapshotBuilder
{
    public static IpcSnapshot Build(
        ISensorBackend sensors, IFanController fans, DaemonStatus status, bool dryRun, double hottestTempC,
        AppConfig config, IReadOnlySet<string> manualFans, IpcCalibration? calibration,
        IpcIdentify? identify = null, IpcTachMapping? tachMapping = null)
    {
        var readings = sensors.DiscoverSensors()
            .Select(s => (Descriptor: s, Value: sensors.ReadValue(s.Id)))
            .ToList();

        // Custom-Namen aus der Konfiguration haben Vorrang vor den hwmon-Roh-Labels. Einmalig nach Id
        // indizieren statt pro Kanal linear zu suchen (O(Kanäle) statt O(Kanäle×Config) je Snapshot-Tick).
        // TryAdd = erster Treffer gewinnt - verhält sich wie das frühere FirstOrDefault.
        var sensorById = new Dictionary<string, SensorConfig>();
        foreach (SensorConfig s in config.Sensors)
            sensorById.TryAdd(s.SensorId, s);
        var fanById = new Dictionary<string, FanConfig>();
        foreach (FanConfig f in config.Fans)
            fanById.TryAdd(f.FanId, f);

        string SensorName(string id, string hardware) =>
            sensorById.TryGetValue(id, out SensorConfig? sc) && sc.Name is { Length: > 0 } ? sc.Name : hardware;
        string FanName(string id, string hardware) =>
            fanById.TryGetValue(id, out FanConfig? fc) && fc.Name is { Length: > 0 } ? fc.Name : hardware;

        var sensorDtos = readings
            .Select(r => new IpcSensor(r.Descriptor.Id.Value, SensorName(r.Descriptor.Id.Value, r.Descriptor.Name),
                r.Descriptor.Kind.ToString(), r.Descriptor.Unit, r.Value))
            .ToList();

        Dictionary<string, double> rpmById = readings
            .Where(r => r.Descriptor.Kind == SensorKind.FanRpm)
            .ToDictionary(r => r.Descriptor.Id.Value, r => r.Value);

        var fanDtos = fans.DiscoverFans().Select(f =>
        {
            // Explizit zugeordnetes RpmSource-Override gewinnt vor dem Backend-gepaarten Tacho.
            SensorId? tach = fanById.TryGetValue(f.Id.Value, out FanConfig? fc) && fc.RpmSource is { Length: > 0 } rs
                ? new SensorId(rs)
                : f.Tachometer;
            double? rpm = null;
            if (tach is { } t && rpmById.TryGetValue(t.Value, out double value) && !double.IsNaN(value))
                rpm = value;
            return new IpcFan(f.Id.Value, FanName(f.Id.Value, f.Name), rpm,
                fans.GetPwm(f.Id), fans.GetMode(f.Id).ToString(), f.CanControl,
                ManualOverride: manualFans.Contains(f.Id.Value));
        }).ToList();

        return new IpcSnapshot(
            status, dryRun, hottestTempC, sensorDtos, fanDtos, ConfigMapper.ToIpc(config), calibration, identify,
            tachMapping);
    }
}
