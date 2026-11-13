// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Display option for <see cref="LanguageChoice"/> in the language switch. The names are <b>endonyms</b> (each language
/// in itself) and deliberately not translated - they read the same in both languages, so a user who picked the wrong
/// one still finds their own. "System" names the language it resolves to, e.g. "System (Deutsch)".
/// </summary>
/// <param name="Name">The entry's name in the menu: „System" or the endonym.</param>
/// <param name="SystemLanguage">For „System": the endonym of the language it resolves to; <c>null</c> otherwise.</param>
public sealed record LanguageOption(LanguageChoice Value, string Name, string? SystemLanguage = null)
{
    public static readonly IReadOnlyList<LanguageOption> All = Build(Localizer.SystemLanguage);

    /// <param name="system">The language "System" resolves to (<see cref="LanguageChoice.German"/> or <see cref="LanguageChoice.English"/>).</param>
    public static IReadOnlyList<LanguageOption> Build(LanguageChoice system) => new[]
    {
        new LanguageOption(LanguageChoice.System, "System", Endonym(system)),
        new LanguageOption(LanguageChoice.German, Endonym(LanguageChoice.German)),
        new LanguageOption(LanguageChoice.English, Endonym(LanguageChoice.English)),
    };

    /// <summary>What the chip shows: the name, for „System" with the language it resolves to.</summary>
    public string Display => SystemLanguage is null ? Name : $"{Name} ({SystemLanguage})";

    private static string Endonym(LanguageChoice language) => language == LanguageChoice.German ? "Deutsch" : "English";

    public static LanguageOption For(LanguageChoice value) =>
        All.FirstOrDefault(o => o.Value == value) ?? All[0];

    public override string ToString() => Display;
}
