// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace LinFan.App.Services;

/// <summary>
/// Lädt/speichert die GUI-lokalen <see cref="UiSettings"/> als JSON. Pfad ist <b>single-path und per-User</b>
/// (kein OS-Branch): <c>~/.config/linfan/ui.json</c> (Linux/macOS) bzw. <c>%AppData%\linfan\ui.json</c>
/// (Windows). Bewusst getrennt vom Daemon-Config (der liegt auf Windows maschinenweit unter
/// <c>%ProgramData%</c>, damit der SYSTEM-Dienst dieselbe Datei nutzt - für reine UI-Prefs falsch).
/// Lesen und Schreiben sind defensiv: fehlt/zerbricht die Datei → Defaults; ein Schreibfehler darf den
/// App-Shutdown nicht stören (best-effort).
/// </summary>
public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Vollständiger Pfad der Settings-Datei.</summary>
    public string FilePath { get; }

    public UiSettingsStore(string? filePath = null) => FilePath = filePath ?? DefaultPath();

    /// <summary>Per-User-Pfad, ohne OS-Branch - <c>ApplicationData</c> ist <c>~/.config</c> bzw. <c>%AppData%</c>.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "linfan", "ui.json");

    /// <summary>Lädt die Einstellungen; bei fehlender oder unlesbarer Datei kommen die Defaults zurück.</summary>
    public UiSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings()
                : new UiSettings();
        }
        catch
        {
            return new UiSettings(); // korrupte/unlesbare Datei → Defaults statt Crash
        }
    }

    /// <summary>Speichert die Einstellungen (best-effort; ein Schreibfehler wird verschluckt).</summary>
    public void Save(UiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // UI-Geometrie ist nicht kritisch - ein Schreibfehler (Rechte, Platte voll) darf das Beenden nicht stören.
        }
    }
}
