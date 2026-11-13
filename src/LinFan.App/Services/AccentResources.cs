// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Media;

namespace LinFan.App.Services;

/// <summary>
/// What puts a chosen accent colour on screen, in two parts.
/// <para>
/// <see cref="Build"/>: a resource dictionary with the accent tokens of every theme. The app adds it after
/// Theme/SemanticColors.axaml, so its keys win over the themes' own (merged dictionaries are searched last to first).
/// It carries one entry per theme with the same keys - a variant without an entry would fall back to the dark one inside
/// this dictionary and beat its own theme's tokens.
/// </para>
/// <para>
/// <see cref="FluentAccents"/>: the accent of FluentTheme's own controls (check boxes, switches, sliders, focus
/// frames). It cannot go into that dictionary: Fluent only has a light and a dark palette and resolves its accent under
/// those two keys, never under a variant of our own, and its light brushes ignore an application-level
/// <c>SystemAccentColor</c> altogether (headless spike, Avalonia 12.0.4). So the app sets the accent of the two palettes
/// - the dark one to the accent of the theme on screen when that is terminal or high contrast.
/// </para>
/// </summary>
public static class AccentResources
{
    private static readonly ThemeChoice[] Themes =
        [ThemeChoice.Light, ThemeChoice.Dark, ThemeChoice.Terminal, ThemeChoice.HighContrast];

    /// <summary>
    /// The token override for an accent; <c>null</c> for the themes' own accent. An own colour that is not a valid hex
    /// value counts as the themes' own accent.
    /// </summary>
    public static ResourceDictionary? Build(AccentChoice accent, string? customHex)
    {
        if (!IsChosen(accent, customHex, out Color custom))
            return null;

        var overrides = new ResourceDictionary();
        foreach (ThemeChoice theme in Themes)
        {
            AccentTokens tokens = AccentPalette.Derive(theme, accent, custom)!;
            overrides.ThemeDictionaries[ThemeVariantMap.ToVariant(theme)] = new ResourceDictionary
            {
                ["Accent"] = new SolidColorBrush(tokens.Accent),
                ["AccentFill"] = new SolidColorBrush(tokens.Fill),
                ["AccentHover"] = new SolidColorBrush(tokens.Hover),
                ["AccentPressed"] = new SolidColorBrush(tokens.Pressed),
                ["AccentTint"] = new SolidColorBrush(tokens.Tint),
                ["AccentColor"] = tokens.Accent,
                ["AccentShadeColor"] = tokens.Shade,
                ["ClampColor"] = tokens.Clamp,
            };
        }
        return overrides;
    }

    /// <summary>
    /// The accent of Fluent's light and dark palette for a theme choice: the light one as the light theme shows the
    /// accent, the dark one as the dark theme - or terminal / high contrast while one of those is chosen.
    /// </summary>
    public static (Color Light, Color Dark) FluentAccents(ThemeChoice theme, AccentChoice accent, string? customHex)
    {
        if (!IsChosen(accent, customHex, out Color custom))
            accent = AccentChoice.Standard;
        ThemeChoice dark = theme is ThemeChoice.Terminal or ThemeChoice.HighContrast ? theme : ThemeChoice.Dark;
        return (AccentPalette.AccentOf(ThemeChoice.Light, accent, custom), AccentPalette.AccentOf(dark, accent, custom));
    }

    private static bool IsChosen(AccentChoice accent, string? customHex, out Color custom)
    {
        custom = default;
        return accent != AccentChoice.Standard
            && (accent != AccentChoice.Custom || ColorContrast.TryParseHex(customHex, out custom));
    }
}
