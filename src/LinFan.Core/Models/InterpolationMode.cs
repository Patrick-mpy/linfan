// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Wie <see cref="LinFan.Core.Services.CurveEngine"/> zwischen den Stützpunkten einer Kurve interpoliert.
/// <see cref="Linear"/> ist der sichere Default: vorhersagbar, monoton, ohne Überschwingen.
/// </summary>
public enum InterpolationMode
{
    /// <summary>Geradlinige Verbindung zwischen benachbarten Stützpunkten (sicherer Default).</summary>
    Linear = 0,

    /// <summary>
    /// Monotone kubische Hermite-Interpolation (Fritsch-Carlson). Weicher Verlauf, der die Monotonie
    /// der Stützpunkte erhält und - anders als eine naive natürliche Spline - nicht über deren
    /// Wertebereich hinausschwingt: Zwischen zwei Punkten bleibt der Wert stets im Bereich der beiden
    /// Punkte, jeder Stützpunkt wird exakt getroffen.
    /// </summary>
    Spline = 1,

    /// <summary>
    /// Stufen: Die Punkte wirken als Schwellwerte - zwischen zwei Stützpunkten hält die Kurve den Wert
    /// des unteren Punkts und springt erst beim Erreichen des nächsten Punkts (Verhalten klassischer
    /// BIOS-Lüftersteuerungen; vermeidet ständiges Nachregeln).
    /// </summary>
    Step = 2,
}
