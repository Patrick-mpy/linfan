// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// GUI-local interface settings (per user), kept apart from the daemon's config: window geometry, theme, accent,
/// language, tray and update checks; <see cref="UiSettingsStore"/> persists them as JSON. The geometry fields are
/// nullable so that "not set yet" (first start) differs from "0".
/// </summary>
public sealed record UiSettings
{
    /// <summary>Fensterbreite in DIP (Normal-Zustand); <c>null</c> = noch nichts gespeichert.</summary>
    public double? Width { get; init; }

    /// <summary>Fensterhöhe in DIP (Normal-Zustand).</summary>
    public double? Height { get; init; }

    /// <summary>Fenster-X in physischen Pixeln (Normal-Zustand).</summary>
    public int? X { get; init; }

    /// <summary>Fenster-Y in physischen Pixeln (Normal-Zustand).</summary>
    public int? Y { get; init; }

    /// <summary>Beim letzten Schließen maximiert (Minimiert wird bewusst nicht als Startzustand gespeichert).</summary>
    public bool Maximized { get; init; }

    /// <summary>Gewählter Theme-Modus; Default <see cref="ThemeChoice.System"/> (folgt dem OS).</summary>
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>The accent colour: the theme's own (default), a preset, or <see cref="AccentCustom"/>.</summary>
    public AccentChoice Accent { get; init; } = AccentChoice.Standard;

    /// <summary>
    /// The user's own accent as "#RRGGBB"; <c>null</c> until they entered one. Kept while another accent is chosen,
    /// so switching back to "custom" brings it back.
    /// </summary>
    public string? AccentCustom { get; init; }

    /// <summary>Gewählte UI-Sprache; Default <see cref="LanguageChoice.System"/> (folgt der OS-Kultur).</summary>
    public LanguageChoice Language { get; init; } = LanguageChoice.System;

    /// <summary>Ob das Schließen-/Minimieren-Verhalten das Fenster ins Tray legt statt zu beenden (Default aus).</summary>
    public bool MinimizeToTray { get; init; }

    /// <summary>Ob beim Start automatisch auf neue Releases geprüft wird (Opt-out; Default an).</summary>
    public bool UpdateChecksEnabled { get; init; } = true;

    /// <summary>Zuletzt im Update-Banner weggeklickte Version; eine <b>neuere</b> Release zeigt wieder an.</summary>
    public string? DismissedUpdateVersion { get; init; }
}
