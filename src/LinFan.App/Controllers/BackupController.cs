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
/// (keine Datei-Dialoge - die macht das View-Code-Behind) und über Delegates verdrahtet, damit die
/// Logik (Serialisieren, Validieren, Anwenden) ohne Daemon/Socket unit-testbar bleibt. Import/Reset
/// gehen über den IPC-Client (<see cref="ICommandSink"/>): Import <b>ersetzt</b> die Config vollständig
/// (nicht Merge), Reset setzt auf Werkszustand. Die GUI-Prefs (Theme/Akzent/Sprache/Tray) werden auf den
/// <see cref="SettingsController"/> angewandt (der persistiert sie selbst).
/// </summary>
public sealed partial class BackupController : ObservableObject
{
    /// <summary>
    /// How long an import waits for the daemon's verdict at most - past the resync's own grace (ten ticks of at most
    /// 5 s), so it only matters when no verdict can come at all, e.g. while every snapshot fails to apply.
    /// </summary>
    public static readonly TimeSpan VerdictTimeout = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<AppConfig> _getConfig;
    private readonly Func<AppConfig, Task<bool>> _sendReplace;
    private readonly Func<Task<bool>> _sendReset;
    private readonly SettingsController _settings;
    private readonly Func<Task<bool>> _armConfigResync;
    private readonly Action _cancelConfigResync;
    private readonly TimeProvider _clock;

    /// <param name="armConfigResync">
    /// Arms the editor's rebuild for the config the daemon reports next - called before a replace or reset goes out. Its
    /// task completes with <c>true</c> once the daemon reports a changed config, <c>false</c> when none came.
    /// </param>
    /// <param name="cancelConfigResync">Disarms it again when nothing went out.</param>
    /// <param name="statusAutoHide">How long an auto-hiding status toast stays visible (default 4 s; injectable for tests).</param>
    /// <param name="clock">Time source of <see cref="VerdictTimeout"/> and the status toast (tests advance it by hand).</param>
    public BackupController(
        Func<AppConfig> getConfig,
        Func<AppConfig, Task<bool>> sendReplace,
        Func<Task<bool>> sendReset,
        SettingsController settings,
        Func<Task<bool>> armConfigResync,
        Action cancelConfigResync,
        TimeSpan? statusAutoHide = null,
        TimeProvider? clock = null)
    {
        _getConfig = getConfig;
        _sendReplace = sendReplace;
        _sendReset = sendReset;
        _settings = settings;
        _armConfigResync = armConfigResync;
        _cancelConfigResync = cancelConfigResync;
        _clock = clock ?? TimeProvider.System;
        Status = new TransientStatus(statusAutoHide, _clock);
    }

    /// <summary>Zuletzt erzeugte Status-/Ergebnismeldung (an den transienten Status-Toast gebunden).</summary>
    public TransientStatus Status { get; }

    /// <summary>
    /// An import or reset is on its way - the view locks both: a second one would supersede the first one's resync, and
    /// its verdict would turn into "not confirmed".
    /// </summary>
    [ObservableProperty] private bool _isBusy;

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
                Accent = _settings.Accent,
                AccentCustom = _settings.AccentCustom,
                Language = _settings.Language,
                MinimizeToTray = _settings.MinimizeToTray,
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
    /// Reads and checks a backup file's content - before the view asks for confirmation: a file that cannot be imported
    /// needs none. <c>null</c> (with the error toast) when it is no backup this app can import. Never throws.
    /// </summary>
    public BackupImport? ReadImport(string json)
    {
        LinFanBackup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<LinFanBackup>(json, Json);
        }
        catch (JsonException)
        {
            return FailRead(Localizer.Instance["Settings.ImportFailedParse"]);
        }

        // A hand-edited file may carry an explicit null anywhere; the lists counted below must exist. Deeper nulls
        // surface when the config is mapped for the daemon (see ImportAsync).
        if (backup?.Config is not { } config || backup.Ui is null
            || config.Fans is null || config.Curves is null || config.Profiles is null || config.Sensors is null)
            return FailRead(Localizer.Instance["Settings.ImportFailedParse"]);

        if (backup.FormatVersion > LinFanBackup.CurrentFormatVersion)
            return FailRead(Localizer.Instance.Format("Settings.ImportFailedVersion", backup.FormatVersion));

