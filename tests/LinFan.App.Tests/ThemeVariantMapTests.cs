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
}
