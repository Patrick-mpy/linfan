// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Tests;

/// <summary>
/// The <see cref="BackupController"/>: export serializes config and prefs; import reads and checks the file before the
/// confirmation, replaces the config (ReplaceConfig) and reports success only once the daemon reports the new config back
/// - only then follow the prefs (never the update check). Broken, incompatible or untransferable imports fail cleanly,
/// without applying anything halfway.
/// </summary>
public sealed class BackupControllerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"linfan-backupctrl-{Guid.NewGuid():N}.json");

    private static AppConfig SampleConfig() => AppConfig.Empty with
    {
        Fans = new[]
        {
            new FanConfig { FanId = "f1", Name = "CPU", MinPwm = 40, MaxPwm = 220, Calibration = new FanCalibration { StartPwm = 96, MinRpm = 400, MaxRpm = 1800 } },
        },
        Sensors = new[] { new SensorConfig { SensorId = "s1", Name = "Package" } },
        OnboardingCompleted = true,
    };

    /// <summary>Another configuration than <see cref="SampleConfig"/> - an import of it changes what the daemon runs.</summary>
    private static AppConfig OtherConfig() => SampleConfig() with
    {
        Fans = new[] { new FanConfig { FanId = "f1", Name = "Front", MinPwm = 30, MaxPwm = 255 } },
        Curves = new[] { new CurveConfig { Id = "c1", Name = "Silent" } },
        Profiles = new[]
        {
            new Profile { Id = "p1", Name = "Quiet", Curves = new[] { new CurveConfig { Id = "c1", Name = "Silent" } } },
            new Profile
            {
                Id = "p2", Name = "Gaming",
                Curves = new[] { new CurveConfig { Id = "c2", Name = "Loud" }, new CurveConfig { Id = "c3", Name = "GPU" } },
            },
        },
    };

    private sealed class Harness
    {
        public AppConfig Config = SampleConfig();
        public readonly List<AppConfig> Replaced = new();
        public int ResetCalls;
        public int ArmCalls;
        public int CancelCalls;
        public bool ReplaceResult = true;
        public bool ResetResult = true;

        /// <summary>What the daemon reports back after an armed send: by default it takes the change.</summary>
        public Func<Task<bool>> ReportedBack = () => Task.FromResult(true);

        public readonly SettingsController Settings;
        public readonly BackupController Controller;

        public Harness(TimeSpan? statusAutoHide = null, Func<AppConfig, Task<bool>>? sendReplace = null,
            TimeProvider? clock = null)
        {
            Settings = new SettingsController(new UiSettingsStore(TempPath()));
            Controller = new BackupController(
                () => Config,
                sendReplace ?? (cfg => { Replaced.Add(cfg); return Task.FromResult(ReplaceResult); }),
                () => { ResetCalls++; return Task.FromResult(ResetResult); },
                Settings,
                () => { ArmCalls++; return ReportedBack(); },
                () => CancelCalls++,
                statusAutoHide: statusAutoHide,
                clock: clock);
        }
    }

    /// <summary>A backup of <paramref name="config"/> with Light/German/tray, as another installation would export it.</summary>
    private static string BackupOf(AppConfig config)
    {
        var source = new Harness { Config = config };
        source.Settings.Theme = ThemeChoice.Light;
        source.Settings.Language = LanguageChoice.German;
        source.Settings.MinimizeToTray = true;
        return source.Controller.BuildBackupJson();
    }

    private static async Task<BackupImportResult> ImportAsync(Harness h, string json) =>
        await h.Controller.ImportAsync(h.Controller.ReadImport(json) ?? throw new InvalidOperationException("not importable"));

    [Fact]
    public void BuildBackupJson_RoundTrips_ConfigAndPrefs()
    {
        var h = new Harness();
        h.Settings.Theme = ThemeChoice.Dark;
        h.Settings.Language = LanguageChoice.English;
        h.Settings.MinimizeToTray = true;

        string json = h.Controller.BuildBackupJson();
        LinFanBackup? backup = JsonSerializer.Deserialize<LinFanBackup>(json,
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        Assert.NotNull(backup);
        Assert.Equal(LinFanBackup.CurrentFormatVersion, backup!.FormatVersion);
        FanConfig fan = Assert.Single(backup.Config.Fans);
        Assert.Equal("CPU", fan.Name);
        Assert.Equal(96, fan.Calibration!.StartPwm); // the calibration is part of the backup
        Assert.Equal(ThemeChoice.Dark, backup.Ui.Theme);
        Assert.Equal(LanguageChoice.English, backup.Ui.Language);
        Assert.True(backup.Ui.MinimizeToTray);
        Assert.DoesNotContain("UpdateChecksEnabled", json); // not restored on import, so not written either
    }

    [Fact]
    public async Task The_appearance_travels_with_the_backup()
    {
        var source = new Harness { Config = OtherConfig() };
        source.Settings.Theme = ThemeChoice.Terminal;
        source.Settings.AccentCustom = "#7C3AED";
        source.Settings.Accent = AccentChoice.Custom;
        string json = source.Controller.BuildBackupJson();
        Assert.Contains("\"Theme\": \"Terminal\"", json);
        Assert.Contains("\"Accent\": \"Custom\"", json);

        var target = new Harness();
        BackupImportResult result = await ImportAsync(target, json);

        Assert.True(result.Success);
        Assert.Equal(ThemeChoice.Terminal, target.Settings.Theme);
        Assert.Equal(AccentChoice.Custom, target.Settings.Accent);
        Assert.Equal("#7C3AED", target.Settings.AccentCustom);
    }

    /// <summary>A backup from before accent colours (or without an own one) leaves the user's remembered own colour.</summary>
    [Fact]
    public async Task A_backup_without_an_own_colour_keeps_the_remembered_one()
    {
        var target = new Harness();
        target.Settings.AccentCustom = "#22C55E";
        target.Settings.Accent = AccentChoice.Custom;

        await ImportAsync(target, BackupOf(OtherConfig())); // Light, theme's own accent

        Assert.Equal(AccentChoice.Standard, target.Settings.Accent);
        Assert.Equal("#22C55E", target.Settings.AccentCustom);
    }

    /// <summary>A newer LinFan's theme or accent - or a broken own colour - costs the backup nothing but that setting.</summary>
    [Fact]
    public async Task Unknown_appearance_values_fall_back_without_failing_the_backup()
    {
        var target = new Harness();
        target.Settings.AccentCustom = "#22C55E";
        string json = BackupOf(OtherConfig())
            .Replace("\"Theme\": \"Light\"", "\"Theme\": \"Aurora\"", StringComparison.Ordinal)
            .Replace("\"Accent\": \"Standard\"", "\"Accent\": \"Teal\"", StringComparison.Ordinal)
            .Replace("\"AccentCustom\": null", "\"AccentCustom\": \"zz\"", StringComparison.Ordinal);
        Assert.Contains("Aurora", json);
        Assert.Contains("Teal", json);
        Assert.Contains("\"zz\"", json);

        BackupImportResult result = await ImportAsync(target, json);

        Assert.True(result.Success); // the fans and curves still came back
        Assert.Equal(ThemeChoice.System, target.Settings.Theme);
        Assert.Equal(AccentChoice.Standard, target.Settings.Accent);
        Assert.Equal("#22C55E", target.Settings.AccentCustom);
        Assert.Equal(LanguageChoice.German, target.Settings.Language);
    }

    // --- Reading: before the confirmation, an unusable file needs none ---------------------------------------------

    [Fact]
    public void ReadImport_Valid_SummarizesTheBackup_WithoutSendingAnything()
    {
        var h = new Harness();

        BackupImport? import = h.Controller.ReadImport(BackupOf(OtherConfig()));

        Assert.NotNull(import);
        Assert.Contains("Lüfter: 1 · Profile: 2 · Kurven: 3", import!.ConfirmMessage); // curves of every profile
        Assert.Contains("vollständig", import.ConfirmMessage);
        Assert.Empty(h.Replaced);
        Assert.Equal(0, h.ArmCalls);
        Assert.Equal("", h.Controller.Status.Text);
    }

    [Fact]
    public void ReadImport_WithoutProfiles_CountsTheRunningCurves()
    {
        var h = new Harness();

        BackupImport? import = h.Controller.ReadImport(BackupOf(SampleConfig() with
        {
            Curves = new[] { new CurveConfig { Id = "c1", Name = "A" }, new CurveConfig { Id = "c2", Name = "B" } },
        }));

        Assert.Contains("Lüfter: 1 · Profile: 0 · Kurven: 2", import!.ConfirmMessage);
    }

    [Fact]
    public void ReadImport_Malformed_FailsWithAnErrorThatStays()
    {
        var h = new Harness(statusAutoHide: TimeSpan.FromMilliseconds(20));

        Assert.Null(h.Controller.ReadImport("{ das ist kein json"));

        Assert.Equal("Die Datei ist kein gültiges LinFan-Backup.", h.Controller.Status.Text);
        Assert.True(h.Controller.Status.IsError);
        Assert.Empty(h.Replaced);
    }

    [Theory]
    [InlineData("{\"FormatVersion\":1,\"Config\":{\"Fans\":null},\"Ui\":{}}")]
    [InlineData("{\"FormatVersion\":1,\"Config\":{\"Profiles\":null},\"Ui\":{}}")]
    [InlineData("{\"FormatVersion\":1,\"Config\":null,\"Ui\":{}}")]
    [InlineData("{\"FormatVersion\":1,\"Config\":{},\"Ui\":null}")]
    public void ReadImport_WithAnExplicitNull_FailsWithoutThrowing(string json)
    {
        var h = new Harness();

        Assert.Null(h.Controller.ReadImport(json));

        Assert.True(h.Controller.Status.IsError);
    }

    [Fact]
    public void ReadImport_FutureFormatVersion_Fails()
    {
        var h = new Harness();
        string json = "{\"FormatVersion\":999,\"Config\":{\"SchemaVersion\":3},\"Ui\":{\"Theme\":\"System\",\"Language\":\"System\",\"MinimizeToTray\":false}}";

        Assert.Null(h.Controller.ReadImport(json));

        Assert.Equal("Nicht unterstützte Backup-Version 999.", h.Controller.Status.Text);
    }

    // --- Importing: success only once the daemon reports the new config back -----------------------------------------

    [Fact]
    public async Task Import_ReportedBack_ReplacesConfig_AndAppliesPrefs()
    {
        var target = new Harness();

        BackupImportResult result = await ImportAsync(target, BackupOf(OtherConfig()));

        Assert.True(result.Success);
        AppConfig replaced = Assert.Single(target.Replaced);
        Assert.Equal("Front", Assert.Single(replaced.Fans).Name);
        Assert.Equal(ThemeChoice.Light, target.Settings.Theme);
        Assert.Equal(LanguageChoice.German, target.Settings.Language);
        Assert.True(target.Settings.MinimizeToTray);
        Assert.Equal(1, target.ArmCalls);   // the editor rebuild is armed …
        Assert.Equal(0, target.CancelCalls); // … and not disarmed again
        Assert.Equal("Backup importiert.", result.Message);
    }

    [Fact]
    public async Task Import_ArmsTheResync_BeforeSending()
    {
        // Armed after the send, a snapshot with the new config could slip in first and become the baseline.
        int armedBeforeSend = -1;
        Harness? h = null;
        h = new Harness(sendReplace: _ =>
        {
            armedBeforeSend = h!.ArmCalls;
            return Task.FromResult(true);
        });

        await ImportAsync(h, BackupOf(OtherConfig()));

        Assert.Equal(1, armedBeforeSend);
    }

    [Fact]
    public async Task Import_WaitsForTheDaemon_BeforeReportingOrApplyingAnything()
    {
        var reportedBack = new TaskCompletionSource<bool>();
        var h = new Harness { ReportedBack = () => reportedBack.Task };

        Task<BackupImportResult> import = h.Controller.ImportAsync(h.Controller.ReadImport(BackupOf(OtherConfig()))!);

        Assert.False(import.IsCompleted);
        Assert.Equal("Backup wird übernommen … warte auf die Bestätigung des Dienstes.", h.Controller.Status.Text);
        Assert.False(h.Controller.Status.IsError);
        Assert.Equal(ThemeChoice.System, h.Settings.Theme); // nothing applied while the daemon has not answered

        reportedBack.SetResult(true);
        BackupImportResult result = await import;

        Assert.True(result.Success);
        Assert.Equal(ThemeChoice.Light, h.Settings.Theme);
        Assert.Equal("Backup importiert.", h.Controller.Status.Text);
    }

    [Fact]
    public async Task Import_NotReportedBack_FailsHonestly_AndKeepsThePrefs()
    {
        var h = new Harness { ReportedBack = () => Task.FromResult(false) }; // refused (limits) or the connection dropped

        BackupImportResult result = await ImportAsync(h, BackupOf(OtherConfig()));

        Assert.False(result.Success);
        Assert.Single(h.Replaced); // it went out …
        Assert.True(h.Controller.Status.IsError); // … but "imported" would be a claim nobody confirmed
        Assert.Contains("nicht bestätigt", h.Controller.Status.Text);
        Assert.Equal(ThemeChoice.System, h.Settings.Theme);
        Assert.False(h.Settings.MinimizeToTray);
    }

    [Fact]
    public async Task Import_OfTheRunningConfig_IsNoFailure_AndDoesNotWaitForAChange()
    {
        // The daemon will report no change - there is none. The verdict is clear at once, and nothing is armed: a change
        // meanwhile (a profile switch elsewhere) would rebuild the editor and read as "imported".
        var h = new Harness { ReportedBack = () => new TaskCompletionSource<bool>().Task };

        Task<BackupImportResult> import = h.Controller.ImportAsync(h.Controller.ReadImport(BackupOf(h.Config))!);

        Assert.True(import.IsCompleted);
        BackupImportResult result = await import;
        Assert.True(result.Success);
        Assert.Single(h.Replaced); // it still goes out - the daemon stays the authority
        Assert.Equal(0, h.ArmCalls);
        Assert.False(h.Controller.Status.IsError);
        Assert.Equal("Backup übernommen - es entsprach bereits der aktuellen Konfiguration.", result.Message);
        Assert.Equal(ThemeChoice.Light, h.Settings.Theme); // the prefs follow as with any successful import
    }

    [Fact]
    public async Task Import_WithoutAnyVerdict_GivesUpAfterTheTimeout()
    {
        // No verdict can come, e.g. while every snapshot fails to apply: "importing …" must not stay forever.
        var clock = new ManualClock();
        var h = new Harness(clock: clock) { ReportedBack = () => new TaskCompletionSource<bool>().Task };

        Task<BackupImportResult> import = h.Controller.ImportAsync(h.Controller.ReadImport(BackupOf(OtherConfig()))!);
        clock.Advance(BackupController.VerdictTimeout - TimeSpan.FromSeconds(1));
        Assert.False(import.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        BackupImportResult result = await import.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Success);
        Assert.Contains("nicht bestätigt", result.Message);
        Assert.False(h.Controller.IsBusy);
    }

    [Fact]
    public async Task Import_IsBusy_UntilItsVerdict()
    {
        var reportedBack = new TaskCompletionSource<bool>();
        var h = new Harness { ReportedBack = () => reportedBack.Task };
        Assert.False(h.Controller.IsBusy);

        Task<BackupImportResult> import = h.Controller.ImportAsync(h.Controller.ReadImport(BackupOf(OtherConfig()))!);
        Assert.True(h.Controller.IsBusy); // the view locks import and reset: a second arm would turn this verdict

        reportedBack.SetResult(true);
        await import;
        Assert.False(h.Controller.IsBusy);
    }

    [Fact]
    public async Task Import_ReportsSuccess_InTheLanguageTheBackupBrought()
    {
        // As in the app, where a changed language setting switches the localizer at once.
        var h = new Harness();
        h.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsController.Language))
                Localizer.Instance.SetLanguage(h.Settings.Language);
        };
        string json = BackupOf(OtherConfig()).Replace("\"Language\": \"German\"", "\"Language\": \"English\"");
        try
        {
            BackupImportResult result = await ImportAsync(h, json);

            Assert.Equal("Backup imported.", result.Message);
            Assert.Equal("Backup imported.", h.Controller.Status.Text);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // the assembly's pinned culture
        }
    }

    [Fact]
    public async Task Import_KeepsTheUsersUpdateCheckChoice()
    {
        // An older backup still carries the switch; restoring someone else's must not opt the user out of update notices.
        var h = new Harness();
        Assert.True(h.Settings.UpdateChecksEnabled);
        string json = BackupOf(OtherConfig())
            .Replace("\"MinimizeToTray\": true", "\"MinimizeToTray\": true, \"UpdateChecksEnabled\": false");
        Assert.Contains("UpdateChecksEnabled", json);

        BackupImportResult result = await ImportAsync(h, json);

        Assert.True(result.Success);
        Assert.True(h.Settings.UpdateChecksEnabled);
    }

    [Fact]
    public async Task Import_DaemonUnreachable_DoesNotApplyPrefs_AndDisarmsTheResync()
    {
        var target = new Harness { ReplaceResult = false };

        BackupImportResult result = await ImportAsync(target, BackupOf(OtherConfig()));

        Assert.False(result.Success);
        Assert.Equal(ThemeChoice.System, target.Settings.Theme); // the prefs stay when the replace fails
        Assert.Equal(1, target.CancelCalls);                     // nothing on its way - no waiting for a foreign change
        Assert.Equal("Dienst nicht erreichbar - Import fehlgeschlagen.", result.Message);
    }

    [Fact]
    public async Task Import_ThatCannotBeMapped_FailsWithoutThrowing_AndDisarmsTheResync()
    {
        // A null deep inside (here: a fan entry) passes the reading but makes the mapping to the IPC DTO throw.
        var h = new Harness(sendReplace: cfg => { _ = cfg.Fans.Select(f => f.Name).ToList(); return Task.FromResult(true); });
        string json = "{\"FormatVersion\":1,\"Config\":{\"Fans\":[null]},\"Ui\":{}}";

        BackupImportResult result = await ImportAsync(h, json);

        Assert.False(result.Success);
        Assert.Equal(1, h.CancelCalls);
        Assert.Equal("Die Datei ist kein gültiges LinFan-Backup.", result.Message);
    }

    /// <summary>
    /// Through the real monitor: a damaged backup fails its mapping before anything goes out and is reported as an unusable
    /// file - not as an unreachable daemon, which is connected.
    /// </summary>
    [Fact]
    public async Task Import_ThatTheMonitorCannotMap_IsAnUnusableFile_NotAnUnreachableDaemon()
    {
        var client = new RecordingIpcClient();
        await using IpcLiveMonitor monitor = await RecordingIpcClient.ConnectedMonitorAsync(client);
        var h = new Harness(sendReplace: monitor.SendReplaceConfigAsync);
        string json = "{\"FormatVersion\":1,\"Config\":{\"Fans\":[null]},\"Ui\":{}}";

        BackupImportResult result = await ImportAsync(h, json);

        Assert.False(result.Success);
        Assert.Equal("Die Datei ist kein gültiges LinFan-Backup.", result.Message);
        Assert.Equal(1, h.CancelCalls);
        Assert.Empty(client.Sent);
    }

    // --- Reset ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResetAsync_CallsSendReset_AndArmsResync()
    {
        var h = new Harness();

        bool ok = await h.Controller.ResetAsync();

        Assert.True(ok);
        Assert.Equal(1, h.ResetCalls);
        Assert.Equal(1, h.ArmCalls);
        Assert.Equal(0, h.CancelCalls);
    }

    [Fact]
    public async Task ResetAsync_IsBusy_WhileItGoesOut()
    {
        var sent = new TaskCompletionSource<bool>();
        var h = new Harness();
        var controller = new BackupController(() => h.Config, _ => Task.FromResult(true), () => sent.Task, h.Settings,
            () => Task.FromResult(true), () => { });

        Task<bool> reset = controller.ResetAsync();
        Assert.True(controller.IsBusy);

        sent.SetResult(true);
        Assert.True(await reset);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task ResetAsync_DaemonUnreachable_DisarmsTheResync()
    {
        var h = new Harness { ResetResult = false };

        bool ok = await h.Controller.ResetAsync();

        Assert.False(ok);
        Assert.Equal(1, h.CancelCalls);
    }

    // --- Status toast: successes auto-hide, errors stay until dismissed ------------------------

    [Fact]
    public async Task Import_Valid_SetsSuccessStatus_ThatAutoHides()
    {
        var target = new Harness(statusAutoHide: TimeSpan.FromMilliseconds(20));
        await ImportAsync(target, BackupOf(OtherConfig()));

        Assert.NotEqual("", target.Controller.Status.Text);
        Assert.False(target.Controller.Status.IsError);

        // Poll instead of a single fixed delay to keep the test robust on slow CI machines.
        for (int i = 0; i < 100 && target.Controller.Status.Text != ""; i++)
            await Task.Delay(20);
        Assert.Equal("", target.Controller.Status.Text);
    }

    [Fact]
    public async Task Import_Malformed_SetsErrorStatus_ThatStays_UntilDismissed()
    {
        var h = new Harness(statusAutoHide: TimeSpan.FromMilliseconds(20));

        h.Controller.ReadImport("{ das ist kein json");

        Assert.NotEqual("", h.Controller.Status.Text);
        Assert.True(h.Controller.Status.IsError);

        await Task.Delay(150); // well past the auto-hide window -> errors must not fade
        Assert.NotEqual("", h.Controller.Status.Text);

        h.Controller.Status.DismissCommand.Execute(null);
        Assert.Equal("", h.Controller.Status.Text);
        Assert.False(h.Controller.Status.IsError);
    }

    [Fact]
    public async Task ResetAsync_Failure_SetsErrorStatus()
    {
        var h = new Harness { ResetResult = false };

        await h.Controller.ResetAsync();

        Assert.NotEqual("", h.Controller.Status.Text);
        Assert.True(h.Controller.Status.IsError);
    }
}
