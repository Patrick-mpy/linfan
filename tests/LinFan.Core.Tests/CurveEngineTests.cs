// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class CurveEngineTests
{
    private static Curve Sample() => new("test", new[]
    {
        new CurvePoint(30, 20),
        new CurvePoint(50, 50),
        new CurvePoint(80, 100),
    });

    private static Curve SampleSpline() => Sample() with { InterpolationMode = InterpolationMode.Spline };

    private static Curve Stair() => new("stair", new[]
    {
        new CurvePoint(30, 20),
        new CurvePoint(50, 20),   // flaches Segment 30..50
        new CurvePoint(80, 100),
    }, InterpolationMode.Spline);

    [Fact]
    public void Evaluate_BelowFirstPoint_ClampsToFirst() =>
        Assert.Equal(20, CurveEngine.Evaluate(Sample(), 10), 3);

    [Fact]
    public void Evaluate_AboveLastPoint_ClampsToLast() =>
        Assert.Equal(100, CurveEngine.Evaluate(Sample(), 95), 3);

    [Theory]
    [InlineData(40, 35)]   // Mitte zwischen (30,20) und (50,50)
    [InlineData(50, 50)]   // genau auf einem Stützpunkt
    [InlineData(65, 75)]   // Mitte zwischen (50,50) und (80,100)
    public void Evaluate_InterpolatesLinearly(double temp, double expected) =>
        Assert.Equal(expected, CurveEngine.Evaluate(Sample(), temp), 3);

    [Fact]
    public void Evaluate_UnsortedPoints_StillInterpolates()
    {
        var unsorted = new Curve("u", new[] { new CurvePoint(80, 100), new CurvePoint(30, 20), new CurvePoint(50, 50) });
        Assert.Equal(35, CurveEngine.Evaluate(unsorted, 40), 3);
    }

    [Fact]
    public void Evaluate_EmptyCurve_FailsSafeTo100() =>
        Assert.Equal(100, CurveEngine.Evaluate(new Curve("empty", Array.Empty<CurvePoint>()), 40), 3);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 255)]
    [InlineData(50, 128)]    // round(0.5 * 255) = 128 (AwayFromZero)
    [InlineData(150, 255)]   // > 100 % wird geclamped
    public void PercentToPwm_MapsAndClampsRange(double percent, byte expected) =>
        Assert.Equal(expected, CurveEngine.PercentToPwm(percent));

    // ----- Spline (monotone kubische Hermite, Fritsch-Carlson) -----

    [Theory]
    [InlineData(30, 20)]
    [InlineData(50, 50)]
    [InlineData(80, 100)]
    public void Spline_HitsControlPointsExactly(double temp, double expected) =>
        Assert.Equal(expected, CurveEngine.Evaluate(SampleSpline(), temp), 3);

    [Fact]
    public void Spline_BelowFirstPoint_ClampsToFirst() =>
        Assert.Equal(20, CurveEngine.Evaluate(SampleSpline(), 10), 3);

    [Fact]
    public void Spline_AboveLastPoint_ClampsToLast() =>
        Assert.Equal(100, CurveEngine.Evaluate(SampleSpline(), 95), 3);

    [Fact]
    public void Spline_FlatSegment_NoDipOrBump()
    {
        // Treppe (30,20),(50,20),(80,100): das flache Segment 30..50 muss exakt bei 20 bleiben,
        // kein Unterschwingen (< 20) und kein Überschwingen (> 20).
        for (double t = 30; t <= 50; t += 0.25)
            Assert.Equal(20.0, CurveEngine.Evaluate(Stair(), t), 3);
    }

    [Fact]
    public void Spline_NoOvershoot_StaysBetweenNeighbourPwms()
    {
        // Über die gesamte Treppe darf nie ein Wert außerhalb der Spanne der einschließenden Stützpunkte liegen.
        var pts = new[] { new CurvePoint(30, 20), new CurvePoint(50, 20), new CurvePoint(80, 100) };
        var curve = new Curve("stair", pts, InterpolationMode.Spline);

        for (double t = 30; t <= 80; t += 0.1)
        {
            double v = CurveEngine.Evaluate(curve, t);
            (double lo, double hi) = EnclosingRange(pts, t);
            Assert.InRange(v, lo - 1e-9, hi + 1e-9);
        }
    }

    [Fact]
    public void Spline_MonotoneInput_ProducesNonDecreasingOutput()
    {
        var curve = new Curve("mono", new[]
        {
            new CurvePoint(20, 0),
            new CurvePoint(40, 10),
            new CurvePoint(55, 60),
            new CurvePoint(70, 65),
            new CurvePoint(90, 100),
        }, InterpolationMode.Spline);

        double prev = double.NegativeInfinity;
        for (double t = 20; t <= 90; t += 0.1)
        {
            double v = CurveEngine.Evaluate(curve, t);
            Assert.True(v >= prev - 1e-9, $"fiel bei {t}: {v} < {prev}");
            prev = v;
        }
    }

    [Fact]
    public void Spline_ConvexSegment_SmoothsBelowChord_ButNeverBelowLowerPoint()
    {
        // Auf konvexen Abschnitten liegt die glatte Spline unter der linearen Verbindung — das ist der
        // sichtbare Effekt des Modus (die frühere Sehnen-Klammer machte Spline ≈ Linear und damit
        // wirkungslos). Sicherheitsgrenze bleibt: nie unter den unteren einschließenden Stützpunkt.
        var pts = new[]
        {
            new CurvePoint(30, 20),
            new CurvePoint(40, 22),
            new CurvePoint(50, 25),
            new CurvePoint(80, 100),
        };
        var linear = new Curve("c", pts, InterpolationMode.Linear);
        var spline = new Curve("c", pts, InterpolationMode.Spline);

        for (double t = 30; t <= 80; t += 0.1)
        {
            double spl = CurveEngine.Evaluate(spline, t);
            (double lo, double hi) = EnclosingRange(pts, t);
            Assert.InRange(spl, lo - 1e-9, hi + 1e-9);
        }

        // Im konvexen Segment (50..80) muss die Glättung tatsächlich unter der Sehne liegen.
        double lin60 = CurveEngine.Evaluate(linear, 60);
        double spl60 = CurveEngine.Evaluate(spline, 60);
        Assert.True(spl60 < lin60 - 1.0, $"Spline glättet nicht: {spl60} ≈ {lin60}");
    }

    [Fact]
    public void Spline_ConvexDip_MatchesMonotoneHermite()
    {
        // Ehemaliger Sehnen-Klammer-Fall: die reine Fritsch-Carlson-Spline ergibt hier ~42,8 %
        // (linear wäre 50 %) — genau diese Rundung ist jetzt gewollt und muss stabil bleiben.
        var spline = new Curve("c", new[]
        {
            new CurvePoint(30, 20),
            new CurvePoint(40, 22),
            new CurvePoint(50, 25),
            new CurvePoint(80, 100),
        }, InterpolationMode.Spline);

        double v = CurveEngine.Evaluate(spline, 60);
        Assert.InRange(v, 25.0, 50.0 - 1.0); // deutlich unter der Sehne, nie unter dem unteren Punkt
    }

    [Fact]
    public void Spline_ClampsTo0And100()
    {
        // Steile Punkte, die ohne Clamp rechnerisch < 0 bzw. > 100 erzeugen könnten.
        var curve = new Curve("steep", new[]
        {
            new CurvePoint(30, 0),
            new CurvePoint(31, 0),
            new CurvePoint(60, 100),
            new CurvePoint(61, 100),
        }, InterpolationMode.Spline);

        for (double t = 30; t <= 61; t += 0.1)
        {
            double v = CurveEngine.Evaluate(curve, t);
            Assert.InRange(v, 0.0, 100.0);
        }
    }

    [Fact]
    public void Spline_EmptyCurve_FailsSafeTo100() =>
        Assert.Equal(100, CurveEngine.Evaluate(new Curve("empty", Array.Empty<CurvePoint>(), InterpolationMode.Spline), 40), 3);

    [Fact]
    public void Spline_SinglePoint_ReturnsThatPoint() =>
        Assert.Equal(42, CurveEngine.Evaluate(new Curve("one", new[] { new CurvePoint(50, 42) }, InterpolationMode.Spline), 40), 3);

    [Fact]
    public void Spline_NonMonotoneInput_StaysWithinEnclosingPoints()
    {
        // Nicht-monotone Kurve (Delle am lokalen Minimum bei 40 °C): ohne das Mit-Nullen von
        // alpha/beta im Fritsch-Carlson-Limiter reanimierte der Radius-3-Zweig die genullte
        // Tangente mit falschem Vorzeichen und die Spline fiel unter den unteren Stützpunkt
        // (~18,5 % bei unterem Punkt 20 %). Die Garantie „Wert bleibt zwischen den
        // einschließenden Punkten" muss auch hier gelten.
        var pts = new[]
        {
            new CurvePoint(30, 80),
            new CurvePoint(40, 20),
            new CurvePoint(50, 25),
        };
        var curve = new Curve("dip", pts, InterpolationMode.Spline);

        for (double t = 30; t <= 50; t += 0.05)
        {
            double v = CurveEngine.Evaluate(curve, t);
            (double lo, double hi) = EnclosingRange(pts, t);
            Assert.InRange(v, lo - 1e-9, hi + 1e-9);
        }
    }

    [Fact]
    public void Spline_DuplicateTemperature_IsRobust()
    {
        var curve = new Curve("dup", new[]
        {
            new CurvePoint(30, 20),
            new CurvePoint(50, 40),
            new CurvePoint(50, 60),   // doppelte Temperatur (span <= 0)
            new CurvePoint(80, 100),
        }, InterpolationMode.Spline);

        double v = CurveEngine.Evaluate(curve, 50);
        Assert.InRange(v, 20.0, 100.0); // kein NaN/Infinity, im plausiblen Bereich
        Assert.False(double.IsNaN(v));
    }

    // ----- Step (Stufen: Punkte wirken als Schwellwerte) -----

    private static Curve SampleStep() => Sample() with { InterpolationMode = InterpolationMode.Step };

    [Theory]
    [InlineData(30, 20)]    // exakt auf dem Punkt
    [InlineData(49.9, 20)]  // hält den unteren Wert bis kurz vor dem nächsten Punkt
    [InlineData(50, 50)]    // springt beim Erreichen des Punkts
    [InlineData(65, 50)]
    [InlineData(80, 100)]
    public void Step_HoldsLowerPoint_UntilNextIsReached(double temp, double expected) =>
        Assert.Equal(expected, CurveEngine.Evaluate(SampleStep(), temp), 3);

    [Fact]
    public void Step_BelowFirstPoint_ClampsToFirst() =>
        Assert.Equal(20, CurveEngine.Evaluate(SampleStep(), 10), 3);

    [Fact]
    public void Step_AboveLastPoint_ClampsToLast() =>
        Assert.Equal(100, CurveEngine.Evaluate(SampleStep(), 95), 3);

    [Fact]
    public void Step_MonotoneInput_ProducesNonDecreasingOutput()
    {
        var curve = Sample() with { InterpolationMode = InterpolationMode.Step };
        double prev = double.NegativeInfinity;
        for (double t = 20; t <= 90; t += 0.1)
        {
            double v = CurveEngine.Evaluate(curve, t);
            Assert.True(v >= prev - 1e-9, $"fiel bei {t}: {v} < {prev}");
            prev = v;
        }
    }

    [Fact]
    public void Step_EmptyCurve_FailsSafeTo100() =>
        Assert.Equal(100, CurveEngine.Evaluate(new Curve("empty", Array.Empty<CurvePoint>(), InterpolationMode.Step), 40), 3);

    [Fact]
    public void Step_SinglePoint_ReturnsThatPoint() =>
        Assert.Equal(42, CurveEngine.Evaluate(new Curve("one", new[] { new CurvePoint(50, 42) }, InterpolationMode.Step), 60), 3);

    [Fact]
    public void Step_DuplicateTemperature_TakesLaterPoint()
    {
        // Wie bei der Spline gewinnt bei doppelter Temperatur deterministisch der spätere Punkt.
        var curve = new Curve("dup", new[]
        {
            new CurvePoint(30, 20),
            new CurvePoint(50, 40),
            new CurvePoint(50, 60),
            new CurvePoint(80, 100),
        }, InterpolationMode.Step);

        Assert.Equal(60, CurveEngine.Evaluate(curve, 50), 3);
        Assert.Equal(60, CurveEngine.Evaluate(curve, 60), 3);
    }

    // ----- Regression: Linear-Modus unverändert (Default = Linear) -----

    [Theory]
    [InlineData(40, 35)]
    [InlineData(50, 50)]
    [InlineData(65, 75)]
    public void Linear_IsDefaultMode_StillExact(double temp, double expected) =>
        Assert.Equal(expected, CurveEngine.Evaluate(Sample(), temp), 3);

    [Fact]
    public void Curve_DefaultInterpolationMode_IsLinear() =>
        Assert.Equal(InterpolationMode.Linear, new Curve("c", Array.Empty<CurvePoint>()).InterpolationMode);

    /// <summary>Spanne [min, max] der Percent-Werte der das gegebene <paramref name="temp"/> einschließenden Stützpunkte.</summary>
    private static (double Lo, double Hi) EnclosingRange(CurvePoint[] pts, double temp)
    {
        for (int i = 0; i < pts.Length - 1; i++)
        {
            if (temp >= pts[i].TemperatureC && temp <= pts[i + 1].TemperatureC)
            {
                double a = pts[i].Percent, b = pts[i + 1].Percent;
                return (Math.Min(a, b), Math.Max(a, b));
            }
        }

        return (pts[^1].Percent, pts[^1].Percent);
    }
}
