// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LinFan.App.Controllers;
using LinFan.Core.Models;

namespace LinFan.App.Converters;

/// <summary>
/// Draws a template's points (<see cref="CurveTemplateOption.Points"/>) as the mini preview of the "add curve" menu: an
/// open line in a 40×24 box (0-100 °C left to right, 0-100 % bottom to top, 2 px margin at top and bottom). One-way.
/// </summary>
public sealed class CurveTemplatePreviewConverter : IValueConverter
{
    public const double Width = 40, Height = 24;
    private const double Margin = 2;

    public static readonly CurveTemplatePreviewConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IReadOnlyList<CurvePoint> points ? Preview(points) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("CurveTemplatePreviewConverter is one-way.");

    /// <summary>The line through <paramref name="points"/>, clamped to the box.</summary>
    public static Geometry Preview(IReadOnlyList<CurvePoint> points)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            for (int i = 0; i < points.Count; i++)
            {
                var at = new Point(Math.Clamp(points[i].TemperatureC, 0, 100) / 100 * Width,
                                   Height - Margin - Math.Clamp(points[i].Percent, 0, 100) / 100 * (Height - 2 * Margin));
                if (i == 0)
                    ctx.BeginFigure(at, isFilled: false);
                else
                    ctx.LineTo(at);
            }
            if (points.Count > 0)
                ctx.EndFigure(isClosed: false);
        }
        return geometry;
    }
}
