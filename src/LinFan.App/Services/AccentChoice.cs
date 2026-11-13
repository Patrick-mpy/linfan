// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// The accent colour the user picked (GUI-local, persisted in <see cref="UiSettings"/>): the theme's own
/// (<see cref="Standard"/>), one of the presets, or a colour of their own (<see cref="Custom"/>, hex in
/// <see cref="UiSettings.AccentCustom"/>). <see cref="AccentPalette"/> derives a readable variant per theme.
/// Persisted as a number (only ever append) and in backups by name (never rename).
/// </summary>
public enum AccentChoice
{
    Standard,
    Indigo,
    Violet,
    Pink,
    Orange,
    Emerald,
    Gray,
    Custom,
}
