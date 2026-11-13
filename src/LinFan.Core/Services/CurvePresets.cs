// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// The point tables new curves start from: the onboarding profiles (<see cref="DefaultProfiles"/>) and the curve
/// editor's templates, all meant for <see cref="InterpolationMode.Linear"/>. Every preset reaches 100 % at 86 °C at
/// the latest, below the fail-safe default (<see cref="AppConfig.DefaultFailSafeTempC"/>), so the fans are at full
/// speed before the watchdog drops them to hardware auto.
/// </summary>
public static class CurvePresets
{
    public static IReadOnlyList<CurvePoint> Silent { get; } =
        [new(35, 0), new(55, 0), new(65, 25), new(78, 55), new(86, 100)];

    public static IReadOnlyList<CurvePoint> Balanced { get; } =
        [new(30, 20), new(50, 35), new(65, 55), new(80, 90), new(86, 100)];

    public static IReadOnlyList<CurvePoint> Performance { get; } =
        [new(30, 35), new(45, 55), new(60, 80), new(72, 100)];

    /// <summary>A fixed 40 % that still ramps to 100 % between 80 and 86 °C instead of leaving the heat to the watchdog.</summary>
    public static IReadOnlyList<CurvePoint> Constant40 { get; } =
        [new(30, 40), new(80, 40), new(86, 100)];
}
