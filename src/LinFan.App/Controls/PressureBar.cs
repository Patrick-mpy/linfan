// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LinFan.App.Controls;

/// <summary>
/// The pressure balance bar of Settings › Airflow: the exhaust colour as the track, the intake share filled from the
/// left, a dashed line at the even point and a knob at the share. Display only; theme colours as <see cref="CurveChart"/>.
/// </summary>
public sealed class PressureBar : Control
{
    public static readonly StyledProperty<double> IntakePercentProperty =
        AvaloniaProperty.Register<PressureBar, double>(nameof(IntakePercent), 50);

    /// <summary>Intake share in percent (0..100).</summary>
    public double IntakePercent
    {
        get => GetValue(IntakePercentProperty);
        set => SetValue(IntakePercentProperty, value);
    }

    private const double TrackHeight = 10;
    private const double KnobWidth = 6;

    private IBrush _intake = new SolidColorBrush(Color.Parse("#38BDF8"));
    private IBrush _exhaust = new SolidColorBrush(Color.Parse("#F59E0B"));
    private IBrush _knob = new SolidColorBrush(Color.Parse("#FAFAFA"));
    private Pen _mid = new(new SolidColorBrush(Color.Parse("#71717A")), 1, new DashStyle(new double[] { 2, 2 }, 0));
    private Pen _halo = new(new SolidColorBrush(Color.Parse("#16161A")), 2);

    static PressureBar() => AffectsRender<PressureBar>(IntakePercentProperty);

    public PressureBar() => Height = 24;

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
        _intake = ColorBrush("IntakeColor", _intake);
        _exhaust = ColorBrush("ExhaustColor", _exhaust);
        _knob = ColorBrush("HandleColor", _knob);
        _mid = new Pen(ColorBrush("AxisColor", _mid.Brush!), 1, new DashStyle(new double[] { 2, 2 }, 0));
        if (this.TryFindResource("BgCard", ActualThemeVariant, out object? card) && card is IBrush cardBrush)
            _halo = new Pen(cardBrush, 2);
    }

    private IBrush ColorBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is Color c ? new SolidColorBrush(c) : fallback;

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        double width = Bounds.Width, height = Bounds.Height;
        if (width <= KnobWidth)
            return;
        double fraction = Math.Clamp(IntakePercent / 100, 0, 1);
        var track = new Rect(0, (height - TrackHeight) / 2, width, TrackHeight);
        ctx.DrawRectangle(_exhaust, null, track, TrackHeight / 2, TrackHeight / 2);
        ctx.DrawRectangle(_intake, null, track.WithWidth(width * fraction), TrackHeight / 2, TrackHeight / 2);
        ctx.DrawLine(_mid, new Point(width / 2, 1), new Point(width / 2, height - 1));

        double x = Math.Clamp(width * fraction - KnobWidth / 2, 0, width - KnobWidth);
        ctx.DrawRectangle(_knob, _halo, new Rect(x, 1, KnobWidth, height - 2), 3, 3);
    }
}
