// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>Anzeige-Option für <see cref="InterpolationMode"/> im Editor (Enum + lokalisierter Klartext).</summary>
public sealed record InterpolationOption(InterpolationMode Value)
{
    /// <summary>In the order of the segmented switch: from straight to smooth, the stepped mode last.</summary>
    public static readonly IReadOnlyList<InterpolationOption> All = new[]
    {
        new InterpolationOption(InterpolationMode.Linear),
        new InterpolationOption(InterpolationMode.Spline),
        new InterpolationOption(InterpolationMode.Step),
    };

    public string Display => Localizer.Instance[$"InterpolationOption.{Value}"];

    /// <summary>Tooltip of the option: what it does.</summary>
    public string Hint => Localizer.Instance[$"InterpolationOption.{Value}Hint"];

    /// <summary>Short note shown next to the switch while the option is selected.</summary>
    public string Summary => Localizer.Instance[$"InterpolationOption.{Value}Summary"];

    /// <summary>Resource key of the option's little curve icon (Icons.axaml).</summary>
    public string IconKey => $"IconInterpolation{Value}";

    public static InterpolationOption For(InterpolationMode value) =>
        All.FirstOrDefault(o => o.Value == value) ?? All[0];

    public override string ToString() => Display;
}
