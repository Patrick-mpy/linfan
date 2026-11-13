// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// The accent derivation: whatever the user picks, every theme gets a variant that reads - lines and text against the
/// theme's surfaces, button text on the fill (4.5:1, high contrast 7:1) - from the first sufficient ladder step for a
/// preset, lightened or darkened for an own colour.
/// </summary>
public sealed class AccentPaletteTests
{
    private static readonly ThemeChoice[] Themes =
        [ThemeChoice.Light, ThemeChoice.Dark, ThemeChoice.Terminal, ThemeChoice.HighContrast];

    public static TheoryData<ThemeChoice, AccentChoice> EveryPresetInEveryTheme()
    {
        var data = new TheoryData<ThemeChoice, AccentChoice>();
        foreach (ThemeChoice theme in Themes)
            foreach (AccentChoice preset in AccentPalette.Presets)
                data.Add(theme, preset);
        return data;
    }

    public static TheoryData<ThemeChoice, string> OwnColoursInEveryTheme()
    {
        string[] colours = ["#FFFFFF", "#000000", "#FFFF00", "#7C3AED", "#38BDF8", "#808080", "#0000FF", "#1E3A8A", "#39FF6A"];
        var data = new TheoryData<ThemeChoice, string>();
        foreach (ThemeChoice theme in Themes)
            foreach (string colour in colours)
                data.Add(theme, colour);
        return data;
    }

    private static Color Hex(string hex) =>
        ColorContrast.TryParseHex(hex, out Color c) ? c : throw new ArgumentException(hex);

    private static void AssertReadable(ThemeChoice theme, AccentTokens tokens)
    {
        double min = AccentPalette.MinContrast(theme);
        Assert.True(ColorContrast.MinRatio(tokens.Accent, AccentPalette.Backgrounds(theme)) >= min,
            $"Accent {ColorContrast.ToHex(tokens.Accent)} too faint on {theme}.");
        foreach ((string name, Color fill) in new[] { ("fill", tokens.Fill), ("hover", tokens.Hover), ("pressed", tokens.Pressed) })
            Assert.True(ColorContrast.Ratio(fill, tokens.OnAccent) >= min,
                $"Button {name} {ColorContrast.ToHex(fill)} carries {ColorContrast.ToHex(tokens.OnAccent)} below {min}:1 on {theme}.");
    }

    [Theory]
    [MemberData(nameof(EveryPresetInEveryTheme))]
    public void Every_preset_reads_in_every_theme(ThemeChoice theme, AccentChoice preset) =>
        AssertReadable(theme, AccentPalette.Derive(theme, preset)!);

    [Theory]
    [MemberData(nameof(OwnColoursInEveryTheme))]
    public void Every_own_colour_reads_in_every_theme(ThemeChoice theme, string colour) =>
        AssertReadable(theme, AccentPalette.Derive(theme, AccentChoice.Custom, Hex(colour))!);

    [Fact]
    public void High_contrast_asks_for_seven_to_one_the_others_for_four_and_a_half()
    {
        Assert.Equal(7, AccentPalette.MinContrast(ThemeChoice.HighContrast));
        Assert.Equal(4.5, AccentPalette.MinContrast(ThemeChoice.Light));
        Assert.Equal(4.5, AccentPalette.MinContrast(ThemeChoice.Dark));
        Assert.Equal(4.5, AccentPalette.MinContrast(ThemeChoice.Terminal));
    }

