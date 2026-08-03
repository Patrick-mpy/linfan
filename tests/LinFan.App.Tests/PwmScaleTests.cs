// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert die einheitliche PWM↔Prozent-Umrechnung. Der Kern-Regressionsfall: vor dem Fix zeigte der
/// „pwm … · …%"-Text die trunkierte Ganzzahl-Division (<c>pwm*100/255</c>), während die Slider-Anzeige
/// (<c>pwm*100.0/255</c> mit Format <c>{0:0}</c>) rundet — Differenz bis 1 % auf derselben Lüfterkarte.
/// </summary>
public sealed class PwmScaleTests
{
    [Theory]
    [InlineData((byte)0, 0)]
    [InlineData((byte)255, 100)]
    [InlineData((byte)128, 50)]   // 50.196 → 50
    [InlineData((byte)130, 51)]   // 50.98  → 51 (alt: 50 durch Trunkierung)
    public void ToPercent_rounds_to_nearest(byte pwm, int expected) =>
        Assert.Equal(expected, PwmScale.ToPercent(pwm));

    [Fact]
    public void ToPercent_matches_slider_display_basis_for_every_pwm()
    {
        // Die Slider-Anzeige formatiert pwm*100.0/255 mit „{0:0}" (rundet). ToPercent muss exakt dasselbe
        // liefern — sonst weichen Text und Slider auf der Karte voneinander ab. Über den gesamten Bereich prüfen.
        for (int pwm = 0; pwm <= 255; pwm++)
        {
            int sliderDisplay = (int)Math.Round((double)(pwm * 100.0 / 255.0));
            Assert.Equal(sliderDisplay, PwmScale.ToPercent((byte)pwm));
        }
    }

    [Theory]
    [InlineData(0.0, (byte)0)]
    [InlineData(100.0, (byte)255)]
    [InlineData(50.0, (byte)128)]   // 127.5 → 128
    public void ToPwm_rounds_and_clamps(double percent, byte expected) =>
        Assert.Equal(expected, PwmScale.ToPwm(percent));

    [Theory]
    [InlineData(-10.0, (byte)0)]
    [InlineData(150.0, (byte)255)]
    public void ToPwm_clamps_out_of_range(double percent, byte expected) =>
        Assert.Equal(expected, PwmScale.ToPwm(percent));
}
