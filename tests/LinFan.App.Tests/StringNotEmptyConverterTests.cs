// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using LinFan.App.Converters;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert die Logik des <see cref="StringNotEmptyConverter"/> ab - er steuert die Sichtbarkeit des
/// PWM-Auto-Swap-Hinweises (sichtbar nur bei nicht-leerem Text). Reine C#-Logik, ohne Avalonia-Headless testbar.
/// </summary>
public sealed class StringNotEmptyConverterTests
{
    private static object? Convert(object? value) =>
        StringNotEmptyConverter.Instance.Convert(value, typeof(bool), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_NonEmptyString_IsTrue() =>
        Assert.Equal(true, Convert("Max auf 150 angehoben (muss ≥ Min sein)"));

    [Fact]
    public void Convert_EmptyString_IsFalse() =>
        Assert.Equal(false, Convert(""));

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Convert_WhitespaceOnly_IsFalse(string value) =>
        Assert.Equal(false, Convert(value));

    [Fact]
    public void Convert_Null_IsFalse() =>
        Assert.Equal(false, Convert(null));

    [Fact]
    public void Convert_NonStringValue_IsFalse() =>
        Assert.Equal(false, Convert(42));

    [Fact]
    public void ConvertBack_Throws() =>
        Assert.Throws<NotSupportedException>(() =>
            StringNotEmptyConverter.Instance.ConvertBack("x", typeof(string), null, CultureInfo.InvariantCulture));
}
