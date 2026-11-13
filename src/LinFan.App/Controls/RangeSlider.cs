// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace LinFan.App.Controls;

/// <summary>
/// A slider with two thumbs for a range (fan › Advanced: min and max power) and an optional marker on the track (the
/// calibrated start point). The thumbs cannot pass each other, so lower ≤ upper holds in the view already. Pointer only:
/// the keyboard path is the pair of steppers next to it (like the list beside the case diagram), so the control takes
/// no focus. Values are whole numbers. Theme colours are resolved like <see cref="CurveChart"/> does.
/// </summary>
public sealed class RangeSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> LowerValueProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(LowerValue), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> UpperValueProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(UpperValue), 100, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double?> MarkerProperty =
        AvaloniaProperty.Register<RangeSlider, double?>(nameof(Marker));

    public static readonly StyledProperty<string?> MarkerLabelProperty =
        AvaloniaProperty.Register<RangeSlider, string?>(nameof(MarkerLabel));

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Value of the left thumb (two-way).</summary>
    public double LowerValue
    {
        get => GetValue(LowerValueProperty);
        set => SetValue(LowerValueProperty, value);
    }

    /// <summary>Value of the right thumb (two-way).</summary>
    public double UpperValue
    {
        get => GetValue(UpperValueProperty);
        set => SetValue(UpperValueProperty, value);
    }

    /// <summary>A value marked on the track, or <c>null</c> for none.</summary>
    public double? Marker
    {
        get => GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    /// <summary>Text above the marker.</summary>
    public string? MarkerLabel
    {
        get => GetValue(MarkerLabelProperty);
        set => SetValue(MarkerLabelProperty, value);
    }

    // Room above the thumbs for the marker label - always kept, so the block does not jump when a marker appears.
    private const double LabelHeight = 16;
    private const double ThumbSize = 2 * RangeSliderLayout.Inset;
    private const double TrackHeight = 4;
    private const double MarkerLabelSize = 10.5;

    private IBrush _accent = new SolidColorBrush(Color.Parse("#38BDF8"));
    private IBrush _track = new SolidColorBrush(Color.Parse("#47FFFFFF"));
    private IBrush _marker = new SolidColorBrush(Color.Parse("#F59E0B"));
    private IBrush _markerText = new SolidColorBrush(Color.Parse("#FBBF24"));
    private Pen _markerPen = null!;

    private RangeSliderLayout.Thumb? _dragging;

    static RangeSlider()
    {
        AffectsRender<RangeSlider>(MinimumProperty, MaximumProperty, LowerValueProperty, UpperValueProperty,
            MarkerProperty, MarkerLabelProperty);
        FocusableProperty.OverrideDefaultValue<RangeSlider>(false);
    }

    public RangeSlider()
    {
        Height = LabelHeight + ThumbSize + 4;
        MinWidth = 2 * RangeSliderLayout.Inset + 40;
        Cursor = new Cursor(StandardCursorType.Hand);
        _markerPen = new Pen(_marker, 2);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResolveBrushes();
        ActualThemeVariantChanged += OnThemeChanged;
        ResourcesChanged += OnResourcesChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        ResourcesChanged -= OnResourcesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // A chosen accent colour arrives as a resource change, not as a change of the theme variant.
    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) => OnThemeChanged(sender, EventArgs.Empty);

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ResolveBrushes();
        InvalidateVisual();
    }

    private void ResolveBrushes()
    {
        if (this.TryFindResource("AccentColor", ActualThemeVariant, out object? accent) && accent is Color c)
            _accent = new SolidColorBrush(c);
        _track = Brush("ControlTrack", _track);
        _marker = Brush("Warning", _marker);
        _markerText = Brush("WarningText", _markerText);
        _markerPen = new Pen(_marker, 2);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush b ? b : fallback;

    // --- Interaction ---------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        double width = Bounds.Width, x = e.GetPosition(this).X;
        _dragging = RangeSliderLayout.Pick(x,
            RangeSliderLayout.XOf(LowerValue, Minimum, Maximum, width),
            RangeSliderLayout.XOf(UpperValue, Minimum, Maximum, width),
            upperAtMaximum: UpperValue >= Maximum, lowerAtMinimum: LowerValue <= Minimum);
        e.Pointer.Capture(this);
        MoveTo(x);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging is not null)
            MoveTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging is null)
            return;
        _dragging = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = null;
    }

    private void MoveTo(double x)
    {
        if (_dragging is not { } thumb)
            return;
        double value = RangeSliderLayout.Constrain(thumb,
            RangeSliderLayout.ValueAt(x, Minimum, Maximum, Bounds.Width), LowerValue, UpperValue);
        if (thumb == RangeSliderLayout.Thumb.Lower)
            SetCurrentValue(LowerValueProperty, value);
        else
            SetCurrentValue(UpperValueProperty, value);
    }

    // --- Drawing -------------------------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        double width = Bounds.Width;
        double centerY = LabelHeight + ThumbSize / 2;
        double lowerX = RangeSliderLayout.XOf(LowerValue, Minimum, Maximum, width);
        double upperX = RangeSliderLayout.XOf(UpperValue, Minimum, Maximum, width);

        var track = new Rect(RangeSliderLayout.Inset, centerY - TrackHeight / 2, Math.Max(0, width - 2 * RangeSliderLayout.Inset), TrackHeight);
        ctx.DrawRectangle(_track, null, track, TrackHeight / 2, TrackHeight / 2);
        ctx.DrawRectangle(_accent, null, new Rect(lowerX, track.Y, Math.Max(0, upperX - lowerX), TrackHeight), TrackHeight / 2, TrackHeight / 2);

        if (Marker is { } marker)
        {
            double markerX = RangeSliderLayout.XOf(marker, Minimum, Maximum, width);
            ctx.DrawLine(_markerPen, new Point(markerX, LabelHeight + 1), new Point(markerX, LabelHeight + ThumbSize - 1));
            if (!string.IsNullOrEmpty(MarkerLabel))
            {
                var text = new FormattedText(MarkerLabel, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, MarkerLabelSize, _markerText);
                double left = Math.Clamp(markerX - text.Width / 2, 0, Math.Max(0, width - text.Width));
                ctx.DrawText(text, new Point(left, LabelHeight - text.Height - 1));
            }
        }

        double radius = ThumbSize / 2;
        ctx.DrawEllipse(_accent, null, new Point(lowerX, centerY), radius, radius);
        ctx.DrawEllipse(_accent, null, new Point(upperX, centerY), radius, radius);
    }
}
