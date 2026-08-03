// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;

namespace LinFan.App.UiTests;

/// <summary>
/// Sichert die Layout-Style-Klassen (Theme/Layout.axaml, in App.axaml inkludiert): jede Border-Klasse muss
/// zur Laufzeit ihre CornerRadius-/Padding-/Background-Setter anwenden. Ein vertippter Selector oder ein
/// nicht inkludiertes Styles-File würde die Karten „nackt" rendern, ohne dass der Build bricht.
/// </summary>
public class LayoutStyleTests
{
    [AvaloniaTheory]
    [InlineData("card", 12, 18, 18, 18, 18)]
    [InlineData("panel", 14, 20, 20, 20, 20)]
    [InlineData("bar", 10, 12, 8, 12, 8)]
    [InlineData("chip", 6, 4, 1, 4, 1)]
    [InlineData("itemCard", 10, 12, 12, 12, 12)]
    [InlineData("inset", 6, 8, 2, 8, 2)]
    public void Border_class_applies_corner_padding_and_background(
        string cls, double radius, double left, double top, double right, double bottom)
    {
        var border = new Border { Classes = { cls } };
        var window = new Window { Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new CornerRadius(radius), border.CornerRadius);
        Assert.Equal(new Thickness(left, top, right, bottom), border.Padding);
        Assert.IsAssignableFrom<IBrush>(border.Background); // DynamicResource aufgelöst → Theme-fest
    }

    [AvaloniaTheory]
    [InlineData("bar", 0, 0, 0, 12)]
    [InlineData("itemCard", 0, 3, 0, 3)]
    public void Role_class_carries_its_margin(string cls, double left, double top, double right, double bottom)
    {
        var border = new Border { Classes = { cls } };
        var window = new Window { Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Thickness(left, top, right, bottom), border.Margin);
    }

    /// <summary>
    /// Die Geräte-Listenzeile (Einstellungen → Sensoren/Lüfter) trägt statt einer gefüllten Karte nur eine
    /// untere Hairline: BorderThickness unten = 1, BorderBrush aus dem Divider-Token (Theme-fest aufgelöst).
    /// </summary>
    [AvaloniaFact]
    public void DeviceRow_has_bottom_divider()
    {
        var border = new Border { Classes = { "deviceRow" } };
        var window = new Window { Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Thickness(0, 0, 0, 1), border.BorderThickness);
        Assert.IsAssignableFrom<IBrush>(border.BorderBrush);
        Assert.Equal(new Thickness(8, 9, 8, 9), border.Padding);
    }
}
