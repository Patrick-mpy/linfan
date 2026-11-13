// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Services;

/// <summary>
/// Vollständige Momentaufnahme zu einem Zeitpunkt: Live-Werte (Sensoren/Lüfter) plus die aktuelle,
/// vom Daemon stammende <see cref="Config"/> (Kurven/Zuordnungen) zum Befüllen des Editors.
/// </summary>
/// <param name="DryRun">The daemon computes curves but writes no PWM (it runs without the rights to).</param>
/// <param name="ConnectionProblem">The reason the monitor reported. Read <see cref="Problem"/> instead, which stays
/// consistent with <paramref name="Connected"/>.</param>
/// <param name="FailSafe">The fail-safe as the daemon's watchdog saw it in the last tick; <c>null</c> = unknown (not
/// connected, a daemon that does not report it, or a state this GUI does not know).</param>
/// <param name="ConfigProblem">Why the daemon runs without the configuration it found at start; <c>null</c> = none (or
/// unknown).</param>
/// <param name="ConfigSetAsidePath">Where the daemon moved an unusable configuration file.</param>
/// <param name="DaemonVersion">The daemon's informational version; <c>null</c> = not connected, or a daemon that does
/// not report it (older than this GUI). With <see cref="Services.ConnectionProblem.UnreadableData"/> it is what the service
/// sent along with the data this GUI could not read.</param>
/// <param name="HardwareChanges">The devices whose configured entries no longer match the hardware, until the user
/// resolves them; <c>null</c> = none (or a daemon that does not report them).</param>
public sealed record MonitorSnapshot(
    string Status,
    IReadOnlyList<SensorReading> Sensors,
    IReadOnlyList<FanReading> Fans,
    AppConfig Config,
    bool Connected = false,
    CalibrationStatus? Calibration = null,
    IdentifyStatus? Identify = null,
    TachMappingStatus? TachMapping = null,
    bool DryRun = false,
    ConnectionProblem ConnectionProblem = ConnectionProblem.None,
    FailSafeState? FailSafe = null,
    ConfigLoadProblem? ConfigProblem = null,
    string? ConfigSetAsidePath = null,
    string? DaemonVersion = null,
    IReadOnlyList<HardwareChangeReading>? HardwareChanges = null)
{
    /// <summary>
    /// Why there is no live data, and what consumers read: <see cref="Services.ConnectionProblem.None"/> exactly while
    /// <see cref="Connected"/>; a snapshot that is not connected but names no reason counts as unreachable.
    /// </summary>
    public ConnectionProblem Problem => Connected
        ? Services.ConnectionProblem.None
        : ConnectionProblem == Services.ConnectionProblem.None ? Services.ConnectionProblem.Unreachable : ConnectionProblem;

    public static MonitorSnapshot Unavailable(string status, ConnectionProblem problem) =>
        new(status, Array.Empty<SensorReading>(), Array.Empty<FanReading>(), AppConfig.Empty, ConnectionProblem: problem);
}