    /// <summary>
    /// Light starts at 600 and walks darker, dark and terminal start at 400, high contrast at 300 - each takes the first
    /// step that reads. Indigo 600 already does on light surfaces, pink needs 700; on dark ones indigo 400 suffices.
    /// </summary>
    [Theory]
    [InlineData(ThemeChoice.Light, AccentChoice.Indigo, "#4F46E5")]
    [InlineData(ThemeChoice.Light, AccentChoice.Pink, "#BE185D")]
    [InlineData(ThemeChoice.Light, AccentChoice.Gray, "#52525B")]
    [InlineData(ThemeChoice.Dark, AccentChoice.Indigo, "#818CF8")]
    [InlineData(ThemeChoice.Dark, AccentChoice.Gray, "#A1A1AA")]
    [InlineData(ThemeChoice.Terminal, AccentChoice.Orange, "#FB923C")]
    [InlineData(ThemeChoice.HighContrast, AccentChoice.Indigo, "#A5B4FC")]
    public void A_preset_takes_the_first_ladder_step_that_reads(ThemeChoice theme, AccentChoice preset, string expected) =>
        Assert.Equal(expected, ColorContrast.ToHex(AccentPalette.Derive(theme, preset)!.Accent));

    [Theory]
    [MemberData(nameof(EveryPresetInEveryTheme))]
    public void No_earlier_ladder_step_would_have_read(ThemeChoice theme, AccentChoice preset)
    {
        IReadOnlyList<Color> ladder = AccentPalette.LadderOf(preset);
        Color accent = AccentPalette.Derive(theme, preset)!.Accent;
        int chosen = ladder.ToList().IndexOf(accent);
        if (chosen < 0)
            return; // computed past the end of the ladder - then no step read at all

        IEnumerable<int> before = theme == ThemeChoice.Light
            ? Enumerable.Range(3, chosen - 3)                        // 600 … one before the chosen, darker
            : Enumerable.Range(chosen + 1, StartOf(theme) - chosen); // the start … one before the chosen, lighter
        foreach (int step in before)
            Assert.True(ColorContrast.MinRatio(ladder[step], AccentPalette.Backgrounds(theme)) < AccentPalette.MinContrast(theme),
                $"{preset} on {theme}: step {step} would have read already.");
    }

    private static int StartOf(ThemeChoice theme) => theme == ThemeChoice.HighContrast ? 0 : 1;

    [Fact]
    public void Light_and_dark_fill_buttons_under_white_terminal_and_high_contrast_under_dark_ink()
    {
        Assert.Equal(Colors.White, AccentPalette.Derive(ThemeChoice.Light, AccentChoice.Violet)!.OnAccent);
        Assert.Equal(Colors.White, AccentPalette.Derive(ThemeChoice.Dark, AccentChoice.Violet)!.OnAccent);
        Assert.Equal(Hex("#050805"), AccentPalette.Derive(ThemeChoice.Terminal, AccentChoice.Violet)!.OnAccent);
        Assert.Equal(Colors.Black, AccentPalette.Derive(ThemeChoice.HighContrast, AccentChoice.Violet)!.OnAccent);

        // Terminal and high contrast fill with the bright accent itself.
        AccentTokens terminal = AccentPalette.Derive(ThemeChoice.Terminal, AccentChoice.Violet)!;
        Assert.Equal(terminal.Accent, terminal.Fill);
    }

    [Fact]
    public void The_tint_is_the_accent_with_little_alpha()
    {
        AccentTokens dark = AccentPalette.Derive(ThemeChoice.Dark, AccentChoice.Pink)!;
        Assert.Equal(Color.FromArgb(0x14, dark.Accent.R, dark.Accent.G, dark.Accent.B), dark.Tint);
        AccentTokens contrast = AccentPalette.Derive(ThemeChoice.HighContrast, AccentChoice.Pink)!;
        Assert.Equal(0x26, contrast.Tint.A);
    }

    [Fact]
    public void The_themes_own_accent_overrides_nothing()
    {
        foreach (ThemeChoice theme in Themes)
        {
            Assert.Null(AccentPalette.Derive(theme, AccentChoice.Standard));
            Assert.Equal(AccentPalette.StandardAccent(theme), AccentPalette.AccentOf(theme, AccentChoice.Standard));
        }
    }

