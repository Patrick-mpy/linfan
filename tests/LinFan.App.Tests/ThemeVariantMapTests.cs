// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Styling;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert die Abbildung <see cref="ThemeChoice"/> → Avalonia-<see cref="ThemeVariant"/>: „System" muss
/// auf <see cref="ThemeVariant.Default"/> (folgt OS) abbilden, die festen Modi auf ihre Variante.
/// </summary>
public sealed class ThemeVariantMapTests
{
    [Theory]
    [InlineData(ThemeChoice.System)]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    public void Maps_each_choice_to_expected_variant(ThemeChoice choice)
    {
        ThemeVariant expected = choice switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        Assert.Equal(expected, ThemeVariantMap.ToVariant(choice));
    }

    /// <summary>Terminal and high contrast are variants of their own that inherit Dark - what they leave out falls back to it.</summary>
    [Theory]
    [InlineData(ThemeChoice.Terminal, "Terminal")]
    [InlineData(ThemeChoice.HighContrast, "HighContrast")]
    public void Terminal_and_high_contrast_are_own_variants_inheriting_dark(ThemeChoice choice, string key)
    {
        ThemeVariant variant = ThemeVariantMap.ToVariant(choice);

        Assert.Equal(key, variant.Key);
        Assert.Equal(ThemeVariant.Dark, variant.InheritVariant);
        Assert.Same(variant, ThemeVariantMap.ToVariant(choice)); // one instance, so lookups and comparisons agree
    }

    [Fact]
    public void The_persisted_numbers_of_the_existing_choices_stay()
    {
        Assert.Equal(0, (int)ThemeChoice.System);
        Assert.Equal(1, (int)ThemeChoice.Light);
        Assert.Equal(2, (int)ThemeChoice.Dark);
        Assert.Equal(3, (int)ThemeChoice.Terminal);
        Assert.Equal(4, (int)ThemeChoice.HighContrast);
    }

    [Theory]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    [InlineData(ThemeChoice.Terminal)]
    [InlineData(ThemeChoice.HighContrast)]
    public void Concrete_names_the_theme_a_variant_shows(ThemeChoice choice) =>
        Assert.Equal(choice, ThemeVariantMap.Concrete(ThemeVariantMap.ToVariant(choice)));

    [Fact]
    public void An_unknown_variant_counts_as_what_it_inherits_finally_as_dark()
    {
        Assert.Equal(ThemeChoice.Light, ThemeVariantMap.Concrete(new ThemeVariant("Sepia", ThemeVariant.Light)));
        Assert.Equal(ThemeChoice.Dark, ThemeVariantMap.Concrete(new ThemeVariant("Sepia", null)));
        Assert.Equal(ThemeChoice.Dark, ThemeVariantMap.Concrete(null));
    }

    [Fact]
    public void Dark_and_its_heirs_are_dark()
    {
        Assert.True(ThemeVariantMap.IsDark(ThemeVariant.Dark));
        Assert.True(ThemeVariantMap.IsDark(ThemeVariantMap.Terminal));
        Assert.True(ThemeVariantMap.IsDark(ThemeVariantMap.HighContrast));
        Assert.False(ThemeVariantMap.IsDark(ThemeVariant.Light));
        Assert.False(ThemeVariantMap.IsDark(ThemeVariant.Default));
        Assert.False(ThemeVariantMap.IsDark(null));
    }
}
