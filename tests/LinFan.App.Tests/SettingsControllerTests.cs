// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert den <see cref="SettingsController"/>: er lädt die persistierten Werte, schreibt Änderungen zurück
/// und erhält dabei die separat gespeicherte Fenster-Geometrie (Load-modify-write - Regression gegen das
/// versehentliche Überschreiben der einen <c>ui.json</c> durch den jeweils anderen Schreibpfad).
/// </summary>
public sealed class SettingsControllerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"linfan-settingsctrl-{Guid.NewGuid():N}.json");

    [Fact]
    public void Loads_persisted_values_on_construction()
    {
        string path = TempPath();
        try
        {
            new UiSettingsStore(path).Save(new UiSettings { Theme = ThemeChoice.Dark, MinimizeToTray = true });

            var controller = new SettingsController(new UiSettingsStore(path));

            Assert.Equal(ThemeChoice.Dark, controller.Theme);
            Assert.True(controller.MinimizeToTray);
            Assert.Equal(ThemeChoice.Dark, controller.SelectedThemeOption.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Changing_theme_persists_and_keeps_window_geometry()
    {
        string path = TempPath();
        try
        {
            var store = new UiSettingsStore(path);
            store.Save(new UiSettings { Width = 1000, Height = 700, X = 10, Y = 20, Maximized = true });

            var controller = new SettingsController(new UiSettingsStore(path));
            controller.Theme = ThemeChoice.Light;
            controller.MinimizeToTray = true;

            UiSettings reloaded = store.Load();
            Assert.Equal(ThemeChoice.Light, reloaded.Theme);
            Assert.True(reloaded.MinimizeToTray);
            // Geometrie unangetastet:
            Assert.Equal(1000, reloaded.Width);
            Assert.Equal(700, reloaded.Height);
            Assert.Equal(10, reloaded.X);
            Assert.Equal(20, reloaded.Y);
            Assert.True(reloaded.Maximized);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SelectedThemeOption_setter_updates_theme()
    {
        var controller = new SettingsController(new UiSettingsStore(TempPath()))
        {
            SelectedThemeOption = ThemeOption.For(ThemeChoice.Dark),
        };

        Assert.Equal(ThemeChoice.Dark, controller.Theme);
    }

    // --- Event-Leak-Regression (2026-07-05 Review): das Localizer-Abo muss lösbar sein. --------------

    [Fact]
    public void Construction_subscribes_and_Dispose_unsubscribes_localizer()
    {
        int before = LocalizerProbe.SubscriberCount();

        var controller = new SettingsController(new UiSettingsStore(TempPath()));
        Assert.Equal(before + 1, LocalizerProbe.SubscriberCount());

        controller.Dispose();
        Assert.Equal(before, LocalizerProbe.SubscriberCount());
    }

    [Fact]
    public void Repeated_create_dispose_does_not_accumulate_localizer_handlers()
    {
        int before = LocalizerProbe.SubscriberCount();

        for (int i = 0; i < 5; i++)
            new SettingsController(new UiSettingsStore(TempPath())).Dispose();

        Assert.Equal(before, LocalizerProbe.SubscriberCount());
    }

    /// <summary>
    /// The theme labels live inside the option instances, not in the binding - without a rebuild the
    /// dropdown would stay in the language the controller was created in. The selection has to survive that
    /// rebuild (record value equality against the fresh list).
    /// </summary>
    [Fact]
    public void ThemeOptions_AreRebuilt_OnLanguageChange_KeepingTheSelection()
    {
        string path = TempPath();
        try
        {
            new UiSettingsStore(path).Save(new UiSettings { Theme = ThemeChoice.Dark });
            using var controller = new SettingsController(new UiSettingsStore(path));

            Localizer.Instance.SetLanguage(LanguageChoice.English);
            Assert.Contains(controller.ThemeOptions, o => o.Display == "Dark");
            Assert.Contains(controller.SelectedThemeOption, controller.ThemeOptions);

            Localizer.Instance.SetLanguage(LanguageChoice.German);
            Assert.Contains(controller.ThemeOptions, o => o.Display == "Dunkel");
            Assert.Contains(controller.SelectedThemeOption, controller.ThemeOptions);
            Assert.Equal(ThemeChoice.Dark, controller.SelectedThemeOption.Value);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // restore the pinned test culture
            File.Delete(path);
        }
    }

    [Fact]
    public void Navigation_HasOneAppearancePage_ForThemeAndLanguage_AndStartupAndBackground()
    {
        string path = TempPath();
        try
        {
            using var controller = new SettingsController(new UiSettingsStore(path));

            Assert.Equal(
                new[]
                {
                    SettingsSection.Sensors, SettingsSection.Fans, SettingsSection.Airflow,
                    SettingsSection.Appearance, SettingsSection.Background, SettingsSection.Backup,
                },
                controller.Sections.Select(s => s.Section));
            SettingsSectionItem appearance = controller.Sections.Single(s => s.Section == SettingsSection.Appearance);
            Assert.Equal("Darstellung", appearance.Label);
            Assert.True(appearance.IsFirstInGroup);
            Assert.Equal("Start & Hintergrund", controller.Sections.Single(s => s.Section == SettingsSection.Background).Label);

            // Backup and the setup assistant share one page in the application group - no third group.
            SettingsSectionItem backup = controller.Sections.Single(s => s.Section == SettingsSection.Backup);
            Assert.Equal("Sicherung & Einrichtung", backup.Label);
            Assert.Equal(appearance.Group, backup.Group);
            Assert.False(backup.IsFirstInGroup);
            Assert.Equal(2, controller.Sections.Count(s => s.IsFirstInGroup));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThemeTiles_CarryHint_AndWhichVariantsTheirPreviewShows()
    {
        IReadOnlyList<ThemeOption> options = ThemeOption.Build();

        Assert.Equal(new[] { ThemeChoice.System, ThemeChoice.Light, ThemeChoice.Dark, ThemeChoice.Terminal, ThemeChoice.HighContrast },
            options.Select(o => o.Value));
        Assert.Equal(new[]
            {
                "Folgt dem Betriebssystem", "Helle Flächen, dunkle Schrift", "Dunkle Flächen, helle Schrift",
                "Phosphor-Grün, Monospace, eckig", "Schwarz/Weiß, kräftige Ränder",
            },
            options.Select(o => o.Hint));
        Assert.Equal(new[] { true, true, false, false, false }, options.Select(o => o.ShowsLight));  // System shows both halves
        Assert.Equal(new[] { true, false, true, false, false }, options.Select(o => o.ShowsDark));
        Assert.Equal(new[] { false, false, false, true, false }, options.Select(o => o.ShowsTerminal));
        Assert.Equal(new[] { false, false, false, false, true }, options.Select(o => o.ShowsHighContrast));
    }

    [Fact]
    public void LanguageMenu_MarksTheCurrentLanguage_AndPickingOneSetsIt()
    {
        string path = TempPath();
        try
        {
            using var controller = new SettingsController(new UiSettingsStore(path));
            Assert.Equal(new[] { "System", "Deutsch", "English" }, controller.LanguageMenu.Select(m => m.Header));
            Assert.True(controller.LanguageMenu[0].IsCurrent);
            Assert.StartsWith("Betriebssystem · ", controller.LanguageMenu[0].Detail);

            controller.LanguageMenu[2].Command!.Execute(null);

            Assert.Equal(LanguageChoice.English, controller.Language);
            Assert.Equal(new[] { false, false, true }, controller.LanguageMenu.Select(m => m.IsCurrent));
            Assert.Equal(LanguageChoice.English, new UiSettingsStore(path).Load().Language);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(LanguageChoice.German, "System (Deutsch)")]
    [InlineData(LanguageChoice.English, "System (English)")]
    public void LanguageSystemOption_NamesTheLanguageItResolvesTo(LanguageChoice system, string label)
    {
        IReadOnlyList<LanguageOption> options = LanguageOption.Build(system);

        Assert.Equal(label, options[0].Display);
        Assert.Equal(new[] { "Deutsch", "English" }, options.Skip(1).Select(o => o.Display));
        Assert.Equal(LanguageChoice.System, options[0].Value);
    }
}
