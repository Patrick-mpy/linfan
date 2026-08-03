// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Erzeugt die drei vordefinierten Onboarding-Profile (Leise / Ausgewogen / Leistung) als reine, seiteneffektfreie
/// Factory-Methode. Keine externen Abhängigkeiten — jederzeit unit-testbar.
/// </summary>
public static class DefaultProfiles
{
    /// <summary>
    /// Baut die drei Onboarding-Standard-Profile für die übergebenen Lüfter und den primären Sensor.
    /// </summary>
    /// <param name="fans">
    /// Lüfter, die in die Profil-Zuordnungen aufgenommen werden. Leere Liste ist erlaubt (→ keine Assignments).
    /// </param>
    /// <param name="primarySensorId">
    /// Id des Haupttemperatursensors, der alle Kurven speist. Darf nicht <c>null</c> oder leer sein.
    /// </param>
    /// <returns>Exakt drei Profile in der Reihenfolge: silent, balanced, performance.</returns>
    public static IReadOnlyList<Profile> Build(IReadOnlyList<FanConfig> fans, string primarySensorId)
    {
        ArgumentNullException.ThrowIfNull(fans);
        if (string.IsNullOrEmpty(primarySensorId))
            throw new ArgumentException("primarySensorId darf nicht null oder leer sein.", nameof(primarySensorId));

        return new[]
        {
            BuildProfile(
                id: "silent",
                name: "Leise",
                primarySensorId: primarySensorId,
                fans: fans,
                points: new[]
                {
                    new CurvePoint(35, 0),
                    new CurvePoint(55, 0),
                    new CurvePoint(65, 25),
                    new CurvePoint(78, 55),
                    new CurvePoint(88, 100),
                }),
            BuildProfile(
                id: "balanced",
                name: "Ausgewogen",
                primarySensorId: primarySensorId,
                fans: fans,
                points: new[]
                {
                    new CurvePoint(30, 20),
                    new CurvePoint(50, 35),
                    new CurvePoint(65, 55),
                    new CurvePoint(80, 90),
                    new CurvePoint(90, 100),
                }),
            BuildProfile(
                id: "performance",
                name: "Leistung",
                primarySensorId: primarySensorId,
                fans: fans,
                points: new[]
                {
                    new CurvePoint(30, 35),
                    new CurvePoint(45, 55),
                    new CurvePoint(60, 80),
                    new CurvePoint(72, 100),
                }),
        };
    }

    private static Profile BuildProfile(
        string id,
        string name,
        string primarySensorId,
        IReadOnlyList<FanConfig> fans,
        CurvePoint[] points)
    {
        string curveId = $"{id}-curve";

        var curve = new CurveConfig
        {
            Id = curveId,
            Name = name,
            SourceSensorIds = new[] { primarySensorId },
            Aggregation = SensorAggregation.Max,
            InterpolationMode = InterpolationMode.Linear,
            HysteresisC = 2.0,
            Points = points,
        };

        var assignments = fans
            .Select(f => new ProfileAssignment(f.FanId, curveId))
            .ToList();

        return new Profile
        {
            Id = id,
            Name = name,
            Curves = new[] { curve },
            Assignments = assignments,
        };
    }
}
