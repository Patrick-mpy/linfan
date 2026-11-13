// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Builds the overview's hint list from a snapshot - pure, so every rule is covered by plain unit tests. Heaviest
/// first; within a severity in the order of the rules below. What the daemon did (fail-safe, a curve holding its value,
/// a failed write) comes from its own report in the snapshot, not from a guess of the GUI.
/// </summary>
public static class OverviewHints
{
    /// <summary>Below this share a stopped fan is normal (many fans only start above it).</summary>
    public const int StandstillMinPercent = 20;

    /// <param name="snapshot">The last connected snapshot.</param>
    /// <param name="problem">
    /// The connection state now. Offline - or with data this GUI cannot read - the list holds only that hint: everything
    /// else would come from the frozen snapshot and could be stale. The dry run only counts while connected.
    /// </param>
    public static IReadOnlyList<OverviewHint> Build(MonitorSnapshot snapshot, ConnectionProblem problem)
    {
        Localizer l = Localizer.Instance;
        if (problem is ConnectionProblem.Unreachable or ConnectionProblem.AccessDenied)
            return new[] { new OverviewHint("offline", HintSeverity.Critical, l["Overview.HintOffline"]) };
        if (problem == ConnectionProblem.UnreadableData)
            return new[] { new OverviewHint("unreadable", HintSeverity.Critical, l["Overview.HintUnreadable"]) };

        var hints = new List<OverviewHint>();
        // A dry run reports the watchdog's verdict but writes nothing: the fans are not on hardware auto because of it.
        // Every state the snapshot can carry is mapped, Blind in a dry run included (today's daemon does not report it
        // there): the GUI shows what the daemon reports instead of relying on its internals.
        string? failSafe = (snapshot.FailSafe, snapshot.DryRun) switch
        {
            (FailSafeState.OverTemperature, false) => l["Overview.HintFailSafeOverTemperature"],
            (FailSafeState.OverTemperature, true) => l["Overview.HintFailSafeOverTemperatureDryRun"],
            (FailSafeState.Blind, false) => l["Overview.HintFailSafeBlind"],
            (FailSafeState.Blind, true) => l["Overview.HintFailSafeBlindDryRun"],
            (FailSafeState.Error, false) => l["Overview.HintFailSafeError"],
            (FailSafeState.Error, true) => l["Overview.HintFailSafeErrorDryRun"],
            _ => null,
        };
        if (failSafe is not null)
            hints.Add(new OverviewHint("failSafe", HintSeverity.Critical, failSafe));
        if (problem == ConnectionProblem.None && snapshot.DryRun)
            hints.Add(new OverviewHint("dryRun", HintSeverity.Notice, l["Overview.HintDryRun"]));
        // Without it the curves seem to have vanished; the daemon drops it with the first saved change.
        if (snapshot.ConfigProblem is ConfigLoadProblem.NotLoaded)
            hints.Add(new OverviewHint("configProblem", HintSeverity.Notice, l["Overview.HintConfigNotLoaded"]));
        else if (snapshot is { ConfigProblem: ConfigLoadProblem.SetAside, ConfigSetAsidePath: { } path })
            hints.Add(new OverviewHint("configProblem", HintSeverity.Notice, l.Format("Overview.HintConfigSetAside", path)));

        AddHotSensors(hints, snapshot, l);
        // A failed decision matters for every fan, hidden ones included: nothing controls it as configured.
        foreach (FanReading fan in snapshot.Fans.Where(f => f.ControlState == FanControlState.Failed))
            hints.Add(new OverviewHint($"failed:{fan.Id}", HintSeverity.Critical, l.Format("Overview.HintFailed", fan.Name)));
        // Calibrating starts only while the daemon is reachable, writes and runs no other device action - the rule of the
        // fan table's button (DeviceActionBlock). Otherwise a hint names the problem without offering it.
        bool canCalibrate = problem == ConnectionProblem.None && !snapshot.DryRun && !DeviceActionBlock.Busy(snapshot);
        AddFans(hints, snapshot, l, canCalibrate);
        AddCurves(hints, snapshot, l);
        AddHardwareChanges(hints, snapshot, l);

        // Unlike missing rights (the dry-run notice says that) a driver lock is invisible otherwise: the fan just reads
        // as not controllable. Hidden fans count too - the lock covers the driver, not one fan.
        if (snapshot.Fans.Any(f => f.ReadOnlyReason == FanReadOnlyReason.DriverLocked))
            hints.Add(new OverviewHint("driverLocked", HintSeverity.Notice, l["Overview.HintDriverLocked"]));

        int uncalibrated = VisibleFans(snapshot)
            .Count(f => f.Reading.CanControl && f.Config?.Calibration is null);
        if (uncalibrated > 0 && !snapshot.DryRun) // a dry run refuses to calibrate
        {
            // Calibrating lives in the fan table's "…" controls, so the action leads there.
            hints.Add(new OverviewHint("uncalibrated", HintSeverity.Info,
                uncalibrated == 1 ? l["Overview.HintUncalibratedOne"] : l.Format("Overview.HintUncalibrated", uncalibrated),
                HintAction.ShowFans, actionLabel: l["Overview.ActionToFans"]));
        }

        // OrderByDescending is stable: equal severities keep the rule order above.
        return hints.OrderByDescending(h => h.Severity).ToList();
    }

