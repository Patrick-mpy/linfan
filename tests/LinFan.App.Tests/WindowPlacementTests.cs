// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert die Off-Screen-Erkennung beim Wiederherstellen der Fensterposition: nur ausreichend sichtbare
/// Fenster werden akzeptiert, sonst startet die App zentriert (verhindert ein unsichtbares Fenster, wenn
/// der gespeicherte Monitor weg ist).
/// </summary>
public sealed class WindowPlacementTests
{
    private static readonly IReadOnlyList<PixelRect> SingleScreen = new[] { new PixelRect(0, 0, 1920, 1080) };

    private static readonly IReadOnlyList<PixelRect> DualScreen = new[]
    {
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(1920, 0, 1920, 1080), // zweiter Monitor rechts daneben
    };

    [Fact]
    public void Window_fully_inside_screen_is_visible() =>
        Assert.True(WindowPlacement.IsOnAnyScreen(new PixelRect(100, 100, 1000, 720), SingleScreen));

    [Fact]
    public void Window_on_second_monitor_is_visible() =>
        Assert.True(WindowPlacement.IsOnAnyScreen(new PixelRect(2200, 200, 1000, 720), DualScreen));

    [Fact]
    public void Window_completely_off_screen_is_not_visible() =>
        Assert.False(WindowPlacement.IsOnAnyScreen(new PixelRect(5000, 5000, 1000, 720), DualScreen));

    [Fact]
    public void Window_with_tiny_sliver_visible_is_rejected() =>
        // nur 10 px ragen rein (< minVisible 80) → als off-screen behandeln
        Assert.False(WindowPlacement.IsOnAnyScreen(new PixelRect(-990, 100, 1000, 720), SingleScreen));

    [Fact]
    public void Window_with_enough_edge_visible_is_accepted() =>
        // 200 px ragen rein (> minVisible 80) → akzeptiert
        Assert.True(WindowPlacement.IsOnAnyScreen(new PixelRect(-800, 100, 1000, 720), SingleScreen));
}
