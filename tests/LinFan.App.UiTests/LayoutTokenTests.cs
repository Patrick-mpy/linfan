// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using LinFan.App.Services;

namespace LinFan.App.UiTests;

/// <summary>
/// Sichert die Layout-Skala-Tokens (Theme/Tokens.axaml, in App.axaml gemergt): jeder Spacing-/Radius-Key
/// muss auf seinen erwarteten Wert auflösen. Ein vertippter Key oder ein nicht gemergtes Dictionary würde
/// sonst erst zur Laufzeit als nicht aufgelöstes Binding auffallen (Build bleibt grün).
/// </summary>
public class LayoutTokenTests
{
    [AvaloniaTheory]
    [InlineData("SpacingXxs", 2.0)]
    [InlineData("SpacingXs", 6.0)]
    [InlineData("SpacingSm", 8.0)]
    [InlineData("SpacingMd", 10.0)]
    [InlineData("SpacingLg", 12.0)]
    [InlineData("SpacingXl", 16.0)]
    public void Spacing_token_resolves(string key, double expected)
    {
        bool found = Application.Current!.TryGetResource(key, null, out object? value);
        Assert.True(found, $"Spacing-Token '{key}' fehlt.");
        Assert.Equal(expected, Assert.IsType<double>(value));
    }

    /// <summary>The radii differ per theme: rounded everywhere, square in the terminal theme.</summary>
    [AvaloniaTheory]
    [InlineData("RadiusXxs", 2.0)]
    [InlineData("RadiusXs", 4.0)]
    [InlineData("RadiusSm", 6.0)]
    [InlineData("RadiusMd", 8.0)]
    [InlineData("RadiusLg", 10.0)]
    [InlineData("RadiusXl", 12.0)]
    [InlineData("RadiusXxl", 14.0)]
    [InlineData("RadiusPill", 999.0)]
    public void Radius_token_resolves_per_theme(string key, double expected)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariantMap.HighContrast, ThemeVariant.Default })
        {
            Assert.True(Application.Current!.TryGetResource(key, variant, out object? value), $"Radius-Token '{key}' fehlt in {variant}.");
            Assert.Equal(new CornerRadius(expected), Assert.IsType<CornerRadius>(value));
        }
        Assert.True(Application.Current!.TryGetResource(key, ThemeVariantMap.Terminal, out object? square));
        Assert.Equal(new CornerRadius(0), Assert.IsType<CornerRadius>(square));
    }

    /// <summary>
    /// Terminal also squares FluentTheme's own controls and sets one monospace face (a point smaller) for the whole
    /// interface; the other themes keep Fluent's values.
    /// </summary>
    [AvaloniaFact]
    public void Terminal_squares_fluent_and_sets_the_monospace_face()
    {
        object? Get(string key, ThemeVariant variant) =>
            Application.Current!.TryGetResource(key, variant, out object? value) ? value : null;

        foreach (string key in new[] { "ControlCornerRadius", "OverlayCornerRadius", "SliderThumbCornerRadius" })
        {
            Assert.Equal(new CornerRadius(0), Get(key, ThemeVariantMap.Terminal));
            Assert.NotEqual(new CornerRadius(0), Get(key, ThemeVariant.Dark));
        }

        Assert.Same(Get("MonoFontFamily", ThemeVariant.Dark), Get("ContentControlThemeFontFamily", ThemeVariantMap.Terminal));
        Assert.NotSame(Get("MonoFontFamily", ThemeVariant.Dark), Get("ContentControlThemeFontFamily", ThemeVariant.Dark));
        Assert.Equal(13.0, Get("ControlContentThemeFontSize", ThemeVariantMap.Terminal));
        Assert.Equal(14.0, Get("ControlContentThemeFontSize", ThemeVariant.Dark));
    }

    /// <summary>The header's area tabs: 15 px text, 52 px tall beside the wordmark, 44 px in their own row (narrow window).</summary>
    [AvaloniaTheory]
    [InlineData("NavTabFontSize", 15.0)]
    [InlineData("NavTabHeight", 52.0)]
    [InlineData("NavTabHeightNarrow", 44.0)]
    public void Nav_tab_token_resolves(string key, double expected)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out object? value), $"Nav-Token '{key}' fehlt.");
        Assert.Equal(expected, Assert.IsType<double>(value));
    }
}
