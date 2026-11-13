// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// The appearance part of <see cref="SettingsController"/>: theme and accent are persisted, unknown values fall back,
/// the colour dots show what each choice becomes in the theme on screen, and the hex field takes six digits at once and
/// keeps anything else as typed, marked invalid.
/// </summary>
public sealed class SettingsControllerAppearanceTests : IDisposable
{
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        foreach (string path in _paths)
            File.Delete(path);
    }

    private string TempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"linfan-appearance-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return path;
    }

    private SettingsController NewController(string? path = null) =>
        new(new UiSettingsStore(path ?? TempPath()));

    private static string Hex(Color c) => ColorContrast.ToHex(c);

    [Fact]
    public void Accent_and_own_colour_persist_and_keep_the_window_geometry()
    {
        string path = TempPath();
        try
        {
            var store = new UiSettingsStore(path);
            store.Save(new UiSettings { Width = 1000, Maximized = true });

            using (var controller = NewController(path))
            {
                controller.Theme = ThemeChoice.Terminal;
                controller.AccentCustom = "#7c3aed";
                controller.Accent = AccentChoice.Custom;
            }

            UiSettings saved = store.Load();
            Assert.Equal(ThemeChoice.Terminal, saved.Theme);
            Assert.Equal(AccentChoice.Custom, saved.Accent);
            Assert.Equal("#7C3AED", saved.AccentCustom); // stored normalised
            Assert.Equal(1000, saved.Width);
            Assert.True(saved.Maximized);

            using var reloaded = NewController(path);
            Assert.Equal(ThemeChoice.Terminal, reloaded.Theme);
            Assert.Equal(AccentChoice.Custom, reloaded.Accent);
            Assert.Equal("#7C3AED", reloaded.AccentCustom);
            Assert.Equal("#7C3AED", reloaded.AccentHexText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Settings of a newer GUI (or edited by hand) may carry values this build does not know.</summary>
    [Fact]
    public void Unknown_persisted_values_fall_back_to_the_defaults()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, """{ "Theme": 9, "Accent": 42, "AccentCustom": "nope" }""");

            using var controller = NewController(path);

            Assert.Equal(ThemeChoice.System, controller.Theme);
            Assert.Equal(AccentChoice.Standard, controller.Accent);
            Assert.Null(controller.AccentCustom);
            Assert.Equal(ThemeChoice.System, controller.SelectedThemeOption.Value);
            Assert.Equal(AccentChoice.Standard, controller.SelectedAccentSwatch!.Choice);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_fixed_theme_is_the_theme_on_screen_system_leaves_that_to_the_app()
    {
        using var controller = NewController();

        controller.Theme = ThemeChoice.HighContrast;
        Assert.Equal(ThemeChoice.HighContrast, controller.ActiveTheme);

        controller.ActiveTheme = ThemeChoice.Light; // what the app reports for "System" on a light desktop
        controller.Theme = ThemeChoice.System;
        Assert.Equal(ThemeChoice.Light, controller.ActiveTheme);
    }

    [Fact]
    public void The_dots_are_the_themes_own_the_presets_and_the_own_colour_as_they_look_on_screen()
    {
        using var controller = NewController();
        controller.Theme = ThemeChoice.Light;

        Assert.Equal(
            new[]
            {
                AccentChoice.Standard, AccentChoice.Indigo, AccentChoice.Violet, AccentChoice.Pink, AccentChoice.Orange,
                AccentChoice.Emerald, AccentChoice.Gray, AccentChoice.Custom,
            },
            controller.AccentSwatches.Select(s => s.Choice));
        Assert.Equal(new[] { "Standard", "Indigo", "Violett", "Pink", "Orange", "Smaragd", "Grau", "Eigene" },
            controller.AccentSwatches.Select(s => s.Name));

        AccentSwatch pink = controller.AccentSwatches.Single(s => s.Choice == AccentChoice.Pink);
        Assert.Equal("#BE185D", Hex(pink.Color)); // pink 700 on light surfaces
        Assert.Equal("Pink - im Theme „Hell“ #BE185D", pink.Tooltip);
        Assert.Equal("#0284C7", Hex(controller.AccentSwatches[0].Color)); // the light theme's own
    }

    /// <summary>
    /// The dots stay the same objects when the theme on screen changes - a new list would make the list box lose its
    /// selection while it changes it, and picking a dot would fall back to the first.
    /// </summary>
    [Fact]
    public void A_theme_change_recolours_the_dots_in_place()
    {
        using var controller = NewController();
        controller.Theme = ThemeChoice.Light;
        controller.Accent = AccentChoice.Pink;
        AccentSwatch[] before = controller.AccentSwatches.ToArray();
        AccentSwatch? selected = controller.SelectedAccentSwatch;

        controller.Theme = ThemeChoice.Dark;

        Assert.Equal(before, controller.AccentSwatches);
        Assert.Same(selected, controller.SelectedAccentSwatch);
        Assert.Equal("#F472B6", Hex(controller.SelectedAccentSwatch!.Color)); // pink 400 on dark surfaces
    }

    [Fact]
    public void Picking_a_dot_sets_the_accent()
    {
        using var controller = NewController();

        controller.SelectedAccentSwatch = controller.AccentSwatches.Single(s => s.Choice == AccentChoice.Emerald);

        Assert.Equal(AccentChoice.Emerald, controller.Accent);
        Assert.False(controller.IsCustomAccent);
        controller.SelectedAccentSwatch = null; // a list box clearing its selection changes nothing
        Assert.Equal(AccentChoice.Emerald, controller.Accent);
    }

    [Fact]
    public void Choosing_an_own_colour_before_there_is_one_starts_from_the_colour_on_screen()
    {
        using var controller = NewController();
        controller.Theme = ThemeChoice.Dark;
        controller.Accent = AccentChoice.Pink;

        controller.Accent = AccentChoice.Custom;

        Assert.True(controller.IsCustomAccent);
        Assert.Equal("#F472B6", controller.AccentCustom);
        Assert.Equal("#F472B6", controller.AccentHexText);
        Assert.False(controller.AccentHexInvalid);
    }

    [Fact]
    public void Six_hex_digits_take_effect_at_once_and_stay_as_typed()
    {
        using var controller = NewController();
        controller.Accent = AccentChoice.Custom;

        controller.AccentHexText = "7c3aed";

        Assert.Equal("#7C3AED", controller.AccentCustom);
        Assert.Equal("7c3aed", controller.AccentHexText); // not rewritten under the cursor
        Assert.False(controller.AccentHexInvalid);
    }

    [Fact]
    public void Anything_else_is_marked_invalid_and_changes_nothing()
    {
        using var controller = NewController();
        controller.AccentCustom = "#7C3AED";
        controller.Accent = AccentChoice.Custom;

        controller.AccentHexText = "12G4";
        Assert.True(controller.AccentHexInvalid);
        Assert.Equal("#7C3AED", controller.AccentCustom);

        controller.AccentHexText = "";
        Assert.True(controller.AccentHexInvalid); // the own colour needs one

        controller.AccentHexText = "#22c55e";
        Assert.False(controller.AccentHexInvalid);
        Assert.Equal("#22C55E", controller.AccentCustom);
    }

    [Fact]
    public void Picking_another_dot_drops_an_invalid_draft()
    {
        using var controller = NewController();
        controller.AccentCustom = "#7C3AED";
        controller.Accent = AccentChoice.Custom;
        controller.AccentHexText = "12G4";

        controller.Accent = AccentChoice.Indigo;
        controller.Accent = AccentChoice.Custom;

        Assert.Equal("#7C3AED", controller.AccentHexText);
        Assert.False(controller.AccentHexInvalid);
    }

    [Fact]
    public void An_own_colour_set_from_outside_shows_in_the_hex_field()
    {
        using var controller = NewController();
        controller.AccentHexText = "zz";

        controller.AccentCustom = "#38bdf8"; // e.g. a restored backup

        Assert.Equal("#38BDF8", controller.AccentHexText);
        Assert.False(controller.AccentHexInvalid);
    }

    [Fact]
    public void The_own_colour_row_says_what_it_becomes_in_the_theme_on_screen()
    {
        using var controller = NewController();
        controller.Theme = ThemeChoice.Dark;
        controller.AccentCustom = "#1E3A8A";
        controller.Accent = AccentChoice.Custom;

        AccentTokens expected = AccentPalette.Derive(ThemeChoice.Dark, AccentChoice.Custom, Color.Parse("#1E3A8A"))!;
        Assert.Equal("Im Theme „Dunkel“: Linien", controller.AccentDerivedText);
        Assert.Equal(Hex(expected.Accent), controller.AccentLineHex);
        Assert.Equal(Hex(expected.Fill), controller.AccentFillHex);
        Assert.True(controller.AccentAdjusted); // far too dark for lines on the dark theme

        controller.Theme = ThemeChoice.Light;
        Assert.Equal("Im Theme „Hell“: Linien", controller.AccentDerivedText);
        Assert.False(controller.AccentAdjusted); // reads as it is on light surfaces, with white text on it
    }

    [Fact]
    public void An_accent_close_to_a_status_colour_gets_a_note()
    {
        using var controller = NewController();

        controller.Accent = AccentChoice.Orange;
        Assert.True(controller.HasAccentWarning);
        Assert.StartsWith("Liegt nah an Warn- und Auslass-Orange", controller.AccentWarning);

        controller.Accent = AccentChoice.Indigo;
        Assert.False(controller.HasAccentWarning);
        Assert.Equal("", controller.AccentWarning);

        controller.AccentCustom = "#EF4444";
        controller.Accent = AccentChoice.Custom;
        Assert.StartsWith("Liegt nah am Fehler-Rot", controller.AccentWarning);
    }

    [Fact]
    public void A_language_change_renames_the_dots_in_place()
    {
        using var controller = NewController();
        AccentSwatch standard = controller.AccentSwatches[0];
        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);

            Assert.Same(standard, controller.AccentSwatches[0]);
            Assert.Equal("Default", standard.Name);
            Assert.Equal("Custom", controller.AccentSwatches[^1].Name);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // restore the pinned test culture
        }
    }
}
