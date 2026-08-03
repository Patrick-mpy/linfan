// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.App.Services;

/// <summary>
/// Datei-Format für Export/Import eines vollständigen LinFan-Backups: die Daemon-Config (Sensoren,
/// Lüfter, Profile, Kurven — <see cref="AppConfig"/>) plus die GUI-lokalen Prefs (Theme/Sprache/Tray).
/// Als JSON serialisiert. <see cref="FormatVersion"/> erlaubt spätere, bewusste Format-Migrationen —
/// ein unbekannter (höherer) Wert wird beim Import abgelehnt statt fehlinterpretiert.
/// </summary>
public sealed record LinFanBackup
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>Die editierbare Daemon-Config (autoritativ beim Import via ReplaceConfig).</summary>
    public AppConfig Config { get; init; } = AppConfig.Empty;

    /// <summary>GUI-lokale Oberflächen-Prefs (bewusst nur diese drei — keine Fenstergeometrie).</summary>
    public BackupUiPrefs Ui { get; init; } = new();
}

/// <summary>Der im Backup gesicherte Teil der <see cref="UiSettings"/> (ohne Fenstergeometrie).</summary>
public sealed record BackupUiPrefs
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
    public LanguageChoice Language { get; init; } = LanguageChoice.System;
    public bool MinimizeToTray { get; init; }
    public bool UpdateChecksEnabled { get; init; } = true;
}
