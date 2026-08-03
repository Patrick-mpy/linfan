// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// JSON-Persistenz der <see cref="AppConfig"/>. Der Pfad ist <c>LINFAN_CONFIG</c> (Override, z. B. für
/// den Linux-System-Dienst auf <c>/etc/linfan/config.json</c>), sonst OS-konform: Linux/macOS per-User
/// (<c>~/.config/linfan</c>), Windows <b>maschinenweit</b> (<c>%ProgramData%\linfan</c>) — damit der als
/// SYSTEM laufende Dienst und die User-GUI garantiert dieselbe Datei nutzen (der per-User-<c>%AppData%</c>
/// des SYSTEM-Profils wäre für die GUI unsichtbar). Speichern erfolgt atomar (temp-Datei + Move).
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ConfigPath { get; }

    /// <summary>Gibt an, ob bereits eine persistierte Konfiguration existiert — First-Run-Signal.</summary>
    public bool Exists => File.Exists(ConfigPath);

    /// <param name="path">Optionaler expliziter Pfad (v. a. für Tests); sonst der OS-Standardpfad.</param>
    public JsonConfigStore(string? path = null) => ConfigPath = path ?? DefaultPath();

    public static string DefaultPath() => ResolveDefaultPath(
        Environment.GetEnvironmentVariable("LINFAN_CONFIG"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsWindows());

    /// <summary>
    /// Reine Pfad-Auflösung (testbar): <c>LINFAN_CONFIG</c> gewinnt. Sonst der OS-konforme Basisordner —
    /// auf <b>Windows</b> maschinenweit (<paramref name="commonAppData"/> = <c>%ProgramData%</c>), damit der
    /// als SYSTEM laufende Dienst und die User-GUI dieselbe Datei sehen; auf Linux/macOS per-User
    /// (<paramref name="appData"/> = <c>~/.config</c>). Ist der Basisordner leer, greift
    /// <paramref name="userProfile"/><c>/.config</c> als Fallback.
    /// </summary>
    internal static string ResolveDefaultPath(
        string? overridePath, string appData, string commonAppData, string userProfile, bool windows)
    {
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        string baseDir = windows ? commonAppData : appData;
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(userProfile, ".config");
        return Path.Combine(baseDir, "linfan", "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return AppConfig.Empty;

        try
        {
            string json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? AppConfig.Empty;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Konfiguration unter {ConfigPath} ist beschädigt: {ex.Message}", ex);
        }
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // GetDirectoryName liefert "" (nicht null) bei einem reinen Dateinamen ohne Verzeichnisanteil;
        // Directory.CreateDirectory("") würfe. Dann liegt die Datei im aktuellen Arbeitsverzeichnis.
        string? dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}
