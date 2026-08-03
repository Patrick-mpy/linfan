// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.App.Services;

/// <summary>
/// Vollständige Momentaufnahme zu einem Zeitpunkt: Live-Werte (Sensoren/Lüfter) plus die aktuelle,
/// vom Daemon stammende <see cref="Config"/> (Kurven/Zuordnungen) zum Befüllen des Editors.
/// </summary>
public sealed record MonitorSnapshot(
    string Status,
    IReadOnlyList<SensorReading> Sensors,
    IReadOnlyList<FanReading> Fans,
    AppConfig Config,
    bool Connected = false,
    CalibrationStatus? Calibration = null,
    IdentifyStatus? Identify = null,
    TachMappingStatus? TachMapping = null)
{
    public static MonitorSnapshot Unavailable(string status) =>
        new(status, Array.Empty<SensorReading>(), Array.Empty<FanReading>(), AppConfig.Empty);
}
