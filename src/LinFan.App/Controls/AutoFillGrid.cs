// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace LinFan.App.Controls;

/// <summary>
/// Lays its children out in as many equal columns as fit at <see cref="MinItemWidth"/> and stretches them over the
/// whole width - CSS <c>repeat(auto-fill, minmax(w, 1fr))</c>. A <see cref="WrapPanel"/> with <c>ItemWidth</c> keeps
/// the items at that width and leaves up to one item's width empty at the right edge. Each row is as high as its
/// tallest child; a shorter child follows its own <c>VerticalAlignment</c>.
/// <para>
/// A child with <see cref="SpansRowProperty"/> takes a row of its own over the full width (CSS <c>grid-column: 1 / -1</c>
/// without dense packing: the row before it ends early). As an items panel it lets the list box move its selection with
/// the arrow keys, Home and End (<see cref="INavigableContainer"/>).
/// </para>
/// </summary>
public sealed class AutoFillGrid : Panel, INavigableContainer
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AutoFillGrid, double>(nameof(MinItemWidth), 250);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<AutoFillGrid, double>(nameof(Spacing));

    /// <summary>The child takes a full row of its own (e.g. an opened tile).</summary>
    public static readonly AttachedProperty<bool> SpansRowProperty =
        AvaloniaProperty.RegisterAttached<AutoFillGrid, Control, bool>("SpansRow");

    private int _columns = 1; // of the last layout pass - the keyboard navigation moves along it

    static AutoFillGrid()
    {
        AffectsMeasure<AutoFillGrid>(MinItemWidthProperty, SpacingProperty);
        AffectsParentMeasure<AutoFillGrid>(SpansRowProperty);
    }

    public static bool GetSpansRow(Control control) => control.GetValue(SpansRowProperty);

    public static void SetSpansRow(Control control, bool value) => control.SetValue(SpansRowProperty, value);

    /// <summary>The narrowest a column may get; one column is used even below it.</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>Gap between columns and between rows.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Columns for <paramref name="width"/> (at least one; without a width limit, one per child).</summary>
    public static int ColumnsFor(double width, double minItemWidth, double spacing, int count)
    {
        if (double.IsInfinity(width))
            return Math.Max(1, count);
        return Math.Max(1, (int)Math.Floor((width + spacing) / (Math.Max(1, minItemWidth) + spacing)));
    }

    private (int Columns, double ItemWidth) Layout(double width, int count)
    {
        int columns = ColumnsFor(width, MinItemWidth, Spacing, count);
        double itemWidth = double.IsInfinity(width)
            ? MinItemWidth
            : Math.Max(0, (width - (columns - 1) * Spacing) / columns);
        return (columns, itemWidth);
    }

    private List<Control> VisibleChildren() => Children.Where(c => c.IsVisible).ToList();

    /// <summary>Splits the children into rows: <paramref name="columns"/> per row, a spanning child alone in its own.</summary>
    private static List<List<Control>> Rows(List<Control> visible, int columns)
    {
        var rows = new List<List<Control>>();
        var current = new List<Control>();
        foreach (Control child in visible)
        {
            if (GetSpansRow(child))
            {
                if (current.Count > 0)
                    rows.Add(current);
                rows.Add(new List<Control> { child });
                current = new List<Control>();
                continue;
            }
            current.Add(child);
            if (current.Count == columns)
            {
                rows.Add(current);
                current = new List<Control>();
            }
        }
        if (current.Count > 0)
            rows.Add(current);
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        List<Control> visible = VisibleChildren();
        (int columns, double itemWidth) = Layout(availableSize.Width, visible.Count);
        _columns = columns;
        double fullWidth = double.IsInfinity(availableSize.Width)
            ? columns * itemWidth + (columns - 1) * Spacing
            : availableSize.Width;

        List<List<Control>> rows = Rows(visible, columns);
        double height = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            double rowHeight = 0;
            foreach (Control child in rows[r])
            {
                child.Measure(new Size(GetSpansRow(child) ? fullWidth : itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight + (r > 0 ? Spacing : 0);
        }

        int used = Math.Min(columns, visible.Count);
        double width = double.IsInfinity(availableSize.Width)
            ? used * itemWidth + Math.Max(0, used - 1) * Spacing
            : availableSize.Width;
        return new Size(visible.Count == 0 ? 0 : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        List<Control> visible = VisibleChildren();
        (int columns, double itemWidth) = Layout(finalSize.Width, visible.Count);
        _columns = columns;
        double y = 0;
        foreach (List<Control> cells in Rows(visible, columns))
        {
            double rowHeight = cells.Max(c => c.DesiredSize.Height);
            for (int col = 0; col < cells.Count; col++)
            {
                double width = GetSpansRow(cells[col]) ? finalSize.Width : itemWidth;
                cells[col].Arrange(new Rect(col * (itemWidth + Spacing), y, width, rowHeight));
            }
            y += rowHeight + Spacing;
        }
        return finalSize;
    }

    /// <summary>
    /// Arrow keys move along the grid as it is laid out (left/right through the order, up/down a row), Home/End to the
    /// first/last child; <paramref name="wrap"/> continues at the other end.
    /// </summary>
    IInputElement? INavigableContainer.GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        List<Control> visible = VisibleChildren();
        if (visible.Count == 0)
            return null;
        int index = from is Control control ? visible.IndexOf(control) : -1;

        switch (direction)
        {
            case NavigationDirection.First:
            case NavigationDirection.PageUp:
                return visible[0];
            case NavigationDirection.Last:
            case NavigationDirection.PageDown:
                return visible[^1];
        }
        if (index < 0)
            return visible[0];

        switch (direction)
        {
            case NavigationDirection.Next:
            case NavigationDirection.Right:
                return index + 1 < visible.Count ? visible[index + 1] : wrap ? visible[0] : null;
            case NavigationDirection.Previous:
            case NavigationDirection.Left:
                return index > 0 ? visible[index - 1] : wrap ? visible[^1] : null;
            case NavigationDirection.Up:
            case NavigationDirection.Down:
                List<List<Control>> rows = Rows(visible, _columns);
                int row = rows.FindIndex(r => r.Contains(visible[index]));
                int col = rows[row].IndexOf(visible[index]);
                int target = row + (direction == NavigationDirection.Down ? 1 : -1);
                if (target < 0 || target >= rows.Count)
                {
                    if (!wrap)
                        return null;
                    target = (target + rows.Count) % rows.Count;
                }
                return rows[target][Math.Min(col, rows[target].Count - 1)];
            default:
                return null;
        }
    }
}
