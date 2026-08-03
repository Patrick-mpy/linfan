// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Plausibilisiert eine geladene <see cref="AppConfig"/>, bevor sie den Regel-Loop steuert.
/// Wichtigster Punkt ist der sicherheitskritische <see cref="AppConfig.FailSafeTempC"/>: eine von
/// Hand editierte Datei darf den Watchdog nicht faktisch abschalten (z. B. <c>200</c> °C oder <c>0</c>).
/// Reine Hardware-Sicherheit (PWM-Grenzen, Kurven-Clamping) ist bereits durch <c>byte</c>-Typen und
/// das Clamping in <see cref="CurveEngine"/>/<see cref="ControlLoop"/> abgedeckt.
/// </summary>
public static class ConfigSanitizer
{
    public const double MinFailSafeC = 40.0;
    public const double MaxFailSafeC = 105.0;
    public const double DefaultFailSafeC = AppConfig.DefaultFailSafeTempC; // zentrale Quelle: AppConfig
    public const int MinPollIntervalMs = 200;

    /// <summary>Liefert eine bereinigte Kopie und sammelt menschenlesbare Warnungen zu Korrekturen.</summary>
    public static AppConfig Sanitize(AppConfig config, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);
        var w = new List<string>();

        double failSafe = config.FailSafeTempC;
        if (double.IsNaN(failSafe) || failSafe < MinFailSafeC || failSafe > MaxFailSafeC)
        {
            w.Add($"FailSafeTempC {failSafe:0.#} außerhalb [{MinFailSafeC:0}–{MaxFailSafeC:0}] °C — " +
                  $"auf sicheren Default {DefaultFailSafeC:0} °C gesetzt.");
            failSafe = DefaultFailSafeC;
        }

        int poll = config.PollIntervalMs;
        if (poll < MinPollIntervalMs)
        {
            w.Add($"PollIntervalMs {poll} < {MinPollIntervalMs} — auf {MinPollIntervalMs} gesetzt.");
            poll = MinPollIntervalMs;
        }

        IReadOnlyList<SensorConfig> sensors = DistinctById(config.Sensors, s => s.SensorId, "Sensor", w);
        IReadOnlyList<FanConfig> fans = DistinctById(SanitizeFans(config.Fans, w), f => f.FanId, "Lüfter", w);
        IReadOnlyList<CurveConfig> curves = SanitizeCurves(config.Curves, w);

        warnings = w;
        // Auch ohne Warnung kann die Schema-1→2-Migration (SourceSensorId → SourceSensorIds) eine neue
        // Kurven-Liste erzeugt haben — dann darf nicht die alte Instanz zurückgegeben werden.
        bool changed = w.Count > 0 || !ReferenceEquals(curves, config.Curves);
        return changed
            ? config with
            {
                FailSafeTempC = failSafe,
                PollIntervalMs = poll,
                Sensors = sensors,
                Fans = fans,
                Curves = curves,
            }
            : config;
    }

    /// <summary>
    /// Entfernt Einträge mit doppelter Id (erster gewinnt). Doppelte Fan-/Sensor-Ids können durch die
    /// hwmon-Id-Migration entstehen (zwei instabile Alt-Ids kollabieren auf dieselbe stabile Id) oder
    /// durch eine von Hand editierte Datei. Ein <c>ToDictionary</c> weiter oben (Snapshot-Bau, GUI) würde
    /// daran werfen und die App abstürzen lassen — hier einmalig, autoritär und mit Warnung bereinigt.
    /// Bei doppelfreier Eingabe wird dieselbe Instanz zurückgegeben (keine Allokation, keine „geändert"-Flags).
    /// </summary>
    private static IReadOnlyList<T> DistinctById<T>(
        IReadOnlyList<T> items, Func<T, string> idOf, string label, List<string> w)
    {
        List<T>? kept = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            string id = idOf(items[i]);
            if (seen.Add(id))
            {
                kept?.Add(items[i]);
                continue;
            }

            if (kept is null) // erstes Duplikat → ab hier materialisieren (Prefix ist dup-frei)
            {
                kept = new List<T>(items.Count);
                for (int j = 0; j < i; j++)
                    kept.Add(items[j]);
            }
            w.Add($"{label} '{id}': doppelter Eintrag entfernt (erster gewinnt).");
        }
        return kept ?? items;
    }

    /// <summary>Erzwingt <c>MinPwm ≤ MaxPwm</c> (sonst stünde der Lüfter dauerhaft auf MinPwm).</summary>
    private static IReadOnlyList<FanConfig> SanitizeFans(IReadOnlyList<FanConfig> fans, List<string> w)
    {
        List<FanConfig>? fixedFans = null;
        for (int i = 0; i < fans.Count; i++)
        {
            FanConfig f = fans[i];
            if (f.MaxPwm < f.MinPwm)
            {
                w.Add($"Lüfter {f.FanId}: MaxPwm {f.MaxPwm} < MinPwm {f.MinPwm} — MaxPwm auf {f.MinPwm} angehoben.");
                fixedFans ??= new List<FanConfig>(fans);
                fixedFans[i] = f with { MaxPwm = f.MinPwm };
            }
        }
        return fixedFans ?? fans;
    }

    /// <summary>
    /// Entfernt nicht-endliche Stützpunkte (NaN/∞), die das Sortieren/Interpolieren stören würden, und
    /// migriert das alte Einzel-Quellfeld (Schema 1) auf die Mehrfach-Quelle (Schema 2): ein gesetztes
    /// <see cref="CurveConfig.SourceSensorId"/> wandert in ein leeres <see cref="CurveConfig.SourceSensorIds"/>.
    /// Die Migration ist eine stille Normalisierung (keine Warnung) — ohne sie verlöre eine Altkurve
    /// ihren Quell-Sensor.
    /// </summary>
    private static IReadOnlyList<CurveConfig> SanitizeCurves(IReadOnlyList<CurveConfig> curves, List<string> w)
    {
        List<CurveConfig>? fixedCurves = null;
        for (int i = 0; i < curves.Count; i++)
        {
            CurveConfig c = curves[i];
            CurveConfig migrated = MigrateCurveSources(c);

            var clean = migrated.Points
                .Where(p => double.IsFinite(p.TemperatureC) && double.IsFinite(p.Percent))
                .ToList();

            bool pointsDropped = clean.Count != migrated.Points.Count;
            if (pointsDropped)
                w.Add($"Kurve '{c.Name}': {migrated.Points.Count - clean.Count} ungültige Stützpunkte (NaN/∞) entfernt.");

            if (!ReferenceEquals(migrated, c) || pointsDropped)
            {
                fixedCurves ??= new List<CurveConfig>(curves);
                fixedCurves[i] = pointsDropped ? migrated with { Points = clean } : migrated;
            }
        }
        return fixedCurves ?? curves;
    }

    /// <summary>Schema-1→2-Migration einer Kurve: leeres SourceSensorIds + gesetztes SourceSensorId → [SourceSensorId].</summary>
    private static CurveConfig MigrateCurveSources(CurveConfig c)
    {
        if (c.SourceSensorIds.Count == 0 && !string.IsNullOrEmpty(c.SourceSensorId))
            return c with { SourceSensorIds = new[] { c.SourceSensorId }, SourceSensorId = null };
        return c;
    }
}
