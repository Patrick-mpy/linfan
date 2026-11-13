// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Media;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>The colour arithmetic behind the accent colours: WCAG contrast, the hex field's parser, lighten until it reads.</summary>
public sealed class ColorContrastTests
{
    [Fact]
    public void Black_on_white_is_21_to_1_and_a_colour_on_itself_1_to_1()
    {
        Assert.Equal(21, ColorContrast.Ratio(Colors.Black, Colors.White), 3);
        Assert.Equal(21, ColorContrast.Ratio(Colors.White, Colors.Black), 3);
        Assert.Equal(1, ColorContrast.Ratio(Colors.Orange, Colors.Orange), 3);
    }

    [Fact]
    public void A_known_pair_gives_its_published_ratio() =>
        // #767676 on white is the classic "just 4.5:1" grey.
        Assert.Equal(4.54, ColorContrast.Ratio(Color.Parse("#767676"), Colors.White), 2);

    [Theory]
    [InlineData("#38BDF8")]
    [InlineData("#38bdf8")]
    [InlineData("38BDF8")]
    [InlineData("  #38BdF8 ")]
    public void Six_hex_digits_are_a_colour_with_or_without_hash(string text)
    {
        Assert.True(ColorContrast.TryParseHex(text, out Color c));
        Assert.Equal("#38BDF8", ColorContrast.ToHex(c));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#FFF")]
    [InlineData("#38BDF")]
    [InlineData("#38BDF8FF")]
    [InlineData("#GGGGGG")]
    [InlineData("12G4")]
    [InlineData("red")]
    [InlineData("##38BDF8")]
    public void Anything_else_is_no_colour(string? text) =>
        Assert.False(ColorContrast.TryParseHex(text, out _));

    [Fact]
    public void Fit_leaves_a_colour_that_reads_and_lightens_one_that_does_not()
    {
        Color[] dark = [Color.Parse("#16161A")];
        Color light = Color.Parse("#A1A1AA");
        Assert.Equal(light, ColorContrast.Fit(light, dark, 4.5, lighten: true));

        Color tooDark = Color.Parse("#3730A3");
        Color fitted = ColorContrast.Fit(tooDark, dark, 4.5, lighten: true);
        Assert.True(ColorContrast.Ratio(fitted, dark[0]) >= 4.5);
        Assert.True(ColorContrast.Luminance(fitted) > ColorContrast.Luminance(tooDark));
    }

    [Fact]
    public void Mix_blends_like_css_color_mix() =>
        Assert.Equal(Color.Parse("#808080"), ColorContrast.Mix(Colors.White, Colors.Black, 0.5));
}
