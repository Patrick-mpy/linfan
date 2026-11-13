// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// The overview's tiles besides the status (hottest sensor, fans, running profile) and the section captions, built from
/// one snapshot - pure, so plain unit tests cover it. Value equality keeps an unchanged tile from re-notifying the view.
/// </summary>
public sealed record OverviewTiles(
    string HottestValue, double HottestTemperature, bool HottestIsHot, string HottestName, string HottestTileName,
    string FanHeadline, string FanDetail, string FanTileName,
    string ProfileName, string ProfileDetail, string ProfileTileName,
    string LoopsSubtitle, string SensorCountText, string FanCountText)
{
    public static readonly OverviewTiles Empty = new("-", double.NaN, false, "", "", "-", "", "", "-", "", "", "", "", "");

    /// <param name="live">The last connected snapshot (frozen while offline).</param>
    /// <param name="connected">Offline the fans tile does not say who controls them - that is unknown then.</param>
    /// <param name="unsavedChanges">The editor holds changes that are not running yet.</param>
    public static OverviewTiles Build(MonitorSnapshot live, bool connected, bool unsavedChanges)
    {
        Localizer l = Localizer.Instance;
        AppConfig config = live.Config;

        // Hottest visible sensor.
        List<(SensorReading Reading, SensorConfig? Config)> sensors = live.Sensors
            .Where(s => s.Kind == SensorKind.Temperature)
            .Select(s => (s, config.Sensors.FirstOrDefault(c => c.SensorId == s.Id)))
            .Where(s => s.Item2?.Hidden != true)
            .ToList();
        var readable = sensors.Where(s => !double.IsNaN(s.Reading.Value)).ToList();
        string hottestValue = "-", hottestName = l["Overview.NoSensorsShort"];
        double hottestTemperature = double.NaN;
        bool hot = false;
        if (readable.Count > 0)
        {
            var h = readable.MaxBy(s => s.Reading.Value);
            hottestTemperature = h.Reading.Value;
            hot = TemperatureLevels.IsHot(hottestTemperature);
            hottestValue = hottestTemperature.ToString("0.0", CultureInfo.InvariantCulture) + " °C";
            hottestName = $"{h.Reading.Name} · {GroupName(h.Config?.Group, config)}";
        }
        string hottestSpoken = hot ? $"{hottestValue} ({l["Overview.HotName"]})" : hottestValue;

        // Fans: average power over the controllable visible fans, speed over those with a tachometer.
        List<FanReading> visibleFans = live.Fans
            .Where(f => config.Fans.FirstOrDefault(c => c.FanId == f.Id)?.Hidden != true)
            .ToList();
        List<FanReading> controllable = visibleFans.Where(f => f.CanControl).ToList();
        string fanHeadline = "-";
        if (controllable.Count > 0)
        {
            double pct = controllable.Average(f => PwmScale.ToPercent(f.Pwm));
            List<double> rpms = controllable.Select(f => f.Rpm).OfType<double>().Where(r => !double.IsNaN(r)).ToList();
            fanHeadline = rpms.Count == 0
                ? l.Format("Overview.FanAveragePercent", Math.Round(pct))
                : l.Format("Overview.FanAverage", Math.Round(pct), Math.Round(rpms.Average()));
        }
        var parts = new List<string>();
        if (connected)
        {
            // A dry run counts as "by curves": the chip itself says "(dry run)".
            int byCurve = controllable.Count(f => f.ControlState == FanControlState.Curve);
            int manual = controllable.Count(f => f.ControlState == FanControlState.Manual);
            int other = controllable.Count - byCurve - manual;
            if (byCurve > 0) parts.Add(l.Format("Overview.FanByCurves", byCurve));
            if (manual > 0) parts.Add(l.Format("Overview.FanManual", manual));
            if (other > 0) parts.Add(l.Format("Overview.FanOther", other));
        }
        string controllableText = l.Format("Overview.FanControllable", controllable.Count);
        string fanDetail = parts.Count == 0 ? controllableText : $"{controllableText} · {string.Join(", ", parts)}";

        // Running profile.
        string profileName = config.Profiles.FirstOrDefault(p => p.Id == config.ActiveProfileId)?.Name is { Length: > 0 } name ? name : "-";
        int active = config.Curves.Count(c => c.Enabled
                                               && CurveSourceResolver.ResolveSources(c.SourceSensorId, c.SourceSensorIds).Count > 0
                                               && config.Fans.Any(f => f.AssignedCurveId == c.Id));
        string activeText = active == 1 ? l["Overview.ProfileOneActive"] : l.Format("Overview.ProfileActive", active);
        string profileDetail = unsavedChanges ? $"{activeText} · {l["Overview.ProfileUnsaved"]}" : activeText;

        return new OverviewTiles(
            hottestValue, hottestTemperature, hot, hottestName,
            l.Format("Overview.TileName", l["Overview.HottestSensor"], hottestSpoken, hottestName),
            fanHeadline, fanDetail, l.Format("Overview.TileName", l["MainWindow.Fans"], fanHeadline, fanDetail),
            profileName, profileDetail, l.Format("Overview.TileName", l["Overview.ProfileTile"], profileName, profileDetail),
            l.Format("Overview.LoopsSubtitle", profileName),
            sensors.Count == 1 ? l["Overview.SensorCountOne"] : l.Format("Overview.SensorCount", sensors.Count),
            visibleFans.Count == 1 ? l["Overview.FanCountOne"] : l.Format("Overview.FanCount", visibleFans.Count));
    }

    /// <summary>A sensor's group as the tiles name it: the saved spelling, or "ungrouped".</summary>
    private static string GroupName(string? group, AppConfig config)
    {
        string key = SensorGroup.KeyOf(group);
        if (key.Length == 0)
            return GroupLabels.Ungrouped;
        return config.SensorGroups.FirstOrDefault(g => string.Equals(g, key, StringComparison.OrdinalIgnoreCase)) ?? key;
    }
}
