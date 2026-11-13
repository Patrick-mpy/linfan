// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// The temperature marks of the presentation: "warm" and "hot" (the overview's hot icon, bold value and hint, the
/// severity colour). Fixed on purpose - typical CPU/GPU ranges, independent of the daemon's fail-safe limit.
/// </summary>
public static class TemperatureLevels
{
    /// <summary>From here "warm" (amber); below it "cool" (green).</summary>
    public const double WarmC = 60.0;

    /// <summary>From here "hot" (red, icon, bold).</summary>
    public const double HotC = 80.0;

    /// <summary>A readable value at or above <see cref="HotC"/>.</summary>
    public static bool IsHot(double temperatureC) => !double.IsNaN(temperatureC) && temperatureC >= HotC;
}
