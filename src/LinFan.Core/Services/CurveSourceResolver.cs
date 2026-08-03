// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Zentraler Helfer für die Kurven-Quellen-Auflösung und sicheres Enum-Parsen.
/// Fasst Logik zusammen, die andernfalls an mehreren Stellen kopiert würde.
/// </summary>
public static class CurveSourceResolver
{
    /// <summary>
    /// Liefert die effektive Quell-Sensor-Liste nach dem 3-Zweig-Muster:
    /// <list type="number">
    ///   <item><description><paramref name="modern"/> wenn nicht leer.</description></item>
    ///   <item><description><paramref name="legacySingle"/> als Einzel-Element-Liste, wenn nicht null/leer.</description></item>
    ///   <item><description>Sonst <see cref="Array.Empty{T}"/>.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> ResolveSources(
        string? legacySingle,
        IReadOnlyList<string>? modern)
    {
        if (modern is { Count: > 0 })
            return modern;

        if (!string.IsNullOrEmpty(legacySingle))
            return [legacySingle];

        return Array.Empty<string>();
    }

    /// <summary>
    /// Parst <paramref name="value"/> in <typeparamref name="T"/> mit einem zusätzlichen
    /// <see cref="Enum.IsDefined"/>-Guard: numerische Strings wie "999", die außerhalb des
    /// deklarierten Wertebereichs liegen, werden als ungültig behandelt und ergeben
    /// <paramref name="fallback"/>.
    /// </summary>
    public static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum
    {
        if (string.IsNullOrEmpty(value))
            return fallback;

        if (!Enum.TryParse<T>(value, ignoreCase: true, out T result))
            return fallback;

        if (!Enum.IsDefined(typeof(T), result))
            return fallback;

        return result;
    }

    /// <summary>
    /// Bequemlichkeits-Überladung: parst <paramref name="value"/> als
    /// <see cref="SensorAggregation"/>; bei Fehlschlag wird
    /// <see cref="SensorAggregation.Max"/> zurückgegeben.
    /// </summary>
    public static SensorAggregation ParseAggregation(string? value) =>
        ParseEnum(value, SensorAggregation.Max);
}
