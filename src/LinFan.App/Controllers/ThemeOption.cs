// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Display option for <see cref="ThemeChoice"/> in the header switch (enum + translated label). Unlike
/// <see cref="LanguageOption"/> (endonyms, deliberately untranslated) the labels follow the UI language, so
/// they have to be rebuilt on a language change - that is what <see cref="Build"/> is for.
/// </summary>
public sealed record ThemeOption(ThemeChoice Value, string Display)
{
    /// <summary>Fresh options, labelled in the current UI language.</summary>
    public static IReadOnlyList<ThemeOption> Build() => new[]
    {
        new ThemeOption(ThemeChoice.System, Localizer.Instance["Theme.System"]),
        new ThemeOption(ThemeChoice.Light, Localizer.Instance["Theme.Light"]),
        new ThemeOption(ThemeChoice.Dark, Localizer.Instance["Theme.Dark"]),
    };

    /// <summary>
    /// Option for an enum value. Deliberately returns a fresh instance from <see cref="Build"/>: records
    /// compare by value, so the ComboBox still finds its selection in the equally freshly built list -
    /// including right after a language change.
    /// </summary>
    public static ThemeOption For(ThemeChoice value)
    {
        IReadOnlyList<ThemeOption> all = Build();
        return all.FirstOrDefault(o => o.Value == value) ?? all[0];
    }

    public override string ToString() => Display;
}
