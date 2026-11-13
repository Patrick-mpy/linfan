// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// A tile of the theme grid for <see cref="ThemeChoice"/> (enum + translated label and hint). Unlike
/// <see cref="LanguageOption"/> (endonyms, deliberately untranslated) the texts follow the UI language, so
/// they have to be rebuilt on a language change - that is what <see cref="Build"/> is for.
/// </summary>
public sealed record ThemeOption(ThemeChoice Value, string Display, string Hint)
{
    /// <summary>Fresh options, labelled in the current UI language.</summary>
    public static IReadOnlyList<ThemeOption> Build() => new[]
    {
        new ThemeOption(ThemeChoice.System, Localizer.Instance["Theme.System"], Localizer.Instance["Theme.SystemHint"]),
        new ThemeOption(ThemeChoice.Light, Localizer.Instance["Theme.Light"], Localizer.Instance["Theme.LightHint"]),
        new ThemeOption(ThemeChoice.Dark, Localizer.Instance["Theme.Dark"], Localizer.Instance["Theme.DarkHint"]),
        new ThemeOption(ThemeChoice.Terminal, Localizer.Instance["Theme.Terminal"], Localizer.Instance["Theme.TerminalHint"]),
        new ThemeOption(ThemeChoice.HighContrast, Localizer.Instance["Theme.HighContrast"], Localizer.Instance["Theme.HighContrastHint"]),
    };

    // The preview has one fixed ThemeVariantScope per theme and shows the ones these flags pick: a scope whose variant is
    // bound and equals the one it inherits keeps its dynamic resources unresolved (Avalonia 12.0.4).

    /// <summary>The preview shows the light variant - alone, or as the left half of "System".</summary>
    public bool ShowsLight => Value is ThemeChoice.System or ThemeChoice.Light;

    /// <summary>The preview shows the dark variant - alone, or as the right half of "System".</summary>
    public bool ShowsDark => Value is ThemeChoice.System or ThemeChoice.Dark;

    public bool ShowsTerminal => Value == ThemeChoice.Terminal;

    public bool ShowsHighContrast => Value == ThemeChoice.HighContrast;

    /// <summary>
    /// Option for an enum value. Deliberately returns a fresh instance from <see cref="Build"/>: records
    /// compare by value, so the list still finds its selection in the equally freshly built list -
    /// including right after a language change.
    /// </summary>
    public static ThemeOption For(ThemeChoice value)
    {
        IReadOnlyList<ThemeOption> all = Build();
        return all.FirstOrDefault(o => o.Value == value) ?? all[0];
    }

    public override string ToString() => Display;
}
