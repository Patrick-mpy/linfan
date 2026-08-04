// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert den <see cref="BackupController"/>: Export serialisiert Config + Prefs, Import ersetzt die Config
/// (ReplaceConfig) und übernimmt die Prefs, und kaputte/inkompatible/nicht-übertragbare Importe scheitern
/// sauber (keine Teilanwendung).
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

    private sealed class Harness
    {
        public AppConfig Config = SampleConfig();
        public readonly List<AppConfig> Replaced = new();
        public int ResetCalls;
        public int ResyncCalls;
        public bool ReplaceResult = true;
        public bool ResetResult = true;
        public readonly SettingsController Settings;
        public readonly BackupController Controller;

        public Harness(TimeSpan? statusAutoHide = null)
        {
            Settings = new SettingsController(new UiSettingsStore(TempPath()));
            Controller = new BackupController(
                () => Config,
                cfg => { Replaced.Add(cfg); return Task.FromResult(ReplaceResult); },
                () => { ResetCalls++; return Task.FromResult(ResetResult); },
                Settings,
                onConfigReplaced: () => ResyncCalls++,
                statusAutoHide: statusAutoHide);
        }
    }

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
        Assert.Equal(96, fan.Calibration!.StartPwm); // Kalibrierung ist im Backup enthalten
        Assert.Equal(ThemeChoice.Dark, backup.Ui.Theme);
        Assert.Equal(LanguageChoice.English, backup.Ui.Language);
        Assert.True(backup.Ui.MinimizeToTray);
    }

    [Fact]
    public async Task Import_Valid_ReplacesConfig_AndAppliesPrefs()
    {
        // Ein Quell-Backup erzeugen …
        var source = new Harness();
        source.Settings.Theme = ThemeChoice.Light;
        source.Settings.Language = LanguageChoice.German;
        source.Settings.MinimizeToTray = true;
        string json = source.Controller.BuildBackupJson();

        // … und in eine frische Instanz importieren.
        var target = new Harness();
        BackupImportResult result = await target.Controller.ImportFromJsonAsync(json);

        Assert.True(result.Success);
        AppConfig replaced = Assert.Single(target.Replaced);
        Assert.Equal("CPU", Assert.Single(replaced.Fans).Name);
        Assert.Equal(ThemeChoice.Light, target.Settings.Theme);
        Assert.Equal(LanguageChoice.German, target.Settings.Language);
        Assert.True(target.Settings.MinimizeToTray);
        Assert.Equal(1, target.ResyncCalls); // Editor-Neuaufbau angestoßen
    }

    [Fact]
    public async Task Import_Malformed_Fails_WithoutReplace()
    {
        var h = new Harness();

        BackupImportResult result = await h.Controller.ImportFromJsonAsync("{ das ist kein json");

        Assert.False(result.Success);
        Assert.Empty(h.Replaced);
    }

    [Fact]
    public async Task Import_ConfigWithNullList_Fails_WithoutThrowing()
    {
        // Ein handeditiertes Backup mit null-Liste ließ früher ToIpcConfigs .Select() werfen (Crash der GUI).
        // Der Import muss stattdessen sauber Fehlschlag melden und darf NIE nach außen werfen.
        var settings = new SettingsController(new UiSettingsStore(TempPath()));
        var controller = new BackupController(
            SampleConfig,
            // Mimt die Produktion: die Serialisierung zum IPC-DTO greift auf die Listen zu und wirft bei null.
            cfg => { _ = cfg.Fans.Select(f => f.Name).ToList(); return Task.FromResult(true); },
            () => Task.FromResult(true),
            settings,
            onConfigReplaced: () => { });

        // Gültiges Format, aber "fans": null (übrige Listen fehlen → Default []).
        string json = "{\"FormatVersion\":1,\"Config\":{\"Fans\":null},\"Ui\":{}}";

        BackupImportResult result = await controller.ImportFromJsonAsync(json);

        Assert.False(result.Success); // sauberer Fehlschlag statt Absturz
    }

    [Fact]
    public async Task Import_FutureFormatVersion_Fails()
    {
        var h = new Harness();
        string json = "{\"FormatVersion\":999,\"Config\":{\"SchemaVersion\":3},\"Ui\":{\"Theme\":\"System\",\"Language\":\"System\",\"MinimizeToTray\":false}}";

        BackupImportResult result = await h.Controller.ImportFromJsonAsync(json);

        Assert.False(result.Success);
        Assert.Empty(h.Replaced);
    }

    [Fact]
    public async Task Import_DaemonUnreachable_DoesNotApplyPrefs()
    {
        var source = new Harness();
        source.Settings.Theme = ThemeChoice.Light;
        string json = source.Controller.BuildBackupJson();

        var target = new Harness { ReplaceResult = false };
        ThemeChoice before = target.Settings.Theme;

        BackupImportResult result = await target.Controller.ImportFromJsonAsync(json);

        Assert.False(result.Success);
        Assert.Equal(before, target.Settings.Theme); // Prefs bleiben unangetastet, wenn das Ersetzen scheitert
        Assert.Equal(0, target.ResyncCalls);          // und kein Editor-Neuaufbau
    }

    [Fact]
    public async Task ResetAsync_CallsSendReset_AndArmsResync()
    {
        var h = new Harness();

        bool ok = await h.Controller.ResetAsync();

        Assert.True(ok);
        Assert.Equal(1, h.ResetCalls);
        Assert.Equal(1, h.ResyncCalls);
    }

    [Fact]
    public async Task ResetAsync_DaemonUnreachable_DoesNotArmResync()
    {
        var h = new Harness { ResetResult = false };

        bool ok = await h.Controller.ResetAsync();

        Assert.False(ok);
        Assert.Equal(0, h.ResyncCalls);
    }

    // --- Status toast: successes auto-hide, errors stay until dismissed ------------------------

    [Fact]
    public async Task Import_Valid_SetsSuccessStatus_ThatAutoHides()
    {
        var source = new Harness();
        string json = source.Controller.BuildBackupJson();

        var target = new Harness(statusAutoHide: TimeSpan.FromMilliseconds(20));
        await target.Controller.ImportFromJsonAsync(json);

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

        await h.Controller.ImportFromJsonAsync("{ das ist kein json");

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
