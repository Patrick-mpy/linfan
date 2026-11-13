// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Controls;
using LinFan.App.Converters;
using Xunit;

namespace LinFan.App.Tests;

public sealed class RadioToggleConverterTests
{
    private static object Convert(object? value) =>
        RadioToggleConverter.Instance.Convert(value, typeof(MenuItemToggleType), null, CultureInfo.InvariantCulture);

    [Fact]
    public void True_IsARadioItem() => Assert.Equal(MenuItemToggleType.Radio, Convert(true));

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    [InlineData("true")]
    public void Anything_Else_IsAPlainItem(object? value) => Assert.Equal(MenuItemToggleType.None, Convert(value));
}