    /// <summary>
    /// Brings <paramref name="list"/> in line with <paramref name="fresh"/>: a hint with the same key keeps its instance
    /// and takes the new text, new ones are inserted, gone ones removed - adjusted in place, never cleared. Returns
    /// whether hints were added, removed or moved.
    /// </summary>
    public static bool Sync(ObservableCollection<OverviewHint> list, IReadOnlyList<OverviewHint> fresh)
    {
        var existing = new Dictionary<string, OverviewHint>(StringComparer.Ordinal);
        foreach (OverviewHint hint in list)
            existing.TryAdd(hint.Key, hint);
        List<OverviewHint> target = fresh
            .Select(f => existing.TryGetValue(f.Key, out OverviewHint? kept) ? kept.UpdateFrom(f) : f)
            .ToList();
        if (target.SequenceEqual(list))
            return false;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(list[i]))
                list.RemoveAt(i);
        }
        for (int i = 0; i < target.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], target[i]))
                continue;
            int at = list.IndexOf(target[i]);
            if (at >= 0)
                list.Move(at, i);
            else
                list.Insert(i, target[i]);
        }
        return true;
    }

    private static void AddHotSensors(List<OverviewHint> hints, MonitorSnapshot snapshot, Localizer l)
    {
        foreach (SensorReading s in snapshot.Sensors.Where(s => s.Kind == SensorKind.Temperature))
        {
            SensorConfig? sc = snapshot.Config.Sensors.FirstOrDefault(c => c.SensorId == s.Id);
            if (sc?.Hidden == true || !TemperatureLevels.IsHot(s.Value))
                continue;
            string value = s.Value.ToString("0.0", CultureInfo.InvariantCulture);
            hints.Add(new OverviewHint($"hot:{s.Id}", HintSeverity.Critical, l.Format("Overview.HintHot", s.Name, value),
                HintAction.ShowTemperatureGroup, SensorGroup.KeyOf(sc?.Group), l["Overview.ActionShow"]));
        }
    }

    private static void AddFans(List<OverviewHint> hints, MonitorSnapshot snapshot, Localizer l, bool canCalibrate)
    {
        // Calibration, identification and coupling drive fans down on purpose - a standstill then says nothing.
        bool deviceActionRunning = snapshot.Calibration?.Running == true || snapshot.Identify?.Running == true
                                   || snapshot.TachMapping?.Running == true
                                   || snapshot.Fans.Any(f => f.ControlState is FanControlState.Calibrating
                                       or FanControlState.Identifying or FanControlState.IdentifyThrottled
                                       or FanControlState.Coupling or FanControlState.CouplingThrottled);
        foreach ((FanReading fan, FanConfig? fc) in VisibleFans(snapshot))
        {
            if (!fan.CanControl)
                continue;

            bool fanCanCalibrate = canCalibrate && !DeviceActionBlock.LacksSpeedSensor(snapshot, fan);
            int percent = PwmScale.ToPercent(fan.Pwm);
            // Only a tachometer that reads 0 counts (null = no tachometer). Above the calibrated start-up point, if any:
            // below it the fan is not meant to turn - unless the daemon finds it standing where its calibration says it
            // turns, which covers a fan that no longer starts at that very start-up point.
            if (!deviceActionRunning && fan.Rpm == 0
                && (percent > StandstillThreshold(fc) || fan.CalibrationMismatch == CalibrationMismatch.Stalls))
            {
                hints.Add(FanHint($"standstill:{fan.Id}", HintSeverity.Critical,
                    l.Format("Overview.HintStandstill", fan.Name, percent), fan.Id, fc, fanCanCalibrate, l));
            }
            else if (MismatchText(fan, percent, l) is { } mismatch)
            {
                hints.Add(FanHint($"calibration:{fan.Id}", HintSeverity.Notice, mismatch, fan.Id, fc, fanCanCalibrate, l));
            }

            if (fan.ManualOverride)
            {
                CurveConfig? overridden = FanControlResolver.AssignedCurve(fan.Id, snapshot.Config) is { Enabled: true } c ? c : null;
                hints.Add(new OverviewHint($"manual:{fan.Id}", HintSeverity.Notice,
                    overridden is null
                        ? l.Format("Overview.HintManual", fan.Name)
                        : l.Format("Overview.HintManualOverrides", fan.Name, overridden.Name),
                    HintAction.BackToCurve, fan.Id,
                    overridden is null ? l["Overview.BackToAuto"] : l["Overview.BackToCurve"]));
            }
        }
    }

    /// <summary>
    /// What the daemon's calibration check found, as a notice; <c>null</c> for nothing. A fan that stands although its
    /// calibration says it turns reads 0 and gets the standstill hint instead - unless it still turns a little.
    /// </summary>
    private static string? MismatchText(FanReading fan, int percent, Localizer l) => fan.CalibrationMismatch switch
    {
        CalibrationMismatch.Slower => l.Format("Overview.HintCalibrationSlower", fan.Name),
        CalibrationMismatch.Faster => l.Format("Overview.HintCalibrationFaster", fan.Name),
        CalibrationMismatch.OtherSensor => l.Format("Overview.HintCalibrationOtherSensor", fan.Name),
        CalibrationMismatch.Stalls when fan.Rpm > 0 => l.Format("Overview.HintCalibrationStalls", fan.Name, percent),
        _ => null,
    };

    /// <summary>A hint about one fan that offers to (re)calibrate it while that can start.</summary>
    private static OverviewHint FanHint(string key, HintSeverity severity, string text, string fanId, FanConfig? fc,
                                        bool canCalibrate, Localizer l) =>
        canCalibrate
            ? new OverviewHint(key, severity, text, HintAction.Calibrate, fanId,
                l[fc?.Calibration is null ? "MainWindow.Calibrate" : "FanAssignRow.Recalibrate"])
            : new OverviewHint(key, severity, text);

    /// <summary>
    /// One hint for all replaced devices and one for all missing ones, both leading to the dialog that resolves them. A
    /// replaced device whose fans keep their old calibration needs a look - their start-up point may not suit the new one.
    /// Kinds this GUI does not know are left out.
    /// </summary>
    private static void AddHardwareChanges(List<OverviewHint> hints, MonitorSnapshot snapshot, Localizer l)
    {
        IReadOnlyList<HardwareChangeReading> changes = snapshot.HardwareChanges ?? [];
        List<HardwareChangeReading> replaced = changes.Where(c => c.Kind == HardwareChangeKind.Replaced).ToList();
        if (replaced.Count > 0)
        {
            bool fans = replaced.Any(c => c.FanIds.Count > 0);
            string text = replaced.Count == 1
                ? l.Format(fans ? "Overview.HintHardwareReplacedFansOne" : "Overview.HintHardwareReplacedOne",
                    HardwareChangeText.DeviceName(replaced[0]))
                : l.Format(fans ? "Overview.HintHardwareReplacedFans" : "Overview.HintHardwareReplaced", replaced.Count);
            hints.Add(new OverviewHint("hardwareReplaced", fans ? HintSeverity.Notice : HintSeverity.Info, text,
                HintAction.ReviewHardware, actionLabel: l["Overview.ActionReview"]));
        }

        int missing = changes.Where(c => c.Kind == HardwareChangeKind.Missing).Sum(c => c.FanIds.Count + c.SensorIds.Count);
        if (missing > 0)
        {
            hints.Add(new OverviewHint("hardwareMissing", HintSeverity.Info,
                missing == 1 ? l["Overview.HintHardwareMissingOne"] : l.Format("Overview.HintHardwareMissing", missing),
                HintAction.ReviewHardware, actionLabel: l["Overview.ActionReview"]));
        }
    }

    /// <summary>The share above which a reading of 0 RPM means "stopped": 20 %, or the calibrated start-up point if higher.</summary>
    public static int StandstillThreshold(FanConfig? fan) =>
        fan?.Calibration is { } cal ? Math.Max(StandstillMinPercent, PwmScale.ToPercent(cal.StartPwm)) : StandstillMinPercent;

    private static void AddCurves(List<OverviewHint> hints, MonitorSnapshot snapshot, Localizer l)
    {
        // Curves the daemon reported holding: on, but without a readable source - their fans keep the last value.
        HashSet<string> holding = snapshot.Fans
            .Where(f => f.ControlState == FanControlState.CurveHolding && f.ControlCurveId is not null)
            .Select(f => f.ControlCurveId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (CurveConfig curve in snapshot.Config.Curves)
        {
            int fans = snapshot.Config.Fans.Count(f => f.AssignedCurveId == curve.Id);
            IReadOnlyList<string> sources = CurveSourceResolver.ResolveSources(curve.SourceSensorId, curve.SourceSensorIds);
            if (!curve.Enabled)
            {
                if (fans > 0)
                {
                    hints.Add(new OverviewHint($"curveOff:{curve.Id}", HintSeverity.Info,
                        fans == 1 ? l.Format("Overview.HintCurveOffOne", curve.Name) : l.Format("Overview.HintCurveOff", curve.Name, fans),
                        HintAction.EnableCurve, curve.Id, l["Overview.ActionEnable"]));
                }
                continue;
            }

            if (fans > 0 && sources.Count == 0)
            {
                hints.Add(new OverviewHint($"curveNoSensor:{curve.Id}", HintSeverity.Notice,
                    l.Format("Overview.HintCurveNoSensor", curve.Name),
                    HintAction.OpenCurve, curve.Id, l["Overview.ActionToCurve"]));
            }
            else if (fans > 0 && holding.Contains(curve.Id))
            {
                hints.Add(new OverviewHint($"curveHolding:{curve.Id}", HintSeverity.Critical,
                    l.Format("Overview.HintCurveUnreadable", curve.Name),
                    HintAction.OpenCurve, curve.Id, l["Overview.ActionToCurve"]));
            }
            else if (fans == 0 && sources.Count > 0)
            {
                hints.Add(new OverviewHint($"curveNoFan:{curve.Id}", HintSeverity.Info,
                    l.Format("Overview.HintCurveNoFan", curve.Name),
                    HintAction.OpenCurve, curve.Id, l["Overview.ActionToCurve"]));
            }
        }
    }

    private static IEnumerable<(FanReading Reading, FanConfig? Config)> VisibleFans(MonitorSnapshot snapshot)
    {
        foreach (FanReading fan in snapshot.Fans)
        {
            FanConfig? fc = snapshot.Config.Fans.FirstOrDefault(c => c.FanId == fan.Id);
            if (fc?.Hidden != true)
                yield return (fan, fc);
        }
    }
}
