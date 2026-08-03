// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// Vom Nutzer gewählter Theme-Modus (GUI-lokal, in <see cref="UiSettings"/> persistiert).
/// <see cref="System"/> folgt dem Betriebssystem (Avalonia <c>ThemeVariant.Default</c>),
/// <see cref="Light"/>/<see cref="Dark"/> erzwingen die jeweilige Variante.
/// </summary>
public enum ThemeChoice
{
    System,
    Light,
    Dark,
}
