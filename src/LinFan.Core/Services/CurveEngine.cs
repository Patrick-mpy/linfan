// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Rechnet Temperatur → Lüfterleistung. Interpolation zwischen den Stützpunkten (linear oder monotone
/// Spline), Clamping unterhalb des ersten bzw. oberhalb des letzten Punkts. Reine, deterministische,
/// seiteneffektfreie Logik ohne Hardware (deshalb hier im Core und voll unit-testbar).
/// </summary>
public static class CurveEngine
{
    /// <summary>Interpoliert die Leistung (0..100 %) für eine Temperatur in °C.</summary>
    public static double Evaluate(Curve curve, double temperatureC)
    {
        ArgumentNullException.ThrowIfNull(curve);

        var points = curve.Points;
        if (points is null || points.Count == 0)
            return 100.0; // Fail-Safe: ohne Kurve volle Leistung

        // Nur bei tatsächlich unsortierter Eingabe defensiv umsortieren. Der Hot-Path (Regel-Tick wertet
        // jede Kurve je Lüfter aus) liefert die Punkte bereits aufsteigend (in der Config so normalisiert),
        // dann fällt die Sortier-Allokation je Auswertung weg — der O(n)-Check ist allokationsfrei.
        IReadOnlyList<CurvePoint> ordered = IsSortedAscending(points)
            ? points
            : points.OrderBy(p => p.TemperatureC).ToArray();

        if (ordered.Count == 1 || temperatureC <= ordered[0].TemperatureC)
            return Clamp(ordered[0].Percent);
        if (temperatureC >= ordered[^1].TemperatureC)
            return Clamp(ordered[^1].Percent);

        return curve.InterpolationMode switch
        {
            InterpolationMode.Spline => Clamp(EvaluateSpline(ordered, temperatureC)),
            _ => Clamp(EvaluateLinear(ordered, temperatureC)),
        };
    }

    /// <summary>Aufsteigend nach Temperatur sortiert? Allokationsfreier O(n)-Check für den Hot-Path.</summary>
    private static bool IsSortedAscending(IReadOnlyList<CurvePoint> points)
    {
        for (int i = 1; i < points.Count; i++)
            if (points[i].TemperatureC < points[i - 1].TemperatureC)
                return false;
        return true;
    }

    /// <summary>Geradlinige Interpolation auf dem einschließenden Intervall (unverändertes Alt-Verhalten).</summary>
    private static double EvaluateLinear(IReadOnlyList<CurvePoint> ordered, double temperatureC)
    {
        for (int i = 0; i < ordered.Count - 1; i++)
        {
            CurvePoint a = ordered[i];
            CurvePoint b = ordered[i + 1];
            if (temperatureC >= a.TemperatureC && temperatureC <= b.TemperatureC)
            {
                double span = b.TemperatureC - a.TemperatureC;
                double t = span <= 0 ? 0 : (temperatureC - a.TemperatureC) / span;
                return a.Percent + t * (b.Percent - a.Percent);
            }
        }

        return ordered[^1].Percent; // unerreichbar, Sicherheitsnetz
    }

    /// <summary>
    /// Monotone kubische Hermite-Interpolation nach Fritsch-Carlson. Die Tangenten werden so beschnitten,
    /// dass die Kurve die Monotonie der Stützpunkte erhält und nicht über den Wertebereich der
    /// einschließenden Punkte hinausschwingt; ein flaches Segment bleibt exakt flach.
    /// <para>
    /// Hardware-konservativ: Das Ergebnis wird zusätzlich nie unter die lineare Verbindung (Sehne)
    /// gedrückt (<c>Math.Max(spline, linear)</c>). Fritsch-Carlson allein garantiert nur Monotonie und
    /// das Bleiben im Stützpunkt-Wertebereich — auf <i>konvexen</i> Segmenten läge die glatte Spline
    /// sonst unter der Sehne, also weniger PWM als der Nutzer gezeichnet hat (Unterkühlungs-/
    /// Übertemp-Risiko). Mit der Schranke gilt garantiert PWM(Spline) ≥ PWM(linear) an jeder Stelle;
    /// die Glättung bleibt dort erhalten, wo die Spline ohnehin oberhalb liegt (konkave Segmente).
    /// </para>
    /// </summary>
    private static double EvaluateSpline(IReadOnlyList<CurvePoint> ordered, double temperatureC)
    {
        int n = ordered.Count;

        // 1) Sekantensteigungen Δy/Δx zwischen benachbarten Stützpunkten.
        var slope = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = ordered[i + 1].TemperatureC - ordered[i].TemperatureC;
            slope[i] = dx <= 0 ? 0.0 : (ordered[i + 1].Percent - ordered[i].Percent) / dx;
        }