        // A profile carries its own curves; a backup from before profiles only has the running set.
        int curves = config.Profiles.Count > 0
            ? config.Profiles.Sum(p => p?.Curves?.Count ?? 0)
            : config.Curves.Count;
        return new BackupImport(backup,
            Localizer.Instance.Format("Settings.ImportConfirmMessage", config.Fans.Count, config.Profiles.Count, curves));
    }

    /// <summary>
    /// Replaces the daemon config with the backup's (ReplaceConfig) once the user confirmed. Success is reported only
    /// when the daemon reports the new config back - a sent command only says the bytes left; the daemon may refuse the
    /// config (its limits) or the connection drop before it applies it. A backup equal to the running config has nothing
    /// to report back and is no failure. Only then follow theme, language and tray; whether to look for updates stays
    /// the user's own choice - a foreign backup must not opt them out. Never throws.
    /// </summary>
    public async Task<BackupImportResult> ImportAsync(BackupImport import)
    {
        IsBusy = true;
        try
        {
            return await ReplaceAsync(import);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<BackupImportResult> ReplaceAsync(BackupImport import)
    {
        AppConfig config = import.Backup.Config;
        // Equal to the running config, the verdict is clear without waiting: nothing will change. Not armed then - a
        // change in the meantime (a profile switch elsewhere) would rebuild the editor and read as "imported".
        bool alreadyCurrent = MainController.ConfigSignature(config) == MainController.ConfigSignature(_getConfig());

        SetStatus(Localizer.Instance["Settings.BackupImporting"]);
        Task<bool>? reportedBack = alreadyCurrent ? null : _armConfigResync();
        bool sent;
        try
        {
            sent = await _sendReplace(config);
        }
        catch (Exception)
        {
            // A damaged backup (a null list deep inside) makes the mapping to the IPC DTO (ToIpcConfig) throw. The import
            // never throws - report the file as unusable instead of tearing down the GUI.
            if (reportedBack is not null)
                _cancelConfigResync();
            return Fail(Localizer.Instance["Settings.ImportFailedParse"]);
        }
        if (!sent)
        {
            if (reportedBack is not null)
                _cancelConfigResync();
            return Fail(Localizer.Instance["Settings.ImportFailedDaemon"]);
        }

        if (reportedBack is not null && !await WithinVerdictTimeoutAsync(reportedBack))
            return Fail(Localizer.Instance["Settings.ImportNotConfirmed"]);

        BackupUiPrefs ui = import.Backup.Ui;
        _settings.Theme = ui.Theme;
        // The own colour first - picking "custom" without one would start from the colour on screen. A backup without a
        // usable one (older, never set, or no colour) leaves the remembered own colour alone.
        if (ColorContrast.TryParseHex(ui.AccentCustom, out _))
            _settings.AccentCustom = ui.AccentCustom;
        _settings.Accent = ui.Accent;
        _settings.Language = ui.Language;
        _settings.MinimizeToTray = ui.MinimizeToTray;

        // After the language: the message comes in the one the backup brought along.
        string message = Localizer.Instance[alreadyCurrent ? "Settings.BackupAlreadyCurrent" : "Settings.BackupImported"];
        SetStatus(message, autoHide: true);
        return new BackupImportResult(true, message);
    }

    /// <summary>The daemon's verdict, or <c>false</c> once <see cref="VerdictTimeout"/> passed without one.</summary>
    private async Task<bool> WithinVerdictTimeoutAsync(Task<bool> verdict)
    {
        using var cts = new CancellationTokenSource();
        Task timeout = Task.Delay(VerdictTimeout, _clock, cts.Token);
        Task first = await Task.WhenAny(verdict, timeout);
        cts.Cancel();
        return first == verdict && await verdict;
    }

    /// <summary>Setzt die Daemon-Config auf Werkszustand zurück (die View bestätigt vorher modal).</summary>
    public async Task<bool> ResetAsync()
    {
        IsBusy = true;
        try
        {
            _ = _armConfigResync(); // the editor rebuilds once the daemon reports the factory state
            bool ok = await _sendReset();
            if (ok)
            {
                SetStatus(Localizer.Instance["Settings.ConfigReset"], autoHide: true);
            }
            else
            {
                _cancelConfigResync();
                SetStatus(Localizer.Instance["Settings.ResetFailedDaemon"], isError: true);
            }
            return ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private BackupImport? FailRead(string message)
    {
        SetStatus(message, isError: true);
        return null;
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
