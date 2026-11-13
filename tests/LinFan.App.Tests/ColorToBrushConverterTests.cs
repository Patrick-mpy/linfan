// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Media;
using LinFan.App.Converters;

namespace LinFan.App.Tests;

public sealed class ColorToBrushConverterTests
{
    [Fact]
    public void AColour_BecomesASolidBrushOfIt()
    {
        object? brush = ColorToBrushConverter.Instance.Convert(Color.Parse("#38BDF8"), typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.Equal(Color.Parse("#38BDF8"), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }

    [Fact]
    public void AnythingElse_GivesNoBrush() =>
        Assert.Null(ColorToBrushConverter.Instance.Convert("#38BDF8", typeof(IBrush), null, CultureInfo.InvariantCulture));
}
