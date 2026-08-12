// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert den <see cref="SettingsController"/>: er lädt die persistierten Werte, schreibt Änderungen zurück
/// und erhält dabei die separat gespeicherte Fenster-Geometrie (Load-modify-write — Regression gegen das
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
    /// The theme labels live inside the option instances, not in the binding — without a rebuild the
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
}
