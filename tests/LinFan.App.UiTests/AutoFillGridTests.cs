// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using LinFan.App.Controls;

namespace LinFan.App.UiTests;

/// <summary>The fan blocks' panel: as many equal columns as fit, stretched over the width.</summary>
public class AutoFillGridTests
{
    [Theory]
    [InlineData(700, 2)] // (700 + 10) / (250 + 10) = 2.7
    [InlineData(770, 3)] // exactly three
    [InlineData(400, 1)]
    [InlineData(100, 1)] // narrower than one column: still one
    public void Columns_AreAsManyAsFit(double width, int columns) =>
        Assert.Equal(columns, AutoFillGrid.ColumnsFor(width, minItemWidth: 250, spacing: 10, count: 5));

    [AvaloniaFact]
    public void Children_FillTheWidth_RowByRow()
    {
        var a = new Border { Height = 40, VerticalAlignment = VerticalAlignment.Top };
        var b = new Border { Height = 80, VerticalAlignment = VerticalAlignment.Top };
        var c = new Border { Height = 30, VerticalAlignment = VerticalAlignment.Top };
        var grid = new AutoFillGrid { MinItemWidth = 250, Spacing = 10, Children = { a, b, c } };

        grid.Measure(new Size(700, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, 700, grid.DesiredSize.Height));

        Assert.Equal(80 + 10 + 30, grid.DesiredSize.Height); // the first row is as tall as its tallest block
        Assert.Equal(new Rect(0, 0, 345, 40), a.Bounds);    // (700 - 10) / 2, top-aligned in its row
        Assert.Equal(new Rect(355, 0, 345, 80), b.Bounds);
        Assert.Equal(new Rect(0, 90, 345, 30), c.Bounds);
    }

    [AvaloniaFact]
    public void HiddenChildren_TakeNoCell()
    {
        var a = new Border { Height = 40 };
        var hidden = new Border { Height = 40, IsVisible = false };
        var b = new Border { Height = 40 };
        var grid = new AutoFillGrid { MinItemWidth = 250, Spacing = 10, Children = { a, hidden, b } };

        grid.Measure(new Size(700, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, 700, grid.DesiredSize.Height));

        Assert.Equal(355, b.Bounds.X);
        Assert.Equal(40, grid.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void ASpanningChild_TakesARowOfItsOwn()
    {
        var a = new Border { Height = 40 };
        var open = new Border { Height = 100 };
        var b = new Border { Height = 40 };
        var c = new Border { Height = 40 };
        AutoFillGrid.SetSpansRow(open, true);
        var grid = new AutoFillGrid { MinItemWidth = 150, Spacing = 10, Children = { a, open, b, c } };

        grid.Measure(new Size(470, double.PositiveInfinity)); // three columns of 150
        grid.Arrange(new Rect(0, 0, 470, grid.DesiredSize.Height));

        Assert.Equal(new Rect(0, 0, 150, 40), a.Bounds);     // its row ends early (no dense packing, like CSS)
        Assert.Equal(new Rect(0, 50, 470, 100), open.Bounds); // full width
        Assert.Equal(new Rect(0, 160, 150, 40), b.Bounds);    // the rest continues below
        Assert.Equal(new Rect(160, 160, 150, 40), c.Bounds);
        Assert.Equal(200, grid.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void Navigation_FollowsTheGrid()
    {
        var items = Enumerable.Range(0, 5).Select(_ => new Border { Height = 40 }).ToArray();
        var grid = new AutoFillGrid { MinItemWidth = 150, Spacing = 10 };
        grid.Children.AddRange(items);
        grid.Measure(new Size(470, double.PositiveInfinity)); // rows: 0 1 2 / 3 4
        grid.Arrange(new Rect(0, 0, 470, grid.DesiredSize.Height));
        var nav = (Avalonia.Input.INavigableContainer)grid;

        Assert.Same(items[1], nav.GetControl(Avalonia.Input.NavigationDirection.Right, items[0], wrap: false));
        Assert.Same(items[4], nav.GetControl(Avalonia.Input.NavigationDirection.Down, items[1], wrap: false));
        Assert.Same(items[4], nav.GetControl(Avalonia.Input.NavigationDirection.Down, items[2], wrap: false)); // shorter row: its last
        Assert.Same(items[0], nav.GetControl(Avalonia.Input.NavigationDirection.Up, items[3], wrap: false));
        Assert.Same(items[0], nav.GetControl(Avalonia.Input.NavigationDirection.First, items[3], wrap: false));
        Assert.Same(items[4], nav.GetControl(Avalonia.Input.NavigationDirection.Last, items[0], wrap: false));
        Assert.Null(nav.GetControl(Avalonia.Input.NavigationDirection.Left, items[0], wrap: false));
        Assert.Same(items[4], nav.GetControl(Avalonia.Input.NavigationDirection.Left, items[0], wrap: true));
    }
}
