// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using LinFan.App.Converters;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Covers <see cref="StepperTextConverter"/>, the text converter of the Stepper theme: input accepts "," and "."
/// as decimal separator, display stays invariant whatever the UI culture. Typing through the real control is
/// covered by LayoutStyleTests (UiTests).
/// </summary>
public sealed class StepperTextConverterTests
{
    private static readonly CultureInfo German = new("de-DE");

    private static object? ToValue(string? text) =>
        StepperTextConverter.Instance.Convert(text, typeof(decimal?), null, German);

    private static object? ToText(object? value) =>
        StepperTextConverter.Instance.ConvertBack(value, typeof(string), null, German);

    [Theory]
    [InlineData("2.5", 2.5)]
    [InlineData("2,5", 2.5)]
    [InlineData(" 7 ", 7)]
    [InlineData("1,", 1)]
    [InlineData("0,25", 0.25)]
    [InlineData("-3", -3)]
    public void Text_parses_with_comma_or_point(string text, double expected) =>
        Assert.Equal((decimal)expected, Assert.IsType<decimal>(ToValue(text)));

    /// <summary>A comma is never a thousands separator here: "1,000" is one, not a thousand.</summary>
    [Fact]
    public void Comma_is_never_a_thousands_separator() =>
        Assert.Equal(1m, Assert.IsType<decimal>(ToValue("1,000")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData(",")]
    [InlineData("1,5,3")]
    [InlineData("1.000,5")]
    public void Anything_but_a_plain_number_is_rejected(string? text) =>
        Assert.Throws<FormatException>(() => ToValue(text));

    [Theory]
    [InlineData(2.5, "2.5")]
    [InlineData(3, "3")]
    [InlineData(0.25, "0.25")]
    [InlineData(45, "45")]
    public void Value_shows_invariant_without_trailing_zeros(double value, string expected) =>
        Assert.Equal(expected, ToText((decimal)value));

    /// <summary>Stepping 2.5 by 0.5 yields 3.0m (decimal keeps its scale) - it must still read "3".</summary>
    [Fact]
    public void Stepped_decimal_drops_its_scale() =>
        Assert.Equal("3", ToText(2.5m + 0.5m));

    [Fact]
    public void Missing_value_has_no_text() =>
        Assert.Null(ToText(null));
}
