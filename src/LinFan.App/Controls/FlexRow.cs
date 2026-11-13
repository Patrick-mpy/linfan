// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;

namespace LinFan.App.Controls;

/// <summary>
/// CSS <c>display: flex; flex-wrap: wrap</c> for one direction: children are placed left to right at their
/// <see cref="BasisProperty"/> (or their own width without one) and wrap to a new line when the next one does not fit;
/// the room left on a line is shared by <see cref="GrowProperty"/>. Lets a section sit next to another on a wide window
/// and below it on a narrow one, without a breakpoint.
/// </summary>
public sealed class FlexRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FlexRow, double>(nameof(Spacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FlexRow, double>(nameof(RowSpacing));

    /// <summary>The width a child starts from (CSS <c>flex-basis</c>); 0 = its own desired width.</summary>
    public static readonly AttachedProperty<double> BasisProperty =
        AvaloniaProperty.RegisterAttached<FlexRow, Control, double>("Basis");

    /// <summary>The child's share of the room left on its line (CSS <c>flex-grow</c>); 0 = keeps its basis.</summary>
    public static readonly AttachedProperty<double> GrowProperty =
        AvaloniaProperty.RegisterAttached<FlexRow, Control, double>("Grow");

    static FlexRow()
    {
        AffectsMeasure<FlexRow>(SpacingProperty, RowSpacingProperty);
        AffectsParentMeasure<FlexRow>(BasisProperty, GrowProperty);
    }

    public static double GetBasis(Control control) => control.GetValue(BasisProperty);
    public static void SetBasis(Control control, double value) => control.SetValue(BasisProperty, value);
    public static double GetGrow(Control control) => control.GetValue(GrowProperty);
    public static void SetGrow(Control control, double value) => control.SetValue(GrowProperty, value);

    /// <summary>Gap between the children of a line.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Gap between lines.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    // Basis of every child from the last measure pass: arranging must not measure again - a changed desired size
    // there would invalidate this panel's measure and loop.
    private readonly Dictionary<Control, double> _basis = new();

    /// <summary>Lines of (child, width) for <paramref name="width"/>, from the bases of the last measure pass.</summary>
    private List<List<(Control Child, double Width)>> Lines(double width)
    {
        var lines = new List<List<(Control, double)>>();
        var line = new List<(Control Child, double Basis)>();
        double used = 0;
        foreach (Control child in Children.Where(c => c.IsVisible))
        {
            double basis = _basis.GetValueOrDefault(child);
            double needed = line.Count == 0 ? basis : used + Spacing + basis;
            if (line.Count > 0 && needed > width)
            {
                lines.Add(Distribute(line, width));
                line = new List<(Control, double)>();
                needed = basis;
            }
            line.Add((child, basis));
            used = needed;
        }
        if (line.Count > 0)
            lines.Add(Distribute(line, width));
        return lines;
    }

    private List<(Control Child, double Width)> Distribute(List<(Control Child, double Basis)> line, double width)
    {
        double basisSum = line.Sum(c => c.Basis) + Spacing * (line.Count - 1);
        double growSum = line.Sum(c => GetGrow(c.Child));
        double extra = double.IsInfinity(width) ? 0 : width - basisSum;
        return line.Select(c =>
        {
            double w = c.Basis;
            if (growSum > 0 && extra > 0)
                w += extra * GetGrow(c.Child) / growSum;
            if (!double.IsInfinity(width))
                w = Math.Min(w, width); // a single child wider than the line is squeezed into it
            return (c.Child, Math.Max(0, w));
        }).ToList();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _basis.Clear();
        foreach (Control child in Children.Where(c => c.IsVisible))
        {
            double basis = GetBasis(child);
            if (basis <= 0)
            {
                child.Measure(Size.Infinity);
                basis = child.DesiredSize.Width;
            }
            _basis[child] = basis;
        }

        double height = 0, widest = 0;
        List<List<(Control Child, double Width)>> lines = Lines(availableSize.Width);
        for (int i = 0; i < lines.Count; i++)
        {
            double lineHeight = 0, lineWidth = Spacing * (lines[i].Count - 1);
            foreach ((Control child, double w) in lines[i])
            {
                child.Measure(new Size(w, double.PositiveInfinity));
                lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
                lineWidth += w;
            }
            height += lineHeight + (i > 0 ? RowSpacing : 0);
            widest = Math.Max(widest, lineWidth);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? widest : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (List<(Control Child, double Width)> line in Lines(finalSize.Width))
        {
            double lineHeight = line.Max(c => c.Child.DesiredSize.Height);
            double x = 0;
            foreach ((Control child, double w) in line)
            {
                child.Arrange(new Rect(x, y, w, lineHeight));
                x += w + Spacing;
            }
            y += lineHeight + RowSpacing;
        }
        return finalSize;
    }
}
