// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Anzeige-Option für <see cref="LanguageChoice"/> im Header-Umschalter. Die Sprachnamen sind
/// <b>Endonyme</b> (jede Sprache in sich selbst geschrieben) und bewusst NICHT lokalisiert - sie
/// bleiben in beiden Sprachlisten gleich (vermeidet das Henne-Ei-Problem beim Umschalten).
/// Spiegelt <see cref="ThemeOption"/>.
/// </summary>
public sealed record LanguageOption(LanguageChoice Value, string Display)
{
    public static readonly IReadOnlyList<LanguageOption> All = new[]
    {
        new LanguageOption(LanguageChoice.System, "System"),
        new LanguageOption(LanguageChoice.German, "Deutsch"),
        new LanguageOption(LanguageChoice.English, "English"),
    };

    public static LanguageOption For(LanguageChoice value) =>
        All.FirstOrDefault(o => o.Value == value) ?? All[0];

    public override string ToString() => Display;
}
