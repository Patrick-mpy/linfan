// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class TemperatureSmootherTests
{
    private const double Window = 3.0;

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void Smooth_NonPositiveWindow_IsPassThrough(double window)
    {
        var smoother = new TemperatureSmoother(new FakeTimeProvider());

        Assert.Equal(45, smoother.Smooth("c", 45, window));
        Assert.Equal(75, smoother.Smooth("c", 75, window));   // no averaging at all - old behaviour
    }

    [Fact]
    public void Smooth_WindowBeyondTheCap_IsClamped_AndNeverThrows()
    {
        // Kurven innerhalb eines Profils erreichen den Regel-Loop, ohne den ConfigSanitizer zu passieren
        // (ProfileService.Apply tauscht die Kurvenliste erst danach ein) - der Filter muss selbst begrenzen.
        // Ohne Clamp würfe TimeSpan.FromSeconds hier, und der Lüfter bliebe auf seinem letzten PWM stehen.
        var time = new FakeTimeProvider();
        var smoother = new TemperatureSmoother(time);

        smoother.Smooth("c", 90, 1e12);
        time.Advance(TemperatureSmoother.MaxWindowSeconds + 1);

        Assert.Equal(50, smoother.Smooth("c", 50, 1e12), 6);   // altes Sample ausgealtert statt Wurf
    }

    [Fact]
    public void Smooth_FirstSample_ReturnsRawValue()
    {
        var smoother = new TemperatureSmoother(new FakeTimeProvider());

        // Cold start must not invent a value: the very first reading is used as-is.
        Assert.Equal(45, smoother.Smooth("c", 45, Window));
    }

    [Fact]
    public void Smooth_AttenuatesSingleSpike()
    {
        var time = new FakeTimeProvider();
        var smoother = new TemperatureSmoother(time);

        double last = 0;
        for (int i = 0; i < 3; i++)                      // idle baseline at 45 °C, one sample per second
        {
            last = smoother.Smooth("c", 45, Window);
            time.Advance(1.0);
        }
        Assert.Equal(45, last);

        // A 30 °C spike over four samples inside the window arrives as 7.5 °C - that is the whole point.
        Assert.Equal(52.5, smoother.Smooth("c", 75, Window), 6);
    }

    [Fact]
    public void Smooth_SustainedLoad_ReachesFullValue()
    {
        var time = new FakeTimeProvider();
        var smoother = new TemperatureSmoother(time);

        smoother.Smooth("c", 45, Window);
        time.Advance(1.0);

        double value = 0;
        for (int i = 0; i < 5; i++)                      // real load, not a spike: the mean catches up
        {
            value = smoother.Smooth("c", 75, Window);
            time.Advance(1.0);
        }

        Assert.Equal(75, value);
    }

    [Fact]
    public void Smooth_DropsSamplesOlderThanWindow()
    {
        var time = new FakeTimeProvider();
        var smoother = new TemperatureSmoother(time);

        smoother.Smooth("c", 90, Window);
        time.Advance(10.0);                              // gap longer than the window (stalled loop, resume)

        // Nothing survives from before the gap, so the reading after it is raw again.
        Assert.Equal(50, smoother.Smooth("c", 50, Window));
    }

    [Fact]
    public void Smooth_Nan_PassesThroughWithoutPoisoningTheBuffer()
    {
        var time = new FakeTimeProvider();
        var smoother = new TemperatureSmoother(time);

        smoother.Smooth("c", 50, Window);
        time.Advance(1.0);

        Assert.True(double.IsNaN(smoother.Smooth("c", double.NaN, Window)));
        time.Advance(1.0);

        // Had the NaN entered the buffer, every later mean would be NaN and the fan would be skipped forever.
        Assert.Equal(50, smoother.Smooth("c", 50, Window));
    }

    [Fact]
    public void Smooth_KeepsOneBufferPerCurve()
    {
        var time = new FakeTimeProvider();
        var smoother = new TemperatureSmoother(time);

        smoother.Smooth("cpu", 40, Window);
        smoother.Smooth("gpu", 80, Window);
        time.Advance(1.0);

        Assert.Equal(45, smoother.Smooth("cpu", 50, Window), 6);   // (40+50)/2
        Assert.Equal(85, smoother.Smooth("gpu", 90, Window), 6);   // (80+90)/2
    }

    [Fact]
    public void Reset_DiscardsBufferedSamples()
    {
        var time = new FakeTimeProvider();
        var smoother = new TemperatureSmoother(time);

        smoother.Smooth("c", 90, Window);
        time.Advance(1.0);
        smoother.Reset();

        Assert.Equal(50, smoother.Smooth("c", 50, Window));
    }
}
