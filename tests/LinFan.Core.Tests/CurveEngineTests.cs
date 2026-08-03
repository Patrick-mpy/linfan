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
    public void Spline_NeverBelowLinearChord_OnConvexCurve()
    {
        // Hardware-konservative Garantie: Auf konvexen Abschnitten läge die glatte Spline rechnerisch
        // unter der linearen Verbindung (= weniger Kühlung als gezeichnet). Der CurveEngine klemmt das
        // nach unten auf die Sehne, also muss Spline ≥ Linear über den gesamten Bereich gelten.
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
            double lin = CurveEngine.Evaluate(linear, t);
            double spl = CurveEngine.Evaluate(spline, t);
            Assert.True(spl >= lin - 1e-9, $"Spline unter Sehne bei {t}: {spl} < {lin}");
        }
    }

    [Fact]
    public void Spline_ConvexDip_IsClampedToLinearChord()
    {
        // Reproduzierter Audit-Fall: ohne Schranke ergäbe die Spline hier ~42,8 % (≈7 pp unter linear).
        // Mit der Schranke muss exakt der lineare Sehnenwert (50 %) herauskommen.
        var spline = new Curve("c", new[]
        {
            new CurvePoint(30, 20),
            new CurvePoint(40, 22),
            new CurvePoint(50, 25),
            new CurvePoint(80, 100),
        }, InterpolationMode.Spline);

        Assert.Equal(50.0, CurveEngine.Evaluate(spline, 60), 3);
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