        // 2) Tangenten je Stützpunkt initialisieren (Mittel der angrenzenden Sekanten, Ränder = Randsekante).
        var tangent = new double[n];
        tangent[0] = slope[0];
        tangent[n - 1] = slope[n - 2];
        for (int i = 1; i < n - 1; i++)
            tangent[i] = (slope[i - 1] + slope[i]) / 2.0;

        // 3) Fritsch-Carlson-Limiter: bei flachem Segment (slope==0) Tangenten an beiden Enden auf 0 setzen,
        //    sonst α/β auf den Kreis mit Radius 3 beschränken → garantiert Monotonie & kein Overshoot.
        for (int i = 0; i < n - 1; i++)
        {
            if (slope[i] == 0.0)
            {
                tangent[i] = 0.0;
                tangent[i + 1] = 0.0;
                continue;
            }

            double alpha = tangent[i] / slope[i];
            double beta = tangent[i + 1] / slope[i];

            // Gegenläufige Tangente (Vorzeichenwechsel) → lokales Extremum verhindern.
            if (alpha < 0.0) tangent[i] = 0.0;
            if (beta < 0.0) tangent[i + 1] = 0.0;

            double s = alpha * alpha + beta * beta;
            if (s > 9.0)
            {
                double tau = 3.0 / Math.Sqrt(s);
                tangent[i] = tau * alpha * slope[i];
                tangent[i + 1] = tau * beta * slope[i];
            }
        }

        // 4) Einschließendes Intervall finden und Hermite-Basis darauf auswerten.
        for (int i = 0; i < n - 1; i++)
        {
            double x0 = ordered[i].TemperatureC;
            double x1 = ordered[i + 1].TemperatureC;
            if (temperatureC < x0 || temperatureC > x1)
                continue;

            double h = x1 - x0;
            if (h <= 0)
                return ordered[i + 1].Percent; // doppelte Temperatur: oberen Punkt nehmen (deterministisch)

            double t = (temperatureC - x0) / h;
            double t2 = t * t;
            double t3 = t2 * t;

            double h00 = 2 * t3 - 3 * t2 + 1;
            double h10 = t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 = t3 - t2;

            double spline = h00 * ordered[i].Percent
                          + h10 * h * tangent[i]
                          + h01 * ordered[i + 1].Percent
                          + h11 * h * tangent[i + 1];

            // Untere Schranke: nie unter die lineare Verbindung (Sehne) fallen — siehe Methoden-Doc.
            double linear = ordered[i].Percent + t * (ordered[i + 1].Percent - ordered[i].Percent);
            return Math.Max(spline, linear);
        }

        return ordered[^1].Percent; // unerreichbar, Sicherheitsnetz
    }

    /// <summary>Wandelt Prozent (0..100) in einen PWM-Rohwert (0..255).</summary>
    public static byte PercentToPwm(double percent)
    {
        double clamped = Clamp(percent);
        return (byte)Math.Round(clamped / 100.0 * 255.0, MidpointRounding.AwayFromZero);
    }

    private static double Clamp(double percent) => Math.Clamp(percent, 0.0, 100.0);
}
