// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Anzeige-abgeleitete Felder von <see cref="CalibrationStatus"/> für die geteilte Kalibrier-Karte
/// (Kopfzeile / Detailzeile / Fortschritt / Sichtbarkeit des Balkens).
/// </summary>
public sealed class CalibrationStatusTests
{
    private static CalibrationStatus Running(int pwm = 128, string? name = "CPU Fan") =>
        new("hwmon7/pwm1", CalibrationPhase.Measuring, pwm, 1500, Running: true, Done: false, StartPwm: null, FailReason: null, FanName: name);

    [Fact]
    public void DisplayName_PrefersFanName_FallsBackToId()
    {
        Assert.Equal("CPU Fan", Running(name: "CPU Fan").DisplayName);
        Assert.Equal("hwmon7/pwm1", Running(name: null).DisplayName);
    }

    [Fact]
    public void Running_Headline_NamesFan_DetailHasPhasePwmRpm_ProgressShown()
    {
        CalibrationStatus s = Running(pwm: 128);

        Assert.Equal("Kalibriere CPU Fan", s.Headline);
        Assert.Contains("Messe 50 %", s.Detail);
        Assert.Contains("pwm 128", s.Detail);
        Assert.Contains("1500", s.Detail);
        Assert.InRange(s.Progress, 49, 51); // 128/255 ≈ 50 %
        Assert.True(s.ShowProgress);
    }

    [Fact]
    public void Done_Headline_ShowsStartPwm_NoDetail_NoProgressBar()
    {
        var s = new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Done, 0, 0,
            Running: false, Done: true, StartPwm: 96, FailReason: null, FanName: "CPU Fan");

        Assert.Contains("96", s.Headline);
        Assert.Equal("", s.Detail);     // Detail nur während des Laufs
        Assert.False(s.ShowProgress);   // Balken nur während des Laufs
    }

    [Fact]
    public void Error_Headline_ShowsError_NoProgressBar()
    {
        var s = new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Failed, 0, 0,
            Running: false, Done: false, StartPwm: null, FailReason: CalibrationFailReason.OverTemperature,
            OverTempC: 95, OverLimitC: 90, FanName: "CPU Fan");

        Assert.Contains("Übertemperatur", s.Headline);
        Assert.False(s.ShowProgress);
    }
}
