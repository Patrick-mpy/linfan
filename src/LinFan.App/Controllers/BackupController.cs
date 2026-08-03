// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// MVC-Controller für Sicherung/Wiederherstellung/Reset im Einstellungen-Tab. Bewusst <b>UI-frei</b>
/// (keine Datei-Dialoge — die macht das View-Code-Behind) und über Delegates verdrahtet, damit die
/// Logik (Serialisieren, Validieren, Anwenden) ohne Daemon/Socket unit-testbar bleibt. Import/Reset
/// gehen über den IPC-Client (<see cref="ICommandSink"/>): Import <b>ersetzt</b> die Config vollständig
/// (nicht Merge), Reset setzt auf Werkszustand. Die GUI-Prefs (Theme/Sprache/Tray) werden auf den
/// <see cref="SettingsController"/> angewandt (der persistiert sie selbst).
/// </summary>
public sealed partial class BackupController : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<AppConfig> _getConfig;
    private readonly Func<AppConfig, Task<bool>> _sendReplace;
    private readonly Func<Task<bool>> _sendReset;
    private readonly SettingsController _settings;
    private readonly Action _onConfigReplaced;

    public BackupController(
        Func<AppConfig> getConfig,
        Func<AppConfig, Task<bool>> sendReplace,
        Func<Task<bool>> sendReset,
        SettingsController settings,
        Action onConfigReplaced)
    {
        _getConfig = getConfig;
        _sendReplace = sendReplace;
        _sendReset = sendReset;
        _settings = settings;
        // Wird nach erfolgreichem Reset/Import aufgerufen, damit der Editor sich aus der neuen Config neu aufbaut.
        _onConfigReplaced = onConfigReplaced;
    }

    /// <summary>Zuletzt erzeugte Status-/Ergebnismeldung (an ein TextBlock im Sicherung-Tab gebunden).</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>Standard-Dateiname für den Speichern-Dialog des Exports.</summary>
    public static string DefaultFileName => "linfan-backup.json";

    /// <summary>Serialisiert die aktuelle Daemon-Config + GUI-Prefs als Backup-JSON (das Schreiben macht die View).</summary>
    public string BuildBackupJson()
    {
        var backup = new LinFanBackup
        {
            Config = _getConfig(),
            Ui = new BackupUiPrefs
            {
                Theme = _settings.Theme,
                Language = _settings.Language,
                MinimizeToTray = _settings.MinimizeToTray,
                UpdateChecksEnabled = _settings.UpdateChecksEnabled,
            },
        };
        return JsonSerializer.Serialize(backup, Json);
    }

    /// <summary>Setzt die Export-Erfolgsmeldung (von der View nach erfolgreichem Schreiben aufgerufen).</summary>
    public void ReportExported() => Status = Localizer.Instance["Settings.BackupExported"];

    /// <summary>
    /// Liest ein Backup-JSON, validiert Format/Version und wendet es an: Config vollständig ersetzen
    /// (ReplaceConfig) und — nur bei Erfolg — die GUI-Prefs übernehmen. Wirft nie; liefert Erfolg + Meldung.
    /// </summary>
    public async Task<BackupImportResult> ImportFromJsonAsync(string json)
    {
        LinFanBackup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<LinFanBackup>(json, Json);
        }
        catch (JsonException)
        {
            return Fail(Localizer.Instance["Settings.ImportFailedParse"]);
        }

        if (backup is null || backup.Config is null)
            return Fail(Localizer.Instance["Settings.ImportFailedParse"]);

        if (backup.FormatVersion > LinFanBackup.CurrentFormatVersion)
            return Fail(Localizer.Instance.Format("Settings.ImportFailedVersion", backup.FormatVersion));

        bool sent;
        try
        {
            sent = await _sendReplace(backup.Config);
        }
        catch (Exception)
        {
            // Ein handeditiertes/beschädigtes Backup (z. B. null-Listen an beliebiger Stelle) lässt die
            // Serialisierung zum IPC-DTO (ToIpcConfig) auf einem .Select werfen. Der Import wirft laut
            // Vertrag NIE nach außen → als „nicht lesbar/beschädigt" melden, statt die GUI abzureißen.
            return Fail(Localizer.Instance["Settings.ImportFailedParse"]);
        }
        if (!sent)
            return Fail(Localizer.Instance["Settings.ImportFailedDaemon"]);

        // Prefs erst nach erfolgreichem Config-Ersetzen anwenden (der SettingsController persistiert selbst).
        _settings.Theme = backup.Ui.Theme;
        _settings.Language = backup.Ui.Language;
        _settings.MinimizeToTray = backup.Ui.MinimizeToTray;
        _settings.UpdateChecksEnabled = backup.Ui.UpdateChecksEnabled;

        _onConfigReplaced(); // Editor-Neuaufbau anstoßen, sobald die neue Config zurückgespiegelt wird
        Status = Localizer.Instance["Settings.BackupImported"];
        return new BackupImportResult(true, Status);
    }

    /// <summary>Setzt die Daemon-Config auf Werkszustand zurück (die View bestätigt vorher modal).</summary>
    public async Task<bool> ResetAsync()
    {
        bool ok = await _sendReset();
        if (ok)
            _onConfigReplaced(); // Editor-Neuaufbau anstoßen, sobald der Werkszustand zurückgespiegelt wird
        Status = ok
            ? Localizer.Instance["Settings.ConfigReset"]
            : Localizer.Instance["Settings.ResetFailedDaemon"];
        return ok;
    }

    private BackupImportResult Fail(string message)
    {
        Status = message;
        return new BackupImportResult(false, message);
    }
}

/// <summary>Ergebnis eines Import-Versuchs: Erfolg + eine (bereits lokalisierte) Meldung für die View.</summary>
public sealed record BackupImportResult(bool Success, string Message);
