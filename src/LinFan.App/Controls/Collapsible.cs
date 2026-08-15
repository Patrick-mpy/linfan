// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace LinFan.App.Controls;

/// <summary>
/// Container that opens and closes its child by animating the height it claims in the layout
/// (0 → the child's full desired height) instead of switching <c>IsVisible</c>, which would pop.
/// The child keeps its full size and is clipped from the bottom, so it wipes into view rather than
/// being squashed. Pure view mechanics - no state of its own beyond <see cref="IsOpen"/>.
/// </summary>
public sealed class Collapsible : Decorator
{
    /// <summary>Matches the chevron flip in MainWindow.axaml so header and panel read as one motion.</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<Collapsible, bool>(nameof(IsOpen));

    /// <summary>Revealed fraction of the child (0 = closed, 1 = open) - the actual animated value.</summary>
    private static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<Collapsible, double>(nameof(Progress));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    static Collapsible()
    {
        AffectsMeasure<Collapsible>(ProgressProperty);
    }

    public Collapsible()
    {
        ClipToBounds = true;
    }

    private readonly Transitions _transitions =
    [
        new DoubleTransition { Property = ProgressProperty, Duration = Duration, Easing = new CubicEaseOut() },
    ];

    // The transition is attached only after the initial state has been applied: neither opening the
    // window nor reusing this control in a recycled DataTemplate row may animate its starting state.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Transitions = null;
        Progress = IsOpen ? 1 : 0;
        Opacity = IsOpen ? 1 : 0;
        IsVisible = IsOpen;
        Transitions = _transitions;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Transitions = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsOpenProperty)
        {
            if (IsOpen)
                IsVisible = true; // has to precede the animation, an invisible control does not lay out
            Progress = IsOpen ? 1 : 0;
        }
        else if (change.Property == ProgressProperty)
        {
            double progress = change.GetNewValue<double>();
            Opacity = progress;
            // Closed content must not stay reachable by Tab; the IsOpen guard keeps a reopen that
            // arrives mid-animation from being hidden by the tail of the closing one.
            if (progress <= 0 && !IsOpen)
                IsVisible = false;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Control? child = Child;
        if (child is null)
            return default;

        // The child always measures at full height - only what this container claims of it shrinks.
        child.Measure(availableSize.WithHeight(double.PositiveInfinity));
        Size desired = child.DesiredSize;
        return new Size(desired.Width, desired.Height * Math.Clamp(Progress, 0, 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Control? child = Child;
        if (child is null)
            return finalSize;

        child.Arrange(new Rect(0, 0, finalSize.Width, child.DesiredSize.Height));
        return finalSize;
    }
}
