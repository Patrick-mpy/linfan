// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace LinFan.App.UiTests;

/// <summary>
/// Sichert die semantischen Farb-Tokens (Theme/SemanticColors.axaml, in App.axaml gemergt): jeder Key muss
/// für <b>beide</b> Varianten (Light/Dark) auflösen. Ein vertippter oder nur in einer Variante definierter
/// Key würde sonst erst zur Laufzeit beim Theme-Wechsel als leeres/fehlendes Binding auffallen.
/// </summary>
public class SemanticColorTokenTests
{
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
    [InlineData("AccentFill")]
    [InlineData("AccentHover")]
    [InlineData("AccentPressed")]
    [InlineData("OnAccent")]
    [InlineData("DangerFill")]
    [InlineData("DangerHover")]
    [InlineData("DangerPressed")]
    [InlineData("OnDanger")]
    public void Brush_token_resolves_in_both_variants(string key)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            bool found = Application.Current!.TryGetResource(key, variant, out object? value);
            Assert.True(found, $"Brush-Token '{key}' fehlt in {variant}.");
            Assert.IsAssignableFrom<IBrush>(value);
        }
    }

    [AvaloniaTheory]
    [InlineData("GridColor")]
    [InlineData("AxisColor")]
    [InlineData("HandleColor")]
    [InlineData("AccentColor")]
    [InlineData("AccentShadeColor")]
    [InlineData("ClampColor")]
    [InlineData("LiveColor")]
    public void Color_token_resolves_in_both_variants(string key)
    {
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            bool found = Application.Current!.TryGetResource(key, variant, out object? value);
            Assert.True(found, $"Color-Token '{key}' fehlt in {variant}.");
            Assert.IsType<Color>(value);
        }
    }
}
