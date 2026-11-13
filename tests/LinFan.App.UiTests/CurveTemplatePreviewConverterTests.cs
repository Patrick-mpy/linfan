// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using LinFan.App.Controllers;
using LinFan.App.Converters;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// The mini previews of the "add curve" menu. Headless because a <see cref="StreamGeometry"/> needs the platform's
/// render interface.
/// </summary>
public class CurveTemplatePreviewConverterTests
{
    [AvaloniaTheory]
    [InlineData(CurveTemplate.Silent)]
    [InlineData(CurveTemplate.Balanced)]
    [InlineData(CurveTemplate.Performance)]
    [InlineData(CurveTemplate.Constant40)]
    public void EveryTemplate_DrawsInsideTheBox(CurveTemplate template)
    {
        var geometry = Assert.IsAssignableFrom<Geometry>(CurveTemplatePreviewConverter.Instance.Convert(
            CurveTemplates.Points(template), typeof(Geometry), null, CultureInfo.InvariantCulture));

        Rect bounds = geometry.Bounds;
        Assert.True(bounds.Left >= 0 && bounds.Right <= CurveTemplatePreviewConverter.Width, bounds.ToString());
        Assert.True(bounds.Top >= 0 && bounds.Bottom <= CurveTemplatePreviewConverter.Height, bounds.ToString());
        Assert.True(bounds.Width > 0);
    }

    [AvaloniaFact]
    public void ThePoints_MapOntoTheAxes()
    {
        // 0 °C/0 % is the bottom left (above the margin), 100 °C/100 % the top right.
        Geometry line = CurveTemplatePreviewConverter.Preview(new[] { new CurvePoint(0, 0), new CurvePoint(100, 100) });

        Assert.Equal(new Rect(0, 2, 40, 20), line.Bounds);
    }

    [AvaloniaFact]
    public void AnythingElse_ConvertsToNull() =>
        Assert.Null(CurveTemplatePreviewConverter.Instance.Convert("Silent", typeof(Geometry), null, CultureInfo.InvariantCulture));
}
