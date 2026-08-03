// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;

namespace LinFan.App.Services;

/// <summary>
/// Einheitlicher Tooltip-Text des „bereits kalibriert"-Badges (Dashboard <b>und</b> Geräte-Tab) aus dem
/// Anlauf-PWM: <c>StartPwm == 255</c> = „kein sicherer Anlauf gefunden" (Fail-Safe), sonst der Anlaufpunkt
/// in Prozent (konsistent mit der übrigen %-Anzeige). Eine Stelle, damit beide Ansichten denselben Text zeigen.
/// </summary>
internal static class CalibrationBadge
{
    public static string Hint(byte startPwm) =>
        startPwm >= 255
            ? Localizer.Instance["CalibrationBadge.NoSafeStart"]
            : Localizer.Instance.Format("CalibrationBadge.StartAt", PwmScale.ToPercent(startPwm));
}
