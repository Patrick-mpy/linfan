// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// Why calibrate, identify, couple - and a manual fan test - cannot start right now. One order for every surface
/// (settings, overview, setup assistant): the daemon is unreachable, it runs dry and writes no fan, or it runs a device
/// action already (only one at a time). It refuses each of these with a log line only, so the GUI must not offer them.
/// </summary>
public static class DeviceActionBlock
{
    /// <summary>A calibration, identification or tach coupling is running (on any fan).</summary>
    public static bool Busy(MonitorSnapshot snapshot) =>
        snapshot.Calibration?.Running == true || snapshot.Identify?.Running == true || snapshot.TachMapping?.Running == true;

    /// <summary>The reason as the actions' tooltip; empty when they can start.</summary>
    public static string Reason(bool connected, bool dryRun, bool busy) =>
        !connected ? Localizer.Instance["FanSections.BlockedOffline"]
        : dryRun ? Localizer.Instance["FanSections.BlockedDryRun"]
        : busy ? Localizer.Instance["FanSections.BlockedBusy"]
        : "";

    /// <summary>
    /// The fan has no speed sensor a calibration could measure with: none of its own (none paired, or its pairing is
    /// coupled to another fan) or one that is not detected right now. The daemon refuses the calibration then, while
    /// identify and coupling - which finds a sensor - still work. Only a daemon that reports its version also reports the
    /// sensor it reads each fan from (both came with 0.5): with an older one this is not known, and nothing is locked.
    /// </summary>
    public static bool LacksSpeedSensor(MonitorSnapshot snapshot, FanReading fan) =>
        snapshot.DaemonVersion is not null
        && (fan.Tachometer is not { } tach || !snapshot.Sensors.Any(s => s.Kind == SensorKind.FanRpm && s.Id == tach));

    /// <summary>
    /// Why a calibration of <paramref name="fan"/> cannot start: <paramref name="shared"/> (<see cref="Reason"/>) first,
    /// then a missing speed sensor; empty when it can.
    /// </summary>
    public static string CalibrateReason(string shared, MonitorSnapshot snapshot, FanReading fan) =>
        shared.Length > 0 ? shared
        : LacksSpeedSensor(snapshot, fan) ? Localizer.Instance["FanSections.BlockedNoTach"]
        : "";
}
