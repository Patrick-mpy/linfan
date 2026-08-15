// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Schema-2→3-Migration: schreibt instabile hwmon-Ids (<c>hwmonN/temp1</c>) auf das stabile
/// <c>chip/channel</c>-Schema um. Reine Funktion über <see cref="AppConfig"/> + einer
/// <c>legacy→stable</c>-Zuordnung (vom Backend per <see cref="Abstractions.ILegacyIdMap"/> geliefert),
/// damit sie ohne Hardware testbar bleibt.
/// <para>
/// Berührt <b>nur</b> Hardware-Ids: Sensor-Ids (<see cref="SensorConfig.SensorId"/>,
/// <see cref="CurveConfig.SourceSensorId"/>/<see cref="CurveConfig.SourceSensorIds"/>) und Lüfter-Ids
/// (<see cref="FanConfig.FanId"/>, <see cref="ProfileAssignment.FanId"/>) - auch in den Kurven/
/// Zuordnungen jedes <see cref="Profile"/>. Kurven- und Profil-Ids bleiben unangetastet.
/// </para>
/// <para>
/// <b>Best effort &amp; idempotent:</b> nur Ids, die als Schlüssel in der Zuordnung stehen, werden
/// ersetzt. Schon migrierte (stabile) Ids sind keine Schlüssel mehr → ein erneuter Aufruf ist ein
/// No-op. Nicht aufgelöste Alt-Ids bleiben stehen und degradieren wie gehabt.
/// </para>
/// </summary>
public static class HwmonIdMigration
{
    /// <summary>Schema-Version nach erfolgter Id-Migration (= aktuelle Version).</summary>
    public const int StableIdSchemaVersion = AppConfig.CurrentSchemaVersion;

    /// <summary>
    /// Liefert eine migrierte Kopie. <paramref name="changed"/> ist <c>true</c>, wenn mindestens eine Id
    /// ersetzt wurde (dann ist auch <see cref="AppConfig.SchemaVersion"/> auf
    /// <see cref="StableIdSchemaVersion"/> gehoben); sonst wird die unveränderte Instanz zurückgegeben.
    /// </summary>
    public static AppConfig Apply(
        AppConfig config, IReadOnlyDictionary<string, string> legacyToStable, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(legacyToStable);

        changed = false;
        if (legacyToStable.Count == 0)
            return config;

        bool any = false;
        string Remap(string id)
        {
            if (legacyToStable.TryGetValue(id, out string? stable)
                && !string.Equals(stable, id, StringComparison.Ordinal))
            {
                any = true;
                return stable;
            }
            return id;
        }
        string? RemapNullable(string? id) => id is null ? null : Remap(id);
        CurveConfig RemapCurve(CurveConfig c) => c with
        {
            SourceSensorId = RemapNullable(c.SourceSensorId),
            SourceSensorIds = c.SourceSensorIds.Select(Remap).ToArray(),
        };

        // DistinctBy (erster gewinnt): kollabieren zwei Alt-Ids auf dieselbe stabile Id, darf nur ein
        // Eintrag übrig bleiben - sonst persistierte die Migration eine Config mit doppelter Id, an der
        // ein späteres ToDictionary (Snapshot/GUI) werfen würde.
        var sensors = config.Sensors.Select(s => s with { SensorId = Remap(s.SensorId) })
            .DistinctBy(s => s.SensorId, StringComparer.Ordinal).ToArray();
        var fans = config.Fans.Select(f => f with { FanId = Remap(f.FanId) })
            .DistinctBy(f => f.FanId, StringComparer.Ordinal).ToArray();
        var curves = config.Curves.Select(RemapCurve).ToArray();
        var profiles = config.Profiles.Select(p => p with
        {
            Curves = p.Curves.Select(RemapCurve).ToArray(),
            Assignments = p.Assignments.Select(a => a with { FanId = Remap(a.FanId) }).ToArray(),
        }).ToArray();

        if (!any)
            return config; // keine Alt-Id getroffen → schon stabil (oder fremde Ids), nichts anfassen

        changed = true;
        return config with
        {
            SchemaVersion = StableIdSchemaVersion,
            Sensors = sensors,
            Fans = fans,
            Curves = curves,
            Profiles = profiles,
        };
    }
}
