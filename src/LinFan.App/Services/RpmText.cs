// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace LinFan.App.Services;

/// <summary>How a live fan speed reads in the settings: „1180 RPM", or „n/a" without a readable tach (null/NaN).</summary>
internal static class RpmText
{
    public static bool IsKnown(double? rpm) => rpm is { } r && !double.IsNaN(r);

    public static string Of(double? rpm) => IsKnown(rpm) ? string.Create(CultureInfo.InvariantCulture, $"{rpm:0} RPM") : "n/a";

    /// <summary>The bare number („1180") for a large readout, or „n/a".</summary>
    public static string Number(double? rpm) => IsKnown(rpm) ? string.Create(CultureInfo.InvariantCulture, $"{rpm:0}") : "n/a";
}
