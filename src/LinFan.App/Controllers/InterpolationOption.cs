// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>Anzeige-Option für <see cref="InterpolationMode"/> im Editor (Enum + lokalisierter Klartext).</summary>
public sealed record InterpolationOption(InterpolationMode Value)
{
    public static readonly IReadOnlyList<InterpolationOption> All = new[]
    {
        new InterpolationOption(InterpolationMode.Linear),
        new InterpolationOption(InterpolationMode.Spline),
    };

    public string Display => Localizer.Instance[$"InterpolationOption.{Value}"];

    public static InterpolationOption For(InterpolationMode value) =>
        All.FirstOrDefault(o => o.Value == value) ?? All[0];

    public override string ToString() => Display;
}
