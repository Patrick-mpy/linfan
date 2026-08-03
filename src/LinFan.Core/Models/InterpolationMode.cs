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
    /// der Stützpunkte erhält und – anders als eine naive natürliche Spline – nicht über deren
    /// Wertebereich hinausschwingt. Zusätzlich klemmt <see cref="LinFan.Core.Services.CurveEngine"/> das
    /// Ergebnis nach unten auf die lineare Verbindung (kein PWM-Dip unter die Sehne → nie weniger
    /// Kühlung als gezeichnet, kein Unterkühlungs-/Übertemp-Risiko).
    /// </summary>
    Spline = 1,
}
