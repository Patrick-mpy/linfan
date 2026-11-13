// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controls;

namespace LinFan.App.Tests;

/// <summary>Geometry and value rules of the two-thumb slider (fan › Advanced, power range).</summary>
public class RangeSliderLayoutTests
{
    // 218 px wide: the usable track is 200 px between the insets, so 1 % = 2 px.
    private const double Width = 218;

    [Theory]
    [InlineData(0, 9)]
    [InlineData(50, 109)]
    [InlineData(100, 209)]
    [InlineData(-5, 9)]    // outside the range: at its end
    [InlineData(140, 209)]
    public void XOf_PlacesValuesBetweenTheInsets(double value, double x) =>
        Assert.Equal(x, RangeSliderLayout.XOf(value, 0, 100, Width), 6);

    [Theory]
    [InlineData(9, 0)]
    [InlineData(109, 50)]
    [InlineData(110, 51)]  // rounded to whole numbers
    [InlineData(0, 0)]     // left of the track
    [InlineData(400, 100)] // right of it
    public void ValueAt_IsTheWholeValueUnderThePointer(double x, double value) =>
        Assert.Equal(value, RangeSliderLayout.ValueAt(x, 0, 100, Width));

    [Fact]
    public void ValueAt_And_XOf_RoundTrip()
    {
        for (int v = 0; v <= 100; v++)
            Assert.Equal(v, RangeSliderLayout.ValueAt(RangeSliderLayout.XOf(v, 0, 100, Width), 0, 100, Width));
    }

    [Theory]
    [InlineData(40, 50, 150, RangeSliderLayout.Thumb.Lower)]
    [InlineData(140, 50, 150, RangeSliderLayout.Thumb.Upper)]
    [InlineData(99, 50, 150, RangeSliderLayout.Thumb.Lower)]
    [InlineData(101, 50, 150, RangeSliderLayout.Thumb.Upper)]
    // Both thumbs on one spot: the side of the press decides, so neither gets stuck.
    [InlineData(90, 100, 100, RangeSliderLayout.Thumb.Lower)]
    [InlineData(110, 100, 100, RangeSliderLayout.Thumb.Upper)]
    public void Pick_TakesTheNearerThumb(double x, double lowerX, double upperX, RangeSliderLayout.Thumb expected) =>
        Assert.Equal(expected, RangeSliderLayout.Pick(x, lowerX, upperX));

    /// <summary>A pair at either end of the range: the thumb that can still move takes the press.</summary>
    [Theory]
    [InlineData(215, true, false, RangeSliderLayout.Thumb.Lower)] // both at 100 %, pressed right of them
    [InlineData(5, false, true, RangeSliderLayout.Thumb.Upper)]   // both at 0 %, pressed left of them
    public void Pick_AtTheEnds_TakesTheThumbThatCanMove(double x, bool upperAtMaximum, bool lowerAtMinimum, RangeSliderLayout.Thumb expected)
    {
        double at = upperAtMaximum ? 209 : 9;
        Assert.Equal(expected, RangeSliderLayout.Pick(x, at, at, upperAtMaximum, lowerAtMinimum));
    }

    [Fact]
    public void Constrain_KeepsTheThumbsFromPassingEachOther()
    {
        Assert.Equal(60, RangeSliderLayout.Constrain(RangeSliderLayout.Thumb.Lower, 80, 20, 60));
        Assert.Equal(30, RangeSliderLayout.Constrain(RangeSliderLayout.Thumb.Lower, 30, 20, 60));
        Assert.Equal(20, RangeSliderLayout.Constrain(RangeSliderLayout.Thumb.Upper, 10, 20, 60));
        Assert.Equal(70, RangeSliderLayout.Constrain(RangeSliderLayout.Thumb.Upper, 70, 20, 60));
    }

    [Fact]
    public void ZeroWidthOrEmptyRange_DoesNotDivideByZero()
    {
        Assert.Equal(RangeSliderLayout.Inset, RangeSliderLayout.XOf(50, 0, 0, Width));
        Assert.Equal(0, RangeSliderLayout.ValueAt(50, 0, 100, 0));
    }
}
