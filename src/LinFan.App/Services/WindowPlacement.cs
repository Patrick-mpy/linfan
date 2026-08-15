// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;

namespace LinFan.App.Services;

/// <summary>
/// Reine Geometrie-Helfer für das Wiederherstellen der Fensterposition - ohne Fenster/Avalonia-Laufzeit
/// testbar (<see cref="PixelRect"/> ist ein Werttyp). Verhindert, dass ein Fenster off-screen startet,
/// wenn der gespeicherte Monitor weg ist (Notebook abgesteckt, Auflösung geändert).
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// True, wenn das Fenster-Rechteck mit mindestens <paramref name="minVisible"/> px Kantenüberlappung auf
    /// einem der Bildschirme sichtbar wäre. Sonst sollte die gespeicherte Position verworfen und zentriert
    /// gestartet werden.
    /// </summary>
    public static bool IsOnAnyScreen(PixelRect window, IReadOnlyList<PixelRect> screens, int minVisible = 80)
    {
        foreach (PixelRect screen in screens)
        {
            PixelRect overlap = screen.Intersect(window);
            if (overlap.Width >= minVisible && overlap.Height >= minVisible)
                return true;
        }
        return false;
    }
}
