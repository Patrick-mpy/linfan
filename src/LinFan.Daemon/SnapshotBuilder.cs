// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;

namespace LinFan.Daemon;

/// <summary>
/// Baut aus dem aktuellen Hardware-Zustand eine <see cref="IpcSnapshot"/> für den IPC-Broadcast. Who controls each
/// fan and the fail-safe state come from the <paramref name="tick"/> just run (<see cref="ControlTick.ControlOf"/>).
/// The loop passes the config already mapped (<paramref name="ipcConfig"/>, from <paramref name="config"/>) with its
/// revision, so an unchanged config is not mapped again every tick.
/// </summary>
internal static class SnapshotBuilder
{
    public static IpcSnapshot Build(
        ISensorBackend sensors, IFanController fans, DaemonStatus status, bool dryRun, ControlTick tick,
        AppConfig config, IpcConfig ipcConfig, long configRevision, IReadOnlySet<string> manualFans,
        IpcCalibration? calibration, IpcIdentify? identify = null, IpcTachMapping? tachMapping = null,
        ConfigLoadProblem? configProblem = null, string? configSetAsidePath = null,
        IReadOnlyList<IpcHardwareChange>? hardwareChanges = null)
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
                r.Descriptor.Kind.ToString(), r.Descriptor.Unit, r.Value, r.Descriptor.Name))
            .ToList();

        Dictionary<string, double> rpmById = readings
            .Where(r => r.Descriptor.Kind == SensorKind.FanRpm)
            .ToDictionary(r => r.Descriptor.Id.Value, r => r.Value);

        var tachometers = new FanTachometers(config.Fans);
        var fanDtos = fans.DiscoverFans().Select(f =>
        {
            SensorId? tach = tachometers.Of(f);
            double? rpm = null;
            if (tach is { } t && rpmById.TryGetValue(t.Value, out double value) && !double.IsNaN(value))
                rpm = value;
            (FanControlState control, string? controlCurveId) = tick.ControlOf(f.Id.Value, f.CanControl);
            (int pwm, string mode) = ReadPwmAndMode(fans, f.Id);
            return new IpcFan(f.Id.Value, FanName(f.Id.Value, f.Name), rpm,
                pwm, mode, f.CanControl,
                ManualOverride: manualFans.Contains(f.Id.Value),
                ControlState: control.ToString(), ControlCurveId: controlCurveId,
                ReadOnlyReason: f.CanControl ? null : f.ReadOnlyReason?.ToString(), Tachometer: tach?.Value);
        }).ToList();

        return new IpcSnapshot(
            status, dryRun, tick.HottestTempC, sensorDtos, fanDtos, ipcConfig, calibration,
            identify, tachMapping, FailSafe: tick.FailSafe.ToString(), ConfigProblem: configProblem?.ToString(),
            ConfigSetAsidePath: configSetAsidePath, DaemonVersion: BuildVersion.Current, ConfigRevision: configRevision,
            HardwareChanges: hardwareChanges);
    }

    /// <summary>
    /// Runs the calibration check over what <paramref name="snapshot"/> read - its fans' read-back PWM and mode, speed and
    /// speed sensor - with who controlled each fan in <paramref name="tick"/>, and names each finding in the fan's
    /// <see cref="IpcFan.CalibrationMismatch"/>. Reads no hardware of its own. Returns <paramref name="snapshot"/> itself
    /// when there is no finding.
    /// </summary>
    public static IpcSnapshot WithCalibrationMismatches(
        IpcSnapshot snapshot, CalibrationPlausibility plausibility, ControlTick tick, AppConfig config, bool dryRun)
    {
        var calibrations = new Dictionary<string, FanCalibration?>(StringComparer.Ordinal);
        foreach (FanConfig f in config.Fans)
            calibrations.TryAdd(f.FanId, f.Calibration);

        List<FanObservation> observed = snapshot.Fans
            .Select(f => new FanObservation(
                f.Id, tick.ControlOf(f.Id, f.CanControl).State, f.Pwm, ModeKnown: f.Mode != UnknownMode, f.Rpm,
                f.Tachometer, calibrations.GetValueOrDefault(f.Id)))
            .ToList();
        IReadOnlyDictionary<string, CalibrationMismatch> found =
            plausibility.Check(observed, dryRun, failSafe: tick.FailSafe != FailSafeState.None);
        if (found.Count == 0)
            return snapshot;

        return snapshot with
        {
            Fans = snapshot.Fans
                .Select(f => found.TryGetValue(f.Id, out CalibrationMismatch m) ? f with { CalibrationMismatch = m.ToString() } : f)
                .ToList(),
        };
    }

    /// <summary>A hardware change as the snapshot carries it: the kind by name, each device as name and fingerprint.</summary>
    public static IpcHardwareChange ToIpc(HardwareChange change) => new(
        change.Key, change.Kind.ToString(), change.Old?.Name, change.Old?.Fingerprint, change.New?.Name,
        change.New?.Fingerprint, change.FanIds, change.SensorIds);

    /// <summary>
    /// The fan's current PWM and mode, or 0 and <see cref="UnknownMode"/> if they cannot be read right now - the
    /// fan controller gives up after a bounded wait while a write is wedged, and a backend throws for a mode it cannot
    /// read. One such fan must not cost every client the whole snapshot; who controls the fan comes from the tick
    /// either way.
    /// </summary>
    private static (int Pwm, string Mode) ReadPwmAndMode(IFanController fans, FanId id)
    {
        int pwm;
        try
        {
            pwm = fans.GetPwm(id);
        }
        catch (TimeoutException)
        {
            return (0, UnknownMode);
        }

        try
        {
            return (pwm, fans.GetMode(id).ToString());
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return (pwm, UnknownMode);
        }
    }

    internal const string UnknownMode = "Unknown";
}