    [Fact]
    public void System_is_no_theme_to_derive_for() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AccentPalette.Derive(ThemeChoice.System, AccentChoice.Pink));

    [Fact]
    public void An_own_colour_that_reads_as_line_and_fill_stays_as_it_is()
    {
        // Dark blue on light surfaces, white text on it - nothing to change.
        Assert.False(AccentPalette.IsAdjusted(ThemeChoice.Light, Hex("#1E40AF")));
        Assert.Equal(Hex("#1E40AF"), AccentPalette.Derive(ThemeChoice.Light, AccentChoice.Custom, Hex("#1E40AF"))!.Accent);
        // The terminal's own green, and the high-contrast cyan, fill under dark ink as they are.
        Assert.False(AccentPalette.IsAdjusted(ThemeChoice.Terminal, Hex("#39FF6A")));
        Assert.False(AccentPalette.IsAdjusted(ThemeChoice.HighContrast, Hex("#3FE0FF")));
        // The dark theme's own accent reads as a line; its deeper button fill is the design, not an adjustment.
        Assert.False(AccentPalette.IsAdjusted(ThemeChoice.Dark, Hex("#38BDF8")));
    }

    [Theory]
    [InlineData(ThemeChoice.Dark)]
    [InlineData(ThemeChoice.Terminal)]
    [InlineData(ThemeChoice.HighContrast)]
    public void An_own_colour_too_dark_for_a_dark_theme_is_lightened(ThemeChoice theme)
    {
        Color own = Hex("#1E3A8A");
        Color accent = AccentPalette.Derive(theme, AccentChoice.Custom, own)!.Accent;

        Assert.True(AccentPalette.IsAdjusted(theme, own));
        Assert.True(ColorContrast.Luminance(accent) > ColorContrast.Luminance(own));
    }

    [Fact]
    public void An_own_colour_too_light_for_the_light_theme_is_darkened()
    {
        Color own = Hex("#FDE047");
        Color accent = AccentPalette.Derive(ThemeChoice.Light, AccentChoice.Custom, own)!.Accent;

        Assert.True(AccentPalette.IsAdjusted(ThemeChoice.Light, own));
        Assert.True(ColorContrast.Luminance(accent) < ColorContrast.Luminance(own));
    }

    [Theory]
    [InlineData(AccentChoice.Orange, null, AccentClash.Warning)]
    [InlineData(AccentChoice.Emerald, null, AccentClash.Success)]
    [InlineData(AccentChoice.Indigo, null, AccentClash.None)]
    [InlineData(AccentChoice.Pink, null, AccentClash.None)]
    [InlineData(AccentChoice.Gray, null, AccentClash.None)]
    [InlineData(AccentChoice.Standard, null, AccentClash.None)]
    [InlineData(AccentChoice.Custom, "#EF4444", AccentClash.Danger)]
    [InlineData(AccentChoice.Custom, "#F59E0B", AccentClash.Warning)]
    [InlineData(AccentChoice.Custom, "#FF4500", AccentClash.Warning)] // orange-red, 16° - between the two, no gap
    [InlineData(AccentChoice.Custom, "#FF2200", AccentClash.Danger)] // 8°
    [InlineData(AccentChoice.Custom, "#22C55E", AccentClash.Success)]
    [InlineData(AccentChoice.Custom, "#7F7F86", AccentClash.None)] // a grey: no hue to clash with
    [InlineData(AccentChoice.Custom, "#2A0505", AccentClash.None)] // near black
    public void A_status_colour_close_by_is_named(AccentChoice choice, string? custom, AccentClash expected) =>
        Assert.Equal(expected, AccentPalette.ClashOf(choice, custom is null ? default : Hex(custom)));

    [Fact]
    public void Every_preset_has_a_ladder_from_300_to_800_getting_darker()
    {
        foreach (AccentChoice preset in AccentPalette.Presets)
        {
            IReadOnlyList<Color> ladder = AccentPalette.LadderOf(preset);
            Assert.Equal(6, ladder.Count);
            for (int i = 1; i < ladder.Count; i++)
                Assert.True(ColorContrast.Luminance(ladder[i]) < ColorContrast.Luminance(ladder[i - 1]), $"{preset} step {i}");
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => AccentPalette.LadderOf(AccentChoice.Custom));
    }
}
