// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert die GUI-lokale Settings-Persistenz: ein Round-Trip erhält die Werte, und eine fehlende oder
/// kaputte Datei führt zu Defaults statt zu einem Crash (die Geometrie ist nicht kritisch).
/// </summary>
public sealed class UiSettingsStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"linfan-uitest-{Guid.NewGuid():N}.json");

    [Fact]
    public void Save_then_Load_round_trips_values()
    {
        string path = TempPath();
        try
        {
            var store = new UiSettingsStore(path);
            var settings = new UiSettings
            {
                Width = 1234,
                Height = 800,
                X = 40,
                Y = 60,
                Maximized = true,
                Theme = ThemeChoice.Light,
                MinimizeToTray = true,
            };

            store.Save(settings);
            UiSettings loaded = store.Load();

            Assert.Equal(settings, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var store = new UiSettingsStore(TempPath()); // existiert nicht
        UiSettings loaded = store.Load();

        Assert.Equal(new UiSettings(), loaded);
        Assert.Null(loaded.Width);
        Assert.False(loaded.Maximized);
        Assert.Equal(ThemeChoice.System, loaded.Theme); // Default: dem OS folgen
        Assert.False(loaded.MinimizeToTray);
    }

    [Fact]
    public void Load_corrupt_file_returns_defaults()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json ");
            UiSettings loaded = new UiSettingsStore(path).Load();

            Assert.Equal(new UiSettings(), loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
