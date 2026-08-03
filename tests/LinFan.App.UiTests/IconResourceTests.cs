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
    [InlineData("IconGauge")]
    [InlineData("IconCheckCircle")]
    public void Icon_geometry_resolves(string key)
    {
        Assert.NotNull(Application.Current);
        bool found = Application.Current!.TryGetResource(key, null, out object? value);
        Assert.True(found, $"Icon-Ressource '{key}' nicht gefunden.");
        Assert.IsAssignableFrom<Geometry>(value);
    }
}
