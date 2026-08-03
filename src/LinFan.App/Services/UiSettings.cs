// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// GUI-lokale Oberflächen-Einstellungen (per-User), bewusst getrennt vom Daemon-Config. Derzeit nur die
/// Fenster-Geometrie; <see cref="UiSettingsStore"/> persistiert sie als JSON. Felder sind nullable, damit
/// „nicht gesetzt" (erster Start) sauberer von „auf 0" unterscheidbar ist.
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

    /// <summary>Gewählte UI-Sprache; Default <see cref="LanguageChoice.System"/> (folgt der OS-Kultur).</summary>
    public LanguageChoice Language { get; init; } = LanguageChoice.System;

    /// <summary>Ob das Schließen-/Minimieren-Verhalten das Fenster ins Tray legt statt zu beenden (Default aus).</summary>
    public bool MinimizeToTray { get; init; }

    /// <summary>Ob beim Start automatisch auf neue Releases geprüft wird (Opt-out; Default an).</summary>
    public bool UpdateChecksEnabled { get; init; } = true;

    /// <summary>Zuletzt im Update-Banner weggeklickte Version; eine <b>neuere</b> Release zeigt wieder an.</summary>
    public string? DismissedUpdateVersion { get; init; }
}
