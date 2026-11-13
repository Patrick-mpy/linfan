// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Services;
using LinFan.App.Views;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace LinFan.App.UiTests;

/// <summary>
/// Terminal and high contrast, and a chosen accent colour, on the real app: the tokens and FluentTheme's own accent
/// follow per theme, every window carries the class of the theme it shows, and what goes beyond colour (square corners,
/// frames, the monospace face, the focus ring) reaches the rendered controls.
/// </summary>
public class ThemeAppearanceUiTests : IDisposable
{
    private static readonly ThemeChoice[] Themes =
        [ThemeChoice.Light, ThemeChoice.Dark, ThemeChoice.Terminal, ThemeChoice.HighContrast];

    private readonly List<(SettingsController Settings, string Path)> _made = [];

    private static App TheApp => (App)Application.Current!;

    // The shared headless app keeps the last applied settings; the next test applies its own, so they can go here.
    public void Dispose()
    {
        foreach ((SettingsController settings, string path) in _made)
        {
            settings.Dispose();
            File.Delete(path);
        }
        GC.SuppressFinalize(this);
    }

    private SettingsController NewSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"linfan-appearance-{Guid.NewGuid():N}.json");
        var settings = new SettingsController(new UiSettingsStore(path));
        _made.Add((settings, path));
        return settings;
    }

    private void Apply(ThemeChoice theme, AccentChoice accent = AccentChoice.Standard, string? custom = null)
    {
        SettingsController settings = NewSettings();
        settings.Theme = theme;
        settings.AccentCustom = custom;
        settings.Accent = accent;
        TheApp.ApplyAppearance(settings);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Back to how the test app starts (App.axaml): dark, the themes' own accent - the app is shared by all tests.</summary>
    private void Restore() => Apply(ThemeChoice.Dark);

    private static T Resource<T>(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out object? value), $"{key} missing in {variant}.");
        return Assert.IsAssignableFrom<T>(value);
    }

    private static Color Brush(string key, ThemeVariant variant) => Resource<ISolidColorBrush>(key, variant).Color;

    /// <summary>
    /// A check, radio dot or switch knob stands out from the accent it sits on (3:1, graphics) in every theme, with the
    /// theme's own accent and with each preset.
    /// </summary>
    [AvaloniaFact]
    public void Checks_and_knobs_stand_out_on_the_accent()
    {
        try
        {
            foreach (AccentChoice accent in AccentPalette.Presets.Prepend(AccentChoice.Standard))
            {
                Apply(ThemeChoice.Light, accent);
                foreach (ThemeChoice theme in Themes)
                {
                    ThemeVariant variant = ThemeVariantMap.ToVariant(theme);
                    foreach ((string glyph, string fill) in new[]
                    {
                        ("CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckBackgroundFillChecked"),
                        ("ToggleSwitchKnobFillOn", "ToggleSwitchFillOn"),
                    })
                    {
                        double ratio = ColorContrast.Ratio(Brush(glyph, variant), Brush(fill, variant));
                        Assert.True(ratio >= 3, $"{glyph} on {fill}, {theme}/{accent}: {ratio:0.00}:1");
                    }
                }
            }
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>A temperature takes its level's colour in the theme on screen - and follows a switch of either.</summary>
    [AvaloniaFact]
    public void A_temperature_colour_follows_its_level_and_the_theme()
    {
        try
        {
            var text = new TextBlock { Text = "85 °C" };
            var line = new Sparkline();
            var window = new Window { Content = new StackPanel { Children = { text, line } } };
            window.Show();
            Apply(ThemeChoice.Dark);
            TemperatureColor.SetValue(text, 85);
            TemperatureColor.SetValue(line, 85);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Brush("TempHot", ThemeVariant.Dark), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
            Assert.Equal(Brush("TempHot", ThemeVariant.Dark), Assert.IsAssignableFrom<ISolidColorBrush>(line.Stroke).Color);

            Apply(ThemeChoice.HighContrast);
            ThemeVariant hc = ThemeVariantMap.HighContrast;
            Assert.NotEqual(Brush("TempHot", ThemeVariant.Dark), Brush("TempHot", hc));
            Assert.Equal(Brush("TempHot", hc), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);

            TemperatureColor.SetValue(text, double.NaN);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Brush("TempNone", hc), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
            window.Close();
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>High contrast frames a hovered button in the accent - the chosen one, not its own cyan.</summary>
    [AvaloniaFact]
    public void High_contrast_frames_a_hovered_button_in_the_chosen_accent()
    {
        try
        {
            Apply(ThemeChoice.HighContrast, AccentChoice.Violet);
            ThemeVariant variant = ThemeVariantMap.ToVariant(ThemeChoice.HighContrast);
            var button = new Button { Content = "x" };
            var window = new Window { Content = button };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(button.TryFindResource("ButtonBorderBrushPointerOver", variant, out object? frame));
            Assert.Equal(AccentPalette.Derive(ThemeChoice.HighContrast, AccentChoice.Violet)!.Accent,
                Assert.IsAssignableFrom<ISolidColorBrush>(frame).Color);
            window.Close();
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void A_chosen_accent_reaches_every_theme_and_fluents_controls()
    {
        try
        {
            Apply(ThemeChoice.Light, AccentChoice.Pink);

            foreach (ThemeChoice theme in Themes)
            {
                ThemeVariant variant = ThemeVariantMap.ToVariant(theme);
                AccentTokens expected = AccentPalette.Derive(theme, AccentChoice.Pink)!;
                Assert.Equal(expected.Accent, Brush("Accent", variant));
                Assert.Equal(expected.Fill, Brush("AccentFill", variant));
                Assert.Equal(expected.OnAccent, Brush("OnAccent", variant));
                Assert.Equal(expected.Accent, Resource<Color>("AccentColor", variant));
                Assert.Equal(expected.Shade, Resource<Color>("AccentShadeColor", variant));
            }

            // FluentTheme's own controls: its light and dark palette carry the accent as those themes show it.
            Color light = AccentPalette.Derive(ThemeChoice.Light, AccentChoice.Pink)!.Accent;
            Assert.Equal(light, Resource<Color>("SystemAccentColor", ThemeVariant.Light));
            Assert.Equal(light, Brush("CheckBoxCheckBackgroundFillChecked", ThemeVariant.Light));
            Assert.Equal(AccentPalette.Derive(ThemeChoice.Dark, AccentChoice.Pink)!.Accent,
                Brush("ToggleSwitchFillOn", ThemeVariant.Dark));

            // Back to the themes' own accent: nothing overrides the theme dictionaries any more.
            Apply(ThemeChoice.Light);
            foreach (ThemeChoice theme in Themes)
                Assert.Equal(AccentPalette.StandardAccent(theme), Brush("Accent", ThemeVariantMap.ToVariant(theme)));
            Assert.Equal(AccentPalette.StandardAccent(ThemeChoice.Light), Brush("CheckBoxCheckBackgroundFillChecked", ThemeVariant.Light));
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void An_invalid_own_colour_shows_the_themes_own_accent()
    {
        try
        {
            Apply(ThemeChoice.Dark, AccentChoice.Custom, custom: null);
            Assert.Equal(AccentPalette.StandardAccent(ThemeChoice.Dark), Brush("Accent", ThemeVariant.Dark));
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>
    /// Fluent resolves its accent under the dark key for variants of our own, so while terminal or high contrast is on,
    /// the dark palette carries their accent - their own one included.
    /// </summary>
    [AvaloniaFact]
    public void Terminal_and_high_contrast_put_their_accent_into_fluents_dark_palette()
    {
        try
        {
            Apply(ThemeChoice.Terminal);
            Assert.Same(ThemeVariantMap.Terminal, TheApp.RequestedThemeVariant);
            Assert.Equal(Color.Parse("#39FF6A"), Brush("CheckBoxCheckBackgroundFillChecked", ThemeVariantMap.Terminal));
            Assert.Equal(Color.Parse("#050805"), Brush("CheckBoxCheckGlyphForegroundChecked", ThemeVariantMap.Terminal));

            Apply(ThemeChoice.HighContrast, AccentChoice.Violet);
            Assert.Equal(AccentPalette.Derive(ThemeChoice.HighContrast, AccentChoice.Violet)!.Accent,
                Brush("ToggleSwitchFillOn", ThemeVariantMap.HighContrast));

            Apply(ThemeChoice.Dark);
            Assert.Equal(AccentPalette.StandardAccent(ThemeChoice.Dark), Brush("ToggleSwitchFillOn", ThemeVariant.Dark));
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void The_settings_learn_which_theme_is_on_screen()
    {
        try
        {
            SettingsController settings = NewSettings();
            settings.Theme = ThemeChoice.System;
            TheApp.ApplyAppearance(settings);
            Assert.Equal(ThemeVariantMap.Concrete(TheApp.ActualThemeVariant), settings.ActiveTheme);

            // A later switch of the variant (the OS turning light) reaches the settings too.
            TheApp.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeChoice.Light, settings.ActiveTheme);
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void Every_window_carries_the_class_of_the_theme_it_shows()
    {
        WindowThemeClasses.AttachAll();
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()), settings: NewSettings());
        var main = new MainWindow { DataContext = ctrl };
        main.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        var onboarding = new OnboardingWindow
        {
            DataContext = new OnboardingController(_ => Task.CompletedTask, () => Task.CompletedTask, _ => Task.FromResult(true), () => { }),
        };
        var confirm = new ConfirmDialog("Titel", "Meldung", "OK");
        var unsaved = new UnsavedChangesDialog("Titel", "Meldung", "Verwerfen", "Speichern", null);
        var location = new FanLocationDialog(ctrl.Editor.CreateLocationPick(ctrl.Editor.Fans[0]));
        Window[] windows = [main, onboarding, confirm, unsaved, location];
        foreach (Window w in windows.Skip(1))
            w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            void AssertClasses(ThemeVariant variant, bool terminal, bool contrast)
            {
                TheApp.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                foreach (Window w in windows)
                {
                    Assert.Equal(terminal, w.Classes.Contains(WindowThemeClasses.Terminal));
                    Assert.Equal(contrast, w.Classes.Contains(WindowThemeClasses.HighContrast));
                }
            }

            AssertClasses(ThemeVariantMap.Terminal, terminal: true, contrast: false);
            AssertClasses(ThemeVariantMap.HighContrast, terminal: false, contrast: true);
            AssertClasses(ThemeVariant.Dark, terminal: false, contrast: false);
        }
        finally
        {
            foreach (Window w in windows.Skip(1))
                w.Close();
            ctrl.Dispose();
            Restore();
            // The main window stays open on purpose: closing it would write its geometry.
        }
    }

    [AvaloniaFact]
    public void Terminal_cards_are_square_framed_and_monospace()
    {
        WindowThemeClasses.AttachAll();
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()), settings: NewSettings());
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        try
        {
            window.Find<TabControl>().Single().SelectedIndex = 1; // the curve editor sits on a card
            Dispatcher.UIThread.RunJobs();
            Border Card() => window.Find<Border>().First(b => b.Classes.Contains("card") && b.IsEffectivelyVisible);

            TheApp.RequestedThemeVariant = ThemeVariantMap.Terminal;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new CornerRadius(0), Card().CornerRadius);
            Assert.Equal(new Thickness(1), Card().BorderThickness);
            Assert.Equal(Brush("CardOutline", ThemeVariantMap.Terminal), Assert.IsAssignableFrom<ISolidColorBrush>(Card().BorderBrush).Color);
            Assert.Same(Resource<FontFamily>("MonoFontFamily", ThemeVariant.Dark), window.FontFamily);

            TheApp.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new CornerRadius(12), Card().CornerRadius);
            Assert.Equal(default, Card().BorderThickness);
            Assert.NotSame(Resource<FontFamily>("MonoFontFamily", ThemeVariant.Dark), window.FontFamily);
        }
        finally
        {
            ctrl.Dispose();
            Restore();
        }
    }

    /// <summary>High contrast: keyboard focus is a 3 px ring in the warning yellow instead of Fluent's own.</summary>
    [AvaloniaFact]
    public void High_contrast_focus_is_a_wide_ring()
    {
        WindowThemeClasses.AttachAll();
        var button = new Button { Content = "OK" };
        var window = new Window { Content = button };
        window.Show();
        try
        {
            TheApp.RequestedThemeVariant = ThemeVariantMap.HighContrast;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(button.FocusAdorner);
            var ring = Assert.IsType<Border>(button.FocusAdorner!.Build());
            Assert.Equal(new Thickness(3), ring.BorderThickness);

            TheApp.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(button.FocusAdorner); // Fluent's own again
        }
        finally
        {
            window.Close();
            Restore();
        }
    }

    /// <summary>
    /// Picking a dot in the real list box sets the accent and keeps the list - replacing it while the list box changes
    /// its own selection made the selection fall back to the first dot ("Standard").
    /// </summary>
    [AvaloniaFact]
    public void Picking_an_accent_dot_keeps_the_choice_and_the_list()
    {
        SettingsController settings = NewSettings();
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()), settings: settings);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
            window.Find<TabControl>().Single().SelectedIndex = 2;
            settings.OpenSection(SettingsSection.Appearance);
            Dispatcher.UIThread.RunJobs();
            ListBox dots = window.Find<ListBox>().Single(l => l.Name == "AccentSwatches");
            Assert.True(dots.IsEffectivelyVisible);
            Assert.Equal(8, dots.ItemCount);

            dots.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AccentChoice.Pink, settings.Accent);

            settings.ActiveTheme = ThemeChoice.Light; // the dots recolour …
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(8, dots.ItemCount);           // … in place
            Assert.Same(settings.SelectedAccentSwatch, dots.SelectedItem);
            Assert.Equal(AccentChoice.Pink, settings.Accent);

            Ellipse wheel = window.Find<Ellipse>().Single(e => e.Classes.Contains("swatchWheel") && e.Classes.Contains("custom"));
            Assert.True(wheel.IsVisible);              // the own colour shows a wheel until it is chosen …

            dots.SelectedIndex = 7;                    // the own colour shows its row
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Find<Border>().Single(b => b.Classes.Contains("accentCustom")).IsEffectivelyVisible);
            Assert.NotNull(settings.AccentCustom);
            Assert.False(wheel.IsVisible);             // … then its dot is the colour itself
            Assert.DoesNotContain(window.Find<Ellipse>(), e => e.Classes.Contains("swatchWheel") && e.IsVisible);
        }
        finally
        {
            ctrl.Dispose();
        }
    }

    /// <summary>
    /// Each theme tile draws its preview with the tokens of its own variant - also the tile of the theme on screen
    /// (a scope bound to the variant it already inherits kept its resources unresolved).
    /// </summary>
    [AvaloniaFact]
    public void Every_theme_tile_previews_its_own_variant()
    {
        SettingsController settings = NewSettings();
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()), settings: settings);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
            window.Find<TabControl>().Single().SelectedIndex = 2;
            settings.OpenSection(SettingsSection.Appearance);
            Dispatcher.UIThread.RunJobs();
            ListBox grid = window.Find<ListBox>().Single(l => l.Name == "ThemeGrid");
            Assert.Equal(5, grid.ItemCount);

            foreach (ThemeVariant onScreen in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariantMap.Terminal })
            {
                TheApp.RequestedThemeVariant = onScreen;
                Dispatcher.UIThread.RunJobs();
                ThemeVariantScope[] scopes = grid.GetVisualDescendants().OfType<ThemeVariantScope>().Where(s => s.IsVisible).ToArray();
                Assert.Equal(6, scopes.Length); // System shows two halves
                foreach (ThemeVariantScope scope in scopes)
                {
                    Panel pane = scope.GetVisualDescendants().OfType<Panel>().First();
                    Assert.Equal(Brush("BgWindow", scope.RequestedThemeVariant!),
                        Assert.IsAssignableFrom<ISolidColorBrush>(pane.Background).Color);
                }
            }
        }
        finally
        {
            ctrl.Dispose();
            Restore();
        }
    }

    /// <summary>
    /// The theme tiles start at the left edge, like the accent colours below, and a wider window puts more of them in a
    /// row (a width limit kept them at four and centred the grid in a wide window).
    /// </summary>
    [AvaloniaFact]
    public void The_theme_tiles_start_at_the_left_and_take_the_row_the_page_has_room_for()
    {
        SettingsController settings = NewSettings();
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()), settings: settings);
        var window = new MainWindow { DataContext = ctrl, Width = 1900 };
        window.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
            window.Find<TabControl>().Single().SelectedIndex = 2;
            settings.OpenSection(SettingsSection.Appearance);
            Dispatcher.UIThread.RunJobs();
            ListBox grid = window.Find<ListBox>().Single(l => l.Name == "ThemeGrid");
            ListBox swatches = window.Find<ListBox>().Single(l => l.Name == "AccentSwatches");
            ListBoxItem[] tiles = grid.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Assert.Equal(5, tiles.Length);
            double Left(Visual visual) => visual.TranslatePoint(default, window)!.Value.X;
            int Rows() => tiles.Select(t => t.Bounds.Y).Distinct().Count();

            Assert.Equal(Left(swatches), Left(tiles[0]), 1);
            Assert.Equal(1, Rows());

            window.Width = 1000;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Left(swatches), Left(tiles[0]), 1);
            Assert.True(Rows() > 1);
        }
        finally
        {
            ctrl.Dispose();
        }
    }
}
