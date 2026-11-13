// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace LinFan.App.UiTests;

/// <summary>
/// Sichert die dependency-freien PathIcon-Geometrien (Icons.axaml, in App.axaml gemergt): jede muss als
/// gültige <see cref="Geometry"/> auflösen. Eine kaputte StreamGeometry würde sonst erst zur Laufzeit
/// werfen, sobald der erste Button mit diesem Icon realisiert wird.
/// </summary>
public class IconResourceTests
{
    [AvaloniaTheory]
    [InlineData("IconSave")]
    [InlineData("IconPlus")]
    [InlineData("IconDelete")]
    [InlineData("IconRefresh")]
    [InlineData("IconCopy")]
    [InlineData("IconRename")]
    [InlineData("IconEye")]
    [InlineData("IconEyeOff")]
    [InlineData("IconThermometer")]
    [InlineData("IconFan")]
    [InlineData("IconSearch")]
    [InlineData("IconChevronDown")]
    [InlineData("IconChevronUp")]
    [InlineData("IconMinus")]
    [InlineData("IconUndo")]
    [InlineData("IconRedo")]
    [InlineData("IconRestore")]
    [InlineData("IconGrip")]
    [InlineData("IconInfo")]
    [InlineData("IconAlert")]
    [InlineData("IconMore")]
    [InlineData("IconGitHub")]
    [InlineData("IconGauge")]
    [InlineData("IconCheckCircle")]
    [InlineData("IconAlertCircle")]
    [InlineData("IconPulse")]
    [InlineData("IconSwap")]
    [InlineData("IconCheck")]
    [InlineData("IconGlobe")]
    [InlineData("IconChartLine")]
    [InlineData("IconCog")]
    [InlineData("IconWind")]
    [InlineData("IconPalette")]
    [InlineData("IconWindow")]
    [InlineData("IconArchive")]
    [InlineData("IconClose")]
    [InlineData("IconSparkle")]
    [InlineData("IconInterpolationLinear")]
    [InlineData("IconInterpolationSpline")]
    [InlineData("IconInterpolationStep")]
    public void Icon_geometry_resolves(string key)
    {
        Assert.NotNull(Application.Current);
        bool found = Application.Current!.TryGetResource(key, null, out object? value);
        Assert.True(found, $"Icon-Ressource '{key}' nicht gefunden.");
        Assert.IsAssignableFrom<Geometry>(value);
    }

    /// <summary>Every interpolation option names an icon that exists (the key is built from the enum value).</summary>
    [AvaloniaFact]
    public void Every_interpolation_option_has_its_icon()
    {
        foreach (LinFan.App.Controllers.InterpolationOption option in LinFan.App.Controllers.InterpolationOption.All)
            Assert.True(Application.Current!.TryGetResource(option.IconKey, null, out object? value) && value is Geometry,
                option.IconKey);
    }
}
