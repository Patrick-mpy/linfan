// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Styling;

namespace LinFan.App.Services;

/// <summary>
/// Reine Abbildung zwischen dem nutzersichtbaren <see cref="ThemeChoice"/> und Avalonias
/// <see cref="ThemeVariant"/>. Bewusst ohne Bezug auf <c>Application</c>, damit die Logik ohne
/// laufende Avalonia-App unit-testbar bleibt; das eigentliche Anwenden
/// (<c>Application.RequestedThemeVariant = …</c>) liegt in der View-/App-Schicht.
/// </summary>
public static class ThemeVariantMap
{
    /// <summary>
    /// Phosphor green on black, monospace, square. Inherits Dark: a resource its own theme dictionaries
    /// (Theme/SemanticColors.axaml, Theme/Tokens.axaml) leave out falls back to the dark one. Always use this
    /// instance - <c>RequestedThemeVariant="Terminal"</c> in XAML would build a variant without that inheritance.
    /// </summary>
    public static readonly ThemeVariant Terminal = new("Terminal", ThemeVariant.Dark);

    /// <summary>Black and white, every text at least 7:1, outlined surfaces. Inherits Dark like <see cref="Terminal"/>.</summary>
    public static readonly ThemeVariant HighContrast = new("HighContrast", ThemeVariant.Dark);

    /// <summary><c>System</c> → <see cref="ThemeVariant.Default"/> (folgt OS), sonst die feste Variante.</summary>
    public static ThemeVariant ToVariant(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,
        ThemeChoice.Terminal => Terminal,
        ThemeChoice.HighContrast => HighContrast,
        _ => ThemeVariant.Default,
    };

    /// <summary>
    /// The concrete theme an actual (OS-resolved) variant shows - never <see cref="ThemeChoice.System"/>. An unknown
    /// variant counts as what it inherits from, finally as dark.
    /// </summary>
    public static ThemeChoice Concrete(ThemeVariant? actual)
    {
        for (ThemeVariant? v = actual; v is not null; v = v.InheritVariant)
        {
            if (v == ThemeVariant.Light)
                return ThemeChoice.Light;
            if (v == ThemeVariant.Dark)
                return ThemeChoice.Dark;
            if (v == Terminal)
                return ThemeChoice.Terminal;
            if (v == HighContrast)
                return ThemeChoice.HighContrast;
        }
        return ThemeChoice.Dark;
    }

    /// <summary>Whether a variant is dark - Dark itself or one that inherits from it (native title bar, Windows).</summary>
    public static bool IsDark(ThemeVariant? variant)
    {
        for (ThemeVariant? v = variant; v is not null; v = v.InheritVariant)
        {
            if (v == ThemeVariant.Dark)
                return true;
        }
        return false;
    }
}
