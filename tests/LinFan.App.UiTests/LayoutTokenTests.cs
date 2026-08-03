// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless.XUnit;

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

    [AvaloniaTheory]
    [InlineData("RadiusSm", 6.0)]
    [InlineData("RadiusMd", 8.0)]
    [InlineData("RadiusLg", 10.0)]
    [InlineData("RadiusXl", 12.0)]
    [InlineData("RadiusXxl", 14.0)]
    public void Radius_token_resolves(string key, double expected)
    {
        bool found = Application.Current!.TryGetResource(key, null, out object? value);
        Assert.True(found, $"Radius-Token '{key}' fehlt.");
        Assert.Equal(new CornerRadius(expected), Assert.IsType<CornerRadius>(value));
    }
}
