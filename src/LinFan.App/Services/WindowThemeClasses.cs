// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;

namespace LinFan.App.Services;

/// <summary>
/// Marks every window with the theme it shows when that theme differs from light and dark in more than colours:
/// <see cref="Terminal"/> or <see cref="HighContrast"/>. Theme/Layout.axaml hangs what a resource value cannot express
/// on these classes - frames around cards and chips, the wider focus ring, square switches.
/// </summary>
public static class WindowThemeClasses
{
    public const string Terminal = "themeTerminal";
    public const string HighContrast = "themeHighContrast";

    private static bool _attached;

    /// <summary>
    /// Registers one class handler for all windows - main window, setup assistant and dialogs alike, including those a
    /// later theme switch finds open. Idempotent.
    /// </summary>
    public static void AttachAll()
    {
        if (_attached)
            return;
        _attached = true;

        Control.LoadedEvent.AddClassHandler<Window>((window, _) =>
        {
            Apply(window);
            // Loaded fires again when a window is re-shown (tray restore); subscribing idempotently keeps handlers from
            // stacking up.
            window.ActualThemeVariantChanged -= OnThemeVariantChanged;
            window.ActualThemeVariantChanged += OnThemeVariantChanged;
        });
    }

    private static void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
            Apply(window);
    }

    private static void Apply(Window window)
    {
        ThemeChoice shown = ThemeVariantMap.Concrete(window.ActualThemeVariant);
        window.Classes.Set(Terminal, shown == ThemeChoice.Terminal);
        window.Classes.Set(HighContrast, shown == ThemeChoice.HighContrast);
    }
}
