// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Controllers;
using LinFan.App.Controls;

namespace LinFan.App.UiTests;

/// <summary>
/// Editing on the <see cref="CurveChart"/> through the real input pipeline (headless mouse/keyboard): double-click
/// adds, Delete removes, arrow keys nudge, Tab walks the points, dragging stays between the neighbours. The bands
/// are checked on the drawings the chart records into a <see cref="DrawingGroup"/>, since headless drawing renders
/// no pixels.
/// </summary>
public class CurveChartInteractionTests
{
    // Mirrors the chart's plot padding; the window size gives round pixel steps (3.5 px per °C, 2.24 px per %).
    private const double PadLeft = 36, PadRight = 14, PadTop = 14, PadBottom = 24;
    private const double WindowWidth = 400, WindowHeight = 262;

    // --- adding ----------------------------------------------------------------

    [AvaloniaFact]
    public void DoubleClick_OnEmptyPlot_RaisesAddPointWithRoundedValues()
    {
        var added = new List<ChartPoint>();
        var (window, chart, _) = Show(Points((20, 30), (80, 90)));
        chart.AddPointCommand = new RelayCommand<ChartPoint>(added.Add);

        DoubleClick(window, At(chart, window, 50.4, 60.3));

        Assert.Equal(new[] { new ChartPoint(50, 60) }, added); // the first click of the pair adds nothing
    }

    [AvaloniaFact]
    public void DoubleClick_OnAHandle_AddsNothing()
    {
        var added = new List<ChartPoint>();
        var (window, chart, _) = Show(Points((20, 30), (80, 90)));
        chart.AddPointCommand = new RelayCommand<ChartPoint>(added.Add);

        DoubleClick(window, At(chart, window, 80, 90));

        Assert.Empty(added);
    }

    [AvaloniaFact]
    public void DoubleClick_AtATakenTemperature_LeavesTheDecisionToCanExecute()
    {
        var asked = new List<ChartPoint>();
        var (window, chart, _) = Show(Points((20, 30), (80, 90)));
        chart.AddPointCommand = new RelayCommand<ChartPoint>(_ => { }, p =>
        {
            asked.Add(p);
            return false;
        });

        DoubleClick(window, At(chart, window, 20.2, 75)); // far from the handle, but 20 °C is taken

        Assert.Contains(new ChartPoint(20, 75), asked);
    }

    [AvaloniaFact]
    public void DoubleClick_HonoursCanExecuteAndReadOnly()
    {
        var added = new List<ChartPoint>();
        var (window, chart, _) = Show(Points((20, 30), (80, 90)));
        chart.AddPointCommand = new RelayCommand<ChartPoint>(added.Add, _ => false);

        DoubleClick(window, At(chart, window, 40, 50));
        Assert.Empty(added);

        chart.AddPointCommand = new RelayCommand<ChartPoint>(added.Add);
        chart.IsReadOnly = true;
        DoubleClick(window, At(chart, window, 60, 50));
        Assert.Empty(added);
    }

