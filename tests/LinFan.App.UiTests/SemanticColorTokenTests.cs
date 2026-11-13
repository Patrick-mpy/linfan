// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using LinFan.App.Services;

namespace LinFan.App.UiTests;

/// <summary>
/// Sichert die semantischen Farb-Tokens (Theme/SemanticColors.axaml, in App.axaml gemergt): jeder Key muss
/// für <b>alle</b> Varianten auflösen (Light/Dark, und Terminal/HighContrast, die fehlende Keys von Dark erben). Ein
/// vertippter oder nur in einer Variante definierter Key würde sonst erst zur Laufzeit beim Theme-Wechsel als
/// leeres/fehlendes Binding auffallen.
/// </summary>
public class SemanticColorTokenTests
{
    private static readonly ThemeVariant[] Variants =
        [ThemeVariant.Light, ThemeVariant.Dark, ThemeVariantMap.Terminal, ThemeVariantMap.HighContrast];

    private static readonly ThemeChoice[] Themes =
        [ThemeChoice.Light, ThemeChoice.Dark, ThemeChoice.Terminal, ThemeChoice.HighContrast];

    private static T Resource<T>(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out object? value), $"{key} missing in {variant}.");
        return Assert.IsAssignableFrom<T>(value);
    }

    /// <summary>
    /// Services/AccentPalette derives accents without an application, so it repeats each theme's surfaces, its own accent
    /// and the ink on filled buttons - they must stay what the theme dictionaries say.
    /// </summary>
    [AvaloniaFact]
    public void AccentPalette_repeats_the_theme_dictionaries_faithfully()
    {
        foreach (ThemeChoice theme in Themes)
        {
            ThemeVariant variant = ThemeVariantMap.ToVariant(theme);
            Assert.Equal(Resource<Color>("AccentColor", variant), AccentPalette.StandardAccent(theme));
            Assert.Equal(
                new[] { "BgWindow", "BgCard", "BgCardAlt" }.Select(k => Resource<ISolidColorBrush>(k, variant).Color),
                AccentPalette.Backgrounds(theme));
            Assert.Equal(Resource<ISolidColorBrush>("OnAccent", variant).Color,
                AccentPalette.Derive(theme, AccentChoice.Indigo)!.OnAccent);
        }
    }

    /// <summary>The variants of their own really differ from dark - and inherit what they leave out (the exhaust colour).</summary>
    [AvaloniaFact]
    public void Terminal_and_high_contrast_override_dark_and_inherit_the_rest()
    {
        Assert.Equal(Color.Parse("#050805"), Resource<ISolidColorBrush>("BgWindow", ThemeVariantMap.Terminal).Color);
        Assert.Equal(Colors.Black, Resource<ISolidColorBrush>("BgWindow", ThemeVariantMap.HighContrast).Color);
        Assert.Equal(Resource<Color>("ExhaustColor", ThemeVariant.Dark), Resource<Color>("ExhaustColor", ThemeVariantMap.Terminal));
        // Intake keeps a colour of its own, apart from the accent.
        Assert.NotEqual(Resource<Color>("AccentColor", ThemeVariantMap.Terminal), Resource<Color>("IntakeColor", ThemeVariantMap.Terminal));
    }

    /// <summary>High contrast: every text token reaches 7:1 on every surface - the faint ones too.</summary>
    [AvaloniaFact]
    public void High_contrast_text_reaches_seven_to_one()
    {
        ThemeVariant hc = ThemeVariantMap.HighContrast;
        string[] texts = ["TextPrimary", "TextHeading", "TextSecondary", "TextMuted", "TextFaint", "TextFaintest", "Accent", "WarningText", "DangerAccent", "Success",
            "TempCool", "TempWarm", "TempHot", "TempNone"];
        foreach (string text in texts)
            foreach (string surface in new[] { "BgWindow", "BgCard", "BgCardAlt" })
            {
                double ratio = ColorContrast.Ratio(Resource<ISolidColorBrush>(text, hc).Color, Resource<ISolidColorBrush>(surface, hc).Color);
                Assert.True(ratio >= 7, $"{text} on {surface}: {ratio:0.00}:1");
            }
    }

    [AvaloniaTheory]
    [InlineData("BgWindow")]
    [InlineData("BgCard")]
    [InlineData("BgCardAlt")]
    [InlineData("BgInset")]
    [InlineData("TextPrimary")]
    [InlineData("TextHeading")]
    [InlineData("TextSecondary")]
    [InlineData("TextMuted")]
    [InlineData("TextFaint")]
    [InlineData("TextFaintest")]
    [InlineData("Accent")]
    [InlineData("Success")]
    [InlineData("Warning")]
    [InlineData("WarningText")]
    [InlineData("DangerBg")]
    [InlineData("DangerText")]
    [InlineData("DangerAccent")]
    [InlineData("InfoBg")]
    [InlineData("InfoText")]
    [InlineData("InfoAccent")]
    [InlineData("WarningBg")]
    [InlineData("AccentTint")]
    [InlineData("ControlHover")]
    [InlineData("ControlTrack")]
    [InlineData("AccentFill")]
    [InlineData("AccentHover")]
    [InlineData("AccentPressed")]
    [InlineData("OnAccent")]
    [InlineData("DangerFill")]
    [InlineData("DangerHover")]
    [InlineData("DangerPressed")]
    [InlineData("OnDanger")]
    [InlineData("IntakeBrush")]
    [InlineData("ExhaustBrush")]
    [InlineData("InternalBrush")]
    [InlineData("CardOutline")]
    [InlineData("TempCool")]
    [InlineData("TempWarm")]
    [InlineData("TempHot")]
    [InlineData("TempNone")]
    public void Brush_token_resolves_in_every_variant(string key)
    {
        foreach (ThemeVariant variant in Variants)
        {
            bool found = Application.Current!.TryGetResource(key, variant, out object? value);
            Assert.True(found, $"Brush-Token '{key}' fehlt in {variant}.");
            Assert.IsAssignableFrom<IBrush>(value);
        }
    }

    private static BoxShadows Shadow(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out object? value), $"{key} missing in {variant}.");
        return Assert.IsType<BoxShadows>(value);
    }

    /// <summary>Light and dark lift with one shadow each; terminal and high contrast draw frames, the small lift is none.</summary>
    [AvaloniaTheory]
    [InlineData("ShadowLg", 1)]
    [InlineData("ShadowSm", 0)]
    public void Shadow_token_resolves_in_every_variant(string key, int framedCount)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            Assert.Equal(1, Shadow(key, variant).Count);
        foreach (ThemeVariant variant in new[] { ThemeVariantMap.Terminal, ThemeVariantMap.HighContrast })
            Assert.Equal(framedCount, Shadow(key, variant).Count);
    }

    /// <summary>
    /// The floating-surface shadow must really differ per theme: tuned for the dark window, it would smudge the
    /// light one.
    /// </summary>
    [AvaloniaFact]
    public void Floating_shadow_differs_per_variant() =>
        Assert.NotEqual(Shadow("ShadowLg", ThemeVariant.Light), Shadow("ShadowLg", ThemeVariant.Dark));

    [AvaloniaTheory]
    [InlineData("GridColor")]
    [InlineData("AxisColor")]
    [InlineData("HandleColor")]
    [InlineData("AccentColor")]
    [InlineData("AccentShadeColor")]
    [InlineData("ClampColor")]
    [InlineData("LiveColor")]
    public void Color_token_resolves_in_every_variant(string key)
    {
        foreach (ThemeVariant variant in Variants)
        {
            bool found = Application.Current!.TryGetResource(key, variant, out object? value);
            Assert.True(found, $"Color-Token '{key}' fehlt in {variant}.");
            Assert.IsType<Color>(value);
        }
    }
}
