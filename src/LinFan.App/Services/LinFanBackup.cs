// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using LinFan.Core.Models;

namespace LinFan.App.Services;

/// <summary>
/// Datei-Format für Export/Import eines vollständigen LinFan-Backups: die Daemon-Config (Sensoren,
/// Lüfter, Profile, Kurven - <see cref="AppConfig"/>) plus die GUI-lokalen Prefs (Theme/Akzent/Sprache/Tray).
/// Als JSON serialisiert. <see cref="FormatVersion"/> erlaubt spätere, bewusste Format-Migrationen -
/// ein unbekannter (höherer) Wert wird beim Import abgelehnt statt fehlinterpretiert.
/// </summary>
public sealed record LinFanBackup
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>Die editierbare Daemon-Config (autoritativ beim Import via ReplaceConfig).</summary>
    public AppConfig Config { get; init; } = AppConfig.Empty;

    /// <summary>GUI-lokale Oberflächen-Prefs (Darstellung, Sprache, Tray - keine Fenstergeometrie).</summary>
    public BackupUiPrefs Ui { get; init; } = new();
}

/// <summary>
/// The part of the <see cref="UiSettings"/> a backup keeps (no window geometry). Not whether to look for updates:
/// restoring a backup - perhaps someone else's - must not opt the user out of update notices. An older backup's
/// <c>UpdateChecksEnabled</c> is ignored on import. The choices travel by name; one this build does not know reads as
/// the default instead of failing the backup (<see cref="LenientEnumConverter{T}"/>).
/// </summary>
public sealed record BackupUiPrefs
{
    [JsonConverter(typeof(LenientEnumConverter<ThemeChoice>))]
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    [JsonConverter(typeof(LenientEnumConverter<AccentChoice>))]
    public AccentChoice Accent { get; init; } = AccentChoice.Standard;

    public string? AccentCustom { get; init; }

    [JsonConverter(typeof(LenientEnumConverter<LanguageChoice>))]
    public LanguageChoice Language { get; init; } = LanguageChoice.System;

    public bool MinimizeToTray { get; init; }
}
