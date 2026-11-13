// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace LinFan.Core.Services;

/// <summary>
/// JSON-Persistenz der <see cref="AppConfig"/>. Der Pfad ist <c>LINFAN_CONFIG</c> (Override, z. B. für
/// den Linux-System-Dienst auf <c>/etc/linfan/config.json</c>), sonst OS-konform: Linux/macOS per-User
/// (<c>~/.config/linfan</c>), Windows <b>maschinenweit</b> (<c>%ProgramData%\linfan</c>) - damit der als
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

    /// <summary>
    /// <see cref="Save"/> refuses a config larger than this, so no client can make the daemon persist - and relay to
    /// every GUI on every tick - an inflated config. A real one stays far below it.
    /// </summary>
    public const int MaxSaveBytes = 4 * 1024 * 1024;

    /// <summary>
    /// <see cref="Load"/> reads more than <see cref="Save"/> writes: a file an older version let grow still parses,
    /// so its over-temperature limit carries over when the daemon sets the rest aside.
    /// </summary>
    public const int MaxLoadBytes = 64 * 1024 * 1024;

    public string ConfigPath { get; }

    /// <summary>Gibt an, ob bereits eine persistierte Konfiguration existiert - First-Run-Signal.</summary>
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
    /// Reine Pfad-Auflösung (testbar): <c>LINFAN_CONFIG</c> gewinnt. Sonst der OS-konforme Basisordner -
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

        using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length > MaxLoadBytes)
            throw new InvalidOperationException(
                $"Konfiguration unter {ConfigPath} ist mit {length} Bytes zu groß (max. {MaxLoadBytes}).");

        AppConfig config;
        try
        {
            // Through a reader, like File.ReadAllText before: it honors a UTF-16 byte order mark (a file a Windows editor
            // saved as "Unicode"), which the serializer's own stream reading does not.
            using var reader = new StreamReader(stream, leaveOpen: true);
            config = JsonSerializer.Deserialize<AppConfig>(reader.ReadToEnd(), Options) ?? AppConfig.Empty;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Konfiguration unter {ConfigPath} ist beschädigt: {ex.Message}", ex);
        }

        NarrowToOwnerOnly(stream.SafeFileHandle);
        return config;
    }

    /// <summary>
    /// A config from before files were created owner-only (0644 under the default umask) turns owner-only once it has
    /// been read, not only with the next save. Only group and other bits go, only after the file parsed, and only
    /// through the handle it was read from - not when the path is a link, so a root daemon run with a user's HOME does
    /// not change a file a link there points to. Best effort: a reader that does not own the file (a CLI without root)
    /// or a read-only file system leaves it.
    /// </summary>
    private void NarrowToOwnerOnly(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows() || new FileInfo(ConfigPath).LinkTarget is not null)
            return;

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(handle);
            UnixFileMode narrowed = mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (narrowed != mode)
                File.SetUnixFileMode(handle, narrowed);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not this reader's to change; the next save by the owner creates the file owner-only anyway.
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

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(config, Options);
        if (json.Length > MaxSaveBytes)
            throw new InvalidOperationException(
                $"Konfiguration mit {json.Length} Bytes zu groß (max. {MaxSaveBytes}) - nicht gespeichert.");

        // A fresh, unpredictable temp name created exclusively: CreateNew refuses anything already there - a symlink
        // a user planted in a config folder they own included (a root daemon run with the user's HOME; /etc/linfan is
        // root's) - and the random name keeps such a plant from blocking every save. The rename then replaces
        // config.json itself, never a file a link there points to.
        string tmp = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Deliberately no fsync: the daemon saves on its loop thread before the watchdog's tick, and a flush on a
            // congested or failing disk would hold the watchdog for as long. ext4 and btrfs flush a file replaced by
            // rename on their own; elsewhere a power loss right after a save can leave an unusable file, which the next
            // start sets aside - fans on hardware auto.
            using (var stream = new FileStream(tmp, NewOwnerOnlyFile()))
                stream.Write(json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); }
            catch { /* the save failed already - that is what the caller hears about */ }
            throw;
        }
    }

    /// <summary>The config is the daemon's business: created owner-only (the default umask makes it world-readable).</summary>
    private static FileStreamOptions NewOwnerOnlyFile()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    /// <summary>
    /// Renames the file to <c>config.json.invalid</c> - or, if one is already there, to a timestamped name: a file
    /// set aside earlier may be the only copy of the operator's configuration.
    /// </summary>
    public string SetAside()
    {
        string aside = ConfigPath + ".invalid";
        if (File.Exists(aside))
            aside += "-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        File.Move(ConfigPath, aside); // never replaces an existing file
        return aside;
    }
}