    [AvaloniaFact]
    public void DoubleClick_SelectsTheAddedPoint()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (80, 90));
        var (window, chart, _) = Show(points);
        chart.AddPointCommand = new RelayCommand<ChartPoint>(p => points.Add(new PointRow(p.Temperature, p.Percent)));

        DoubleClick(window, At(chart, window, 45, 40));
        PointRow added = Assert.Single(points, r => r.Temperature == 45);
        Press(window, Key.Up, PhysicalKey.ArrowUp);

        Assert.Equal(41m, added.Percent);
    }

    // --- removing --------------------------------------------------------------

    [AvaloniaFact]
    public void Delete_RunsTheSelectedRowsRemoveCommand()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (50, 50), (80, 90));
        List<PointRow> removed = WireRemove(points, canRemove: () => true);
        var (window, chart, _) = Show(points);
        PointRow middle = points[1];

        Press(window, Key.Delete, PhysicalKey.Delete);
        Assert.Empty(removed); // nothing selected yet

        Click(window, At(chart, window, 50, 50));
        Assert.True(chart.IsFocused); // the chart takes focus although it handles the press

        Press(window, Key.Delete, PhysicalKey.Delete);
        Assert.Equal(new[] { middle }, removed);
        Assert.Equal(2, points.Count);

        // The removed point is no longer selected: a second key press removes nothing more.
        Press(window, Key.Back, PhysicalKey.Backspace);
        Assert.Single(removed);
    }

    [AvaloniaFact]
    public void Backspace_HonoursTheRemoveCommandsCanExecute()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (80, 90));
        List<PointRow> removed = WireRemove(points, canRemove: () => false);
        var (window, chart, _) = Show(points);

        Click(window, At(chart, window, 20, 30));
        Press(window, Key.Back, PhysicalKey.Backspace);

        Assert.Empty(removed);
        Assert.Equal(2, points.Count);
    }

    // --- selecting -------------------------------------------------------------

    [AvaloniaFact]
    public void PressOnTheEmptyPlot_ClearsTheSelection()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (80, 90));
        List<PointRow> removed = WireRemove(points, canRemove: () => true);
        var (window, chart, _) = Show(points);

        Click(window, At(chart, window, 20, 30));
        Click(window, At(chart, window, 55, 10));
        Press(window, Key.Delete, PhysicalKey.Delete);

        Assert.Empty(removed);
    }

    [AvaloniaFact]
    public void Press_PicksTheNearestOfOverlappingHandles()
    {
        ObservableCollection<PointRow> points = Points((50, 50), (52, 50)); // 7 px apart, both within the hit radius
        var (window, chart, _) = Show(points);

        Click(window, At(chart, window, 51.8, 50));
        Press(window, Key.Up, PhysicalKey.ArrowUp);

        Assert.Equal(50m, points[0].Percent);
        Assert.Equal(51m, points[1].Percent);
    }

    [AvaloniaFact]
    public void SelectedHandle_ShowsARing_OnlyWhileTheChartHasTheFocus()
    {
        var chart = new CurveChart { Points = Points((20, 30), (80, 90)) };
        var other = new Button { Content = "other" };
        DockPanel.SetDock(other, Dock.Top);
        var window = new Window
        {
            Content = new DockPanel { Children = { other, chart } },
            Width = WindowWidth,
            Height = WindowHeight + 40,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, Rings(chart));

        Click(window, At(chart, window, 80, 90));
        Assert.Equal(1, Rings(chart));

        other.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, Rings(chart));
    }

    [AvaloniaFact]
    public void MovingTheRowInTheCollection_KeepsTheSelection()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (80, 90));
        var (window, chart, _) = Show(points);
        PointRow last = points[1];

        Click(window, At(chart, window, 80, 90));
        points.Move(1, 0);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);

        Assert.Equal(79m, last.Temperature);
    }

    [AvaloniaFact]
    public void SwappingOrResettingThePoints_ClearsTheSelection()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (50, 50), (80, 90));
        var (window, chart, _) = Show(points);
        PointRow middle = points[1];

        Click(window, At(chart, window, 50, 50));
        chart.Points = Points((10, 10));
        chart.Points = points; // the same row is back, but no longer selected
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(50m, middle.Temperature);

        Click(window, At(chart, window, 50, 50));
        points.Clear(); // Reset
        points.Add(middle);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(50m, middle.Temperature);
    }

    [AvaloniaFact]
    public void Tab_EntersAtAnEnd_WalksThePoints_AndLeavesPastTheLast()
    {
        ObservableCollection<PointRow> points = Points((80, 90), (20, 30), (50, 50)); // collection order != temperature order
        var before = new Button { Content = "before" };
        var after = new Button { Content = "after" };
        var chart = new CurveChart { Points = points, Height = 200 };
        var window = new Window
        {
            Content = new StackPanel { Children = { before, chart, after } },
            Width = WindowWidth,
            Height = 320,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        before.Focus();
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(chart.IsFocused);
        Press(window, Key.Up, PhysicalKey.ArrowUp); // first point by temperature
        Assert.Equal(31m, points[1].Percent);

        Press(window, Key.Tab, PhysicalKey.Tab);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(51m, points[2].Percent);

        Press(window, Key.Tab, PhysicalKey.Tab);
        Press(window, Key.Tab, PhysicalKey.Tab); // past the last point
        Assert.True(after.IsFocused);

        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.True(chart.IsFocused);
        Press(window, Key.Down, PhysicalKey.ArrowDown); // Shift+Tab enters at the last point
        Assert.Equal(89m, points[0].Percent);

        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift); // past the first point
        Assert.True(before.IsFocused);
    }

    // --- moving ----------------------------------------------------------------

    [AvaloniaFact]
    public void ArrowKeys_NudgeTheSelectedPointByOne()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (50, 50), (80, 90));
        var (window, chart, _) = Show(points);
        PointRow middle = points[1];

        Click(window, At(chart, window, 50, 50));
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal((51m, 52m), (middle.Temperature, middle.Percent));

        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal((49m, 51m), (middle.Temperature, middle.Percent));
        Assert.Equal((20m, 30m), (points[0].Temperature, points[0].Percent)); // only the selected point moves
    }

    [AvaloniaFact]
    public void ArrowKeys_StopOneDegreeBeforeTheNeighbourAndAtTheAxis()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (22, 100));
        var (window, chart, _) = Show(points);
        PointRow first = points[0];

        Click(window, At(chart, window, 20, 30));
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Press(window, Key.Right, PhysicalKey.ArrowRight); // 22 °C is taken by the neighbour
        Assert.Equal(21m, first.Temperature);

        PointRow last = points[1];
        Click(window, At(chart, window, 22, 100));
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(100m, last.Percent);
    }

    [AvaloniaFact]
    public void ArrowKeys_NeverPullAPointBeyondTheAxisOntoIt()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (102, 60)); // the config allows up to 120 °C
        var (window, chart, _) = Show(points);
        PointRow far = points[1];

        Click(window, At(chart, window, 102, 60));
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal((102m, 61m), (far.Temperature, far.Percent));

        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(101m, far.Temperature);

        // The pointer, in contrast, stays on the axis.
        Drag(window, At(chart, window, 101, 61), At(chart, window, 101.2, 61));
        Assert.Equal(100m, far.Temperature);
    }

    [AvaloniaFact]
    public void Drag_KeepsThePointBetweenItsNeighbours()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (50, 50), (80, 90));
        var (window, chart, _) = Show(points);
        PointRow middle = points[1];

        Drag(window, At(chart, window, 50, 50), At(chart, window, 95, 60));
        Assert.Equal((79m, 60m), (middle.Temperature, middle.Percent));

        Drag(window, At(chart, window, 79, 60), At(chart, window, 5, 40));
        Assert.Equal((21m, 40m), (middle.Temperature, middle.Percent));

        Drag(window, At(chart, window, 21, 40), At(chart, window, 35, 45));
        Assert.Equal((35m, 45m), (middle.Temperature, middle.Percent)); // free movement in between
    }

    [AvaloniaFact]
    public void LosingThePointerCapture_EndsTheDrag()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (50, 50), (80, 90));
        var (window, chart, _) = Show(points);
        IPointer? pointer = null;
        chart.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Bubble,
            handledEventsToo: true);

        window.MouseDown(At(chart, window, 50, 50), MouseButton.Left);
        pointer!.Capture(null); // e.g. the window lost activation mid-drag
        window.MouseMove(At(chart, window, 60, 70));
        window.MouseUp(At(chart, window, 60, 70), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((50m, 50m), (points[1].Temperature, points[1].Percent));
    }

    // --- focus without scrolling -------------------------------------------------

    [AvaloniaFact]
    public void PressInAScrolledPage_DoesNotScrollTheChartUnderThePointer()
    {
        ObservableCollection<PointRow> points = Points((20, 30), (80, 90));
        var (window, scroll, chart) = ShowPartlyScrolledIn(points);
        Point handle = At(chart, window, 80, 90);

        window.MouseDown(handle, MouseButton.Left);
        window.MouseMove(handle + new Vector(1, 0));
        window.MouseUp(handle + new Vector(1, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(chart.IsFocused);
        Assert.Equal(0, scroll.Offset.Y);
        Assert.Equal((80m, 90m), (points[1].Temperature, points[1].Percent));
    }

    [AvaloniaFact]
    public void DoubleClickInAScrolledPage_AddsWhereTheFirstClickWas()
    {
        var added = new List<ChartPoint>();
        var (window, scroll, chart) = ShowPartlyScrolledIn(Points((20, 30), (80, 90)));
        chart.AddPointCommand = new RelayCommand<ChartPoint>(added.Add);

        DoubleClick(window, At(chart, window, 50, 88));

        Assert.Equal(0, scroll.Offset.Y);
        Assert.Equal(new[] { new ChartPoint(50, 88) }, added);
    }

    [AvaloniaFact]
    public void PressOnAReadOnlyChart_PassesThrough()
    {
        var chart = new CurveChart { Points = Points((20, 30), (80, 90)), IsReadOnly = true };
        var window = new Window { Content = chart, Width = WindowWidth, Height = WindowHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        bool? handled = null;
        object? source = null;
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            handled = e.Handled;
            source = e.Source;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        Click(window, At(chart, window, 40, 40)); // a grid crossing: drawn read-only too, so the press hits the chart

        Assert.Same(chart, source);
        Assert.False(handled);
        Assert.False(chart.IsFocused);
    }

    [AvaloniaFact]
    public void ReadOnlyChart_IsNoTabStop()
    {
        var (_, chart, _) = Show(Points((20, 30)));
        Assert.True(chart.Focusable);

        chart.IsReadOnly = true;
        Assert.False(chart.Focusable);
    }

    // --- bands -----------------------------------------------------------------

    [AvaloniaFact]
    public void AbsentOrEmptyLimits_DrawNoBand()
    {
        var (_, chart, _) = Show(Points((20, 30), (80, 90)));
        Assert.Empty(Bands(chart));

        chart.LowerLimit = 0;
        chart.UpperLimit = 100;
        Assert.Empty(Bands(chart));

        chart.LowerLimit = double.NaN;
        chart.UpperLimit = double.NaN;
        chart.LowerLimitLabel = "Min";
        Assert.Empty(Bands(chart));
    }

    [AvaloniaFact]
    public void Limits_ShadeBelowTheLowerAndAboveTheUpperLimit()
    {
        var (_, chart, _) = Show(Points((20, 30), (80, 90)));
        chart.LowerLimit = 25;
        chart.UpperLimit = 90;

        List<(Rect Rect, double Opacity)> bands = Bands(chart);

        Assert.Equal(2, bands.Count);
        Assert.All(bands, b => Assert.Equal(0.16, b.Opacity, 3));
        Rect plot = Plot(chart);
        Assert.Contains(bands, b => Near(b.Rect, new Rect(plot.Left, Y(chart, 25), plot.Width, plot.Bottom - Y(chart, 25))));
        Assert.Contains(bands, b => Near(b.Rect, new Rect(plot.Left, plot.Top, plot.Width, Y(chart, 90) - plot.Top)));
    }

    [AvaloniaFact]
    public void LimitLabel_IsDrawnOnlyWhenSupplied()
    {
        var (_, chart, _) = Show(Points((20, 30), (80, 90)));
        chart.LowerLimit = 25;
        int withoutLabel = TextRuns(chart);

        chart.LowerLimitLabel = "Min 25 %";
        Assert.True(TextRuns(chart) > withoutLabel);
    }

    [AvaloniaFact]
    public void Hysteresis_ShadesLiveTemperaturePlusMinusHysteresis()
    {
        var (_, chart, _) = Show(Points((20, 30), (80, 90)));
        chart.Hysteresis = 2;
        Assert.Empty(Bands(chart)); // no live temperature, no band

        chart.LiveTemperature = 50;
        List<(Rect Rect, double Opacity)> bands = Bands(chart);
        Rect plot = Plot(chart);
        var expected = new Rect(X(chart, 48), plot.Top, X(chart, 52) - X(chart, 48), plot.Height);
        Assert.True(Near(Assert.Single(bands).Rect, expected), $"band {bands[0].Rect}, expected {expected}");
        Assert.Equal(0.15, bands[0].Opacity, 3);

        chart.Hysteresis = 0;
        Assert.Empty(Bands(chart));
    }

    // --- helpers ---------------------------------------------------------------

    private static ObservableCollection<PointRow> Points(params (decimal T, decimal P)[] points) =>
        new(points.Select(p => new PointRow(p.T, p.P)));

    /// <summary>Gives every row a remove command like <see cref="CurveEditRow"/> does; returns the removed rows.</summary>
    private static List<PointRow> WireRemove(ObservableCollection<PointRow> points, Func<bool> canRemove)
    {
        var removed = new List<PointRow>();
        foreach (PointRow row in points)
            row.RemoveCommand = new RelayCommand(() =>
            {
                removed.Add(row);
                points.Remove(row);
            }, canRemove);
        return removed;
    }

    private static (Window Window, CurveChart Chart, ObservableCollection<PointRow> Points) Show(
        ObservableCollection<PointRow> points)
    {
        var chart = new CurveChart { Points = points };
        var window = new Window { Content = chart, Width = WindowWidth, Height = WindowHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, chart, points);
    }

    /// <summary>
    /// The chart in a scrollable page like the curve editor, only its upper part in view (content 150-412 px, viewport
    /// 200 px): bringing it into view on focus would scroll it.
    /// </summary>
    private static (Window Window, ScrollViewer Scroll, CurveChart Chart) ShowPartlyScrolledIn(
        ObservableCollection<PointRow> points)
    {
        var chart = new CurveChart { Points = points, Height = WindowHeight };
        var scroll = new ScrollViewer
        {
            Content = new StackPanel { Children = { new Border { Height = 150 }, chart, new Border { Height = 400 } } },
        };
        var window = new Window { Content = scroll, Width = WindowWidth, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, scroll, chart);
    }

    private static Rect Plot(CurveChart chart) =>
        new(PadLeft, PadTop, chart.Bounds.Width - PadLeft - PadRight, chart.Bounds.Height - PadTop - PadBottom);

    private static double X(CurveChart chart, double t) => PadLeft + t / 100 * Plot(chart).Width;
    private static double Y(CurveChart chart, double p) => PadTop + (1 - p / 100) * Plot(chart).Height;

    /// <summary>Window coordinates of a curve position.</summary>
    private static Point At(CurveChart chart, Window window, double t, double p) =>
        chart.TranslatePoint(new Point(X(chart, t), Y(chart, p)), window)
        ?? throw new InvalidOperationException("Chart is not laid out.");

    private static void Click(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void DoubleClick(Window window, Point at)
    {
        Click(window, at);
        Click(window, at);
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static List<Drawing> Record(CurveChart chart)
    {
        var group = new DrawingGroup();
        using (DrawingContext ctx = group.Open())
            chart.Render(ctx);
        var all = new List<Drawing>();
        Flatten(group, all);
        return all;
    }

    private static void Flatten(Drawing drawing, List<Drawing> into)
    {
        into.Add(drawing);
        if (drawing is DrawingGroup g)
            foreach (Drawing child in g.Children)
                Flatten(child, into);
    }

    /// <summary>
    /// The translucent fills of a render - only the bands draw any (the recorder turns rectangles into platform
    /// geometries, so they are told apart by their brush; the hit-test fill is a fully transparent colour).
    /// </summary>
    private static List<(Rect Rect, double Opacity)> Bands(CurveChart chart) =>
        Record(chart).OfType<GeometryDrawing>()
            .Where(d => d is { Geometry: not null, Brush.Opacity: < 1 })
            .Select(d => (d.Geometry!.Bounds, d.Brush!.Opacity))
            .ToList();

    private static int TextRuns(CurveChart chart) => Record(chart).OfType<GlyphRunDrawing>().Count();

    /// <summary>Unfilled two-dimensional outlines - without an evaluator only the selection ring is one (lines are flat).</summary>
    private static int Rings(CurveChart chart) =>
        Record(chart).OfType<GeometryDrawing>()
            .Count(d => d is { Brush: null, Geometry.Bounds: { Width: > 0, Height: > 0 } });

    private static bool Near(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01
        && Math.Abs(a.Width - b.Width) < 0.01 && Math.Abs(a.Height - b.Height) < 0.01;
}
