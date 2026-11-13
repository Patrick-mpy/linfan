// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controls;

/// <summary>
/// Geometry and value rules of <see cref="RangeSlider"/>, apart from drawing so they can be unit-tested: where a value
/// sits on the track, which value a pointer position means, which thumb a press takes and how far it may go.
/// </summary>
public static class RangeSliderLayout
{
    /// <summary>Half a thumb: the track starts and ends this far inside the control, so a thumb at either end stays whole.</summary>
    public const double Inset = 9;

    /// <summary>The two thumbs.</summary>
    public enum Thumb
    {
        Lower,
        Upper,
    }

    /// <summary>X of <paramref name="value"/> on a track of <paramref name="width"/> (values outside the range sit at its ends).</summary>
    public static double XOf(double value, double minimum, double maximum, double width)
    {
        double span = maximum - minimum;
        double fraction = span <= 0 ? 0 : Math.Clamp((value - minimum) / span, 0, 1);
        return Inset + Math.Max(0, width - 2 * Inset) * fraction;
    }

    /// <summary>The whole-number value under <paramref name="x"/>, within the range.</summary>
    public static double ValueAt(double x, double minimum, double maximum, double width)
    {
        double usable = width - 2 * Inset;
        double fraction = usable <= 0 ? 0 : Math.Clamp((x - Inset) / usable, 0, 1);
        return Math.Clamp(Math.Round(minimum + fraction * (maximum - minimum), MidpointRounding.AwayFromZero), minimum, maximum);
    }

    /// <summary>
    /// The thumb a press at <paramref name="x"/> takes: the nearer one. When both sit on the same spot, the one that can
    /// move takes it - the lower one at the top end of the range, the upper one at the bottom end -, and in between the
    /// side of the press decides (left: lower, right: upper). So a pair at 100 % or 0 % never gets stuck.
    /// </summary>
    public static Thumb Pick(double x, double lowerX, double upperX, bool upperAtMaximum = false, bool lowerAtMinimum = false)
    {
        double toLower = Math.Abs(x - lowerX), toUpper = Math.Abs(x - upperX);
        if (toLower < toUpper)
            return Thumb.Lower;
        if (toUpper < toLower)
            return Thumb.Upper;
        if (upperAtMaximum)
            return Thumb.Lower;
        if (lowerAtMinimum)
            return Thumb.Upper;
        return x < lowerX ? Thumb.Lower : Thumb.Upper;
    }

    /// <summary>Where <paramref name="thumb"/> lands when dragged to <paramref name="value"/>: never past the other thumb.</summary>
    public static double Constrain(Thumb thumb, double value, double lower, double upper) =>
        thumb == Thumb.Lower ? Math.Min(value, upper) : Math.Max(value, lower);
}
