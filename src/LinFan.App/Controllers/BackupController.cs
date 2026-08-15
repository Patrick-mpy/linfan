// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// MVC-Controller für Sicherung/Wiederherstellung/Reset im Einstellungen-Tab. Bewusst <b>UI-frei</b>
/// (keine Datei-Dialoge - die macht das View-Code-Behind) und über Delegates verdrahtet, damit die
/// Logik (Serialisieren, Validieren, Anwenden) ohne Daemon/Socket unit-testbar bleibt. Import/Reset
/// gehen über den IPC-Client (<see cref="ICommandSink"/>): Import <b>ersetzt</b> die Config vollständig
/// (nicht Merge), Reset setzt auf Werkszustand. Die GUI-Prefs (Theme/Sprache/Tray) werden auf den
/// <see cref="SettingsController"/> angewandt (der persistiert sie selbst).
/// </summary>
public sealed class BackupController
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

    /// <param name="statusAutoHide">How long an auto-hiding status toast stays visible (default 4 s; injectable for tests).</param>
    public BackupController(
        Func<AppConfig> getConfig,
        Func<AppConfig, Task<bool>> sendReplace,
        Func<Task<bool>> sendReset,
        SettingsController settings,
        Action onConfigReplaced,
        TimeSpan? statusAutoHide = null)
    {
        _getConfig = getConfig;
        _sendReplace = sendReplace;
        _sendReset = sendReset;
        _settings = settings;
        // Wird nach erfolgreichem Reset/Import aufgerufen, damit der Editor sich aus der neuen Config neu aufbaut.
        _onConfigReplaced = onConfigReplaced;
        Status = new TransientStatus(statusAutoHide);
    }

    /// <summary>Zuletzt erzeugte Status-/Ergebnismeldung (an den transienten Status-Toast gebunden).</summary>
    public TransientStatus Status { get; }

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
    public void ReportExported() => SetStatus(Localizer.Instance["Settings.BackupExported"], autoHide: true);

    /// <summary>Reports a failed export (called by the view's catch block; keeps severity handling here).</summary>
    public void ReportExportFailed() => SetStatus(Localizer.Instance["Settings.ExportFailed"], isError: true);

    /// <summary>Reports an unreadable import file (called by the view's catch block).</summary>
    public void ReportImportReadFailed() => SetStatus(Localizer.Instance["Settings.ImportFailedParse"], isError: true);

    /// <summary>
    /// Liest ein Backup-JSON, validiert Format/Version und wendet es an: Config vollständig ersetzen
    /// (ReplaceConfig) und - nur bei Erfolg - die GUI-Prefs übernehmen. Wirft nie; liefert Erfolg + Meldung.
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
        string message = Localizer.Instance["Settings.BackupImported"];
        SetStatus(message, autoHide: true);
        return new BackupImportResult(true, message);
    }

    /// <summary>Setzt die Daemon-Config auf Werkszustand zurück (die View bestätigt vorher modal).</summary>
    public async Task<bool> ResetAsync()
    {
        bool ok = await _sendReset();
        if (ok)
        {
            _onConfigReplaced(); // Editor-Neuaufbau anstoßen, sobald der Werkszustand zurückgespiegelt wird
            SetStatus(Localizer.Instance["Settings.ConfigReset"], autoHide: true);
        }
        else
        {
            SetStatus(Localizer.Instance["Settings.ResetFailedDaemon"], isError: true);
        }
        return ok;
    }

    private BackupImportResult Fail(string message)
    {
        SetStatus(message, isError: true);
        return new BackupImportResult(false, message);
    }

    private void SetStatus(string text, bool autoHide = false, bool isError = false) =>
        Status.Set(text, autoHide, isError);
}

/// <summary>Ergebnis eines Import-Versuchs: Erfolg + eine (bereits lokalisierte) Meldung für die View.</summary>
public sealed record BackupImportResult(bool Success, string Message);
