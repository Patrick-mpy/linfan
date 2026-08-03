// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// Vom Nutzer gewählte UI-Sprache (GUI-lokal, in <see cref="UiSettings"/> persistiert).
/// <see cref="System"/> folgt der OS-Kultur (<c>de*</c> → Deutsch, sonst Englisch),
/// <see cref="German"/>/<see cref="English"/> erzwingen die jeweilige Sprache.
/// Spiegelt <see cref="ThemeChoice"/>.
/// </summary>
public enum LanguageChoice
{
    System,
    German,
    English,
}
