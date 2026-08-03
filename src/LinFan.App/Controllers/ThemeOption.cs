// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>Anzeige-Option für <see cref="ThemeChoice"/> im Header-Umschalter (Enum + deutscher Klartext).</summary>
public sealed record ThemeOption(ThemeChoice Value, string Display)
{
    public static readonly IReadOnlyList<ThemeOption> All = new[]
    {
        new ThemeOption(ThemeChoice.System, "System"),
        new ThemeOption(ThemeChoice.Light, "Hell"),
        new ThemeOption(ThemeChoice.Dark, "Dunkel"),
    };

    public static ThemeOption For(ThemeChoice value) =>
        All.FirstOrDefault(o => o.Value == value) ?? All[0];

    public override string ToString() => Display;
}
