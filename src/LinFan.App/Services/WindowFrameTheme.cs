// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Styling;

namespace LinFan.App.Services;

/// <summary>
/// Paints the native window title bar in the app's theme variant on Windows.
/// <para>
/// Avalonia binds <c>ActualThemeVariant</c> to the platform's frame theme itself, but that binding does
/// not reach the frame here - a dark window keeps a light system title bar. There is no public API to
/// re-trigger it, so the DWM attribute is set directly.
/// </para>
/// <para>
/// One of the few pieces of platform code outside <c>LinFan.Hardware.*</c> (see also
/// <see cref="SingleInstanceGuard"/>): the rule there is about hardware access behind
/// <c>ISensorBackend</c>/<c>IFanController</c>, while this is pure window decoration with no domain
/// meaning. Guarded at runtime instead of by <c>#if</c>, so the same binary stays correct on
/// Linux/macOS, where it simply does nothing.
/// </para>
/// </summary>
public static class WindowFrameTheme
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE - supported from Windows 10 build 18985 / Windows 11.
    private const int DwmwaUseImmersiveDarkMode = 20;

    private const uint WmNcActivate = 0x0086;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004,
        SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// Registers a single class handler that themes every window the app opens - main window, onboarding
    /// and the dialogs alike, without each view having to know about the platform.
    /// </summary>
    public static void AttachAll()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Control.LoadedEvent.AddClassHandler<Window>((window, _) =>
        {
            // Repaint here too, not just on a later theme switch: Loaded runs after the window is on
            // screen, and a modal dialog is activated by Windows the moment it appears - so its one
            // non-client paint happens before the attribute is set, and no further activation change
            // follows while it lives (the theme cannot be switched behind a locked owner). Without the
            // forced redraw a dialog keeps a light title bar for its whole lifetime.
            Apply(window, repaint: true);
            // Loaded fires again when a window is re-shown (tray restore); subscribing idempotently
            // keeps that from stacking up handlers.
            window.ActualThemeVariantChanged -= OnThemeVariantChanged;
            window.ActualThemeVariantChanged += OnThemeVariantChanged;
        });
    }

    private static void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
            Apply(window, repaint: true);
    }

    private static void Apply(Window window, bool repaint)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == nint.Zero)
            return;

        // ActualThemeVariant, not the requested one: in "system" mode it already carries what the OS resolved to.
        int dark = window.ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            if (repaint)
                RepaintFrame(window, hwnd);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows without dwmapi or without the attribute → light title bar. Cosmetic; never
            // worth failing a window over.
        }
    }

    /// <summary>
    /// Forces the title bar of an already-visible window to redraw. Windows 10 only repaints the
    /// non-client area on an activation change, so a newly set attribute otherwise leaves the old bar
    /// standing until the window loses and regains focus - replay exactly that activation change.
    /// </summary>
    private static void RepaintFrame(Window window, nint hwnd)
    {
        SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        // Only for the focused window: faking the activation on a background window would leave it
        // drawn as active until the next real focus change.
        if (window.IsActive)
        {
            SendMessage(hwnd, WmNcActivate, 0, 0);
            SendMessage(hwnd, WmNcActivate, 1, 0);
        }
    }
}
