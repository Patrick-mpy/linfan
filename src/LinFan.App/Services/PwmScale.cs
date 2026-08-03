// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// Einheitliche Umrechnung zwischen Hardware-PWM (0–255) und Anzeige-Prozent (0–100).
/// Beide Richtungen <b>runden</b> — die Anzeige wäre sonst inkonsistent: eine trunkierende
/// Ganzzahl-Division (<c>pwm * 100 / 255</c>) zeigt bis zu 1 % weniger als die rundende
/// Slider-Anzeige desselben Lüfters. Für ganzzahlige PWM-Werte fällt <c>pwm*100/255</c> nie
/// genau auf einen Mittelpunkt (.5), daher ist die Rundungsart irrelevant.
/// </summary>
internal static class PwmScale
{
    /// <summary>PWM (0–255) → gerundete Prozent (0–100).</summary>
    public static int ToPercent(byte pwm) => (int)Math.Round(pwm * 100.0 / 255.0);

    /// <summary>Prozent (0–100) → PWM (0–255), gerundet und geklemmt.</summary>
    public static byte ToPwm(double percent) =>
        (byte)Math.Clamp(Math.Round(percent * 255.0 / 100.0), 0, 255);
}
