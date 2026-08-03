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
    /// <summary><c>System</c> → <see cref="ThemeVariant.Default"/> (folgt OS), sonst die feste Variante.</summary>
    public static ThemeVariant ToVariant(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
