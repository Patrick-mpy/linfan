// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Upper bounds for a config that comes from outside the daemon - an IPC client, an imported backup, the file at
/// start. Every tick works through each fan and its curve's points, and every snapshot carries the whole config,
/// so without bounds one client could stretch the watchdog's cadence and inflate memory, disk and the traffic to
/// every other GUI. The bounds sit far above any real machine; a config past them is rejected as a whole rather
/// than truncated, since a cut-off curve would still drive hardware.
/// </summary>
public static class ConfigLimits
{
    public const int MaxFans = 64;
    public const int MaxSensors = 512;
    public const int MaxSensorGroups = 128;
    public const int MaxProfiles = 32;
    public const int MaxCurvesPerSet = 32;
    public const int MaxPointsPerCurve = 128;
    public const int MaxSourcesPerCurve = 128;
    public const int MaxTotalCurves = 256;
    public const int MaxTotalPoints = 8192;
    public const int MaxTextLength = 256;

    /// <summary>
    /// A calibration ramp at its default step takes 8 samples - the run-out phase before it adds none. Every snapshot with
    /// the config carries them.
    /// </summary>
    public const int MaxCalibrationSamples = 64;

    /// <summary>Names the first bound <paramref name="config"/> exceeds, or returns <c>null</c> if it is within all.</summary>
    /// <remarks>
    /// Counts what <see cref="ProfileService.EnsureProfiles"/> will make of the config: a profile without curves gets
    /// the top-level curves, so a list of empty profiles multiplies one curve list - this is checked before that
    /// expansion happens, on the raw input.
    /// </remarks>
    public static string? Check(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Fans.Count > MaxFans)
            return $"{config.Fans.Count} Lüfter (max. {MaxFans})";
        if (config.Sensors.Count > MaxSensors)
            return $"{config.Sensors.Count} Sensoren (max. {MaxSensors})";
        // A hand-edited "sensorGroups": null reads as no groups (see ConfigSanitizer) - a display-only list must not
        // cost a file its curves.
        IReadOnlyList<string> sensorGroups = config.SensorGroups ?? [];
        if (sensorGroups.Count > MaxSensorGroups)
            return $"{sensorGroups.Count} Sensorgruppen (max. {MaxSensorGroups})";
        if (config.Profiles.Count > MaxProfiles)
            return $"{config.Profiles.Count} Profile (max. {MaxProfiles})";
        if (!TextOk(config.ActiveProfileId))
            return "Text zu lang";

        foreach (FanConfig f in config.Fans)
        {
            if (!TextOk(f.FanId) || !TextOk(f.Name) || !TextOk(f.AssignedCurveId) || !TextOk(f.RpmSource)
                || !TextOk(f.Calibration?.Tachometer))
                return "Text zu lang";
            // A hand-edited "samples": null gets here before the sanitizer.
            int samples = f.Calibration?.Samples?.Count ?? 0;
            if (samples > MaxCalibrationSamples)
                return $"{samples} Kalibrier-Messpunkte eines Lüfters (max. {MaxCalibrationSamples})";
        }
        foreach (SensorConfig s in config.Sensors)
            if (!TextOk(s.SensorId) || !TextOk(s.Name) || !TextOk(s.Group))
                return "Text zu lang";
        if (!sensorGroups.All(TextOk))
            return "Text zu lang";

        int totalCurves = 0, totalPoints = 0;
        string? Add(IReadOnlyList<CurveConfig> curves)
        {
            if (curves.Count > MaxCurvesPerSet)
                return $"{curves.Count} Kurven in einem Set (max. {MaxCurvesPerSet})";
            foreach (CurveConfig c in curves)
            {
                if (c.Points.Count > MaxPointsPerCurve)
                    return $"{c.Points.Count} Stützpunkte in einer Kurve (max. {MaxPointsPerCurve})";
                if (c.SourceSensorIds.Count > MaxSourcesPerCurve)
                    return $"{c.SourceSensorIds.Count} Quell-Sensoren in einer Kurve (max. {MaxSourcesPerCurve})";
                if (!TextOk(c.Id) || !TextOk(c.Name) || !TextOk(c.SourceSensorId) || !c.SourceSensorIds.All(TextOk))
                    return "Text zu lang";
                totalPoints += c.Points.Count;
            }
            totalCurves += curves.Count;
            return null;
        }

        if (Add(config.Curves) is { } curvesViolation)
            return curvesViolation;
        foreach (Profile p in config.Profiles)
        {
            if (!TextOk(p.Id) || !TextOk(p.Name))
                return "Text zu lang";
            if (p.Assignments.Count > MaxFans)
                return $"{p.Assignments.Count} Zuordnungen in einem Profil (max. {MaxFans})";
            foreach (ProfileAssignment a in p.Assignments)
                if (!TextOk(a.FanId) || !TextOk(a.CurveId))
                    return "Text zu lang";
            if (Add(p.Curves.Count == 0 ? config.Curves : p.Curves) is { } profileViolation)
                return profileViolation;
        }

        if (totalCurves > MaxTotalCurves)
            return $"{totalCurves} Kurven insgesamt (max. {MaxTotalCurves})";
        if (totalPoints > MaxTotalPoints)
            return $"{totalPoints} Stützpunkte insgesamt (max. {MaxTotalPoints})";
        return null;
    }

    private static bool TextOk(string? s) => (s?.Length ?? 0) <= MaxTextLength;
}
