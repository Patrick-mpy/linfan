// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LinFan.App.Controls;

/// <summary>
/// A curve at thumbnail size (the overview's control loops): the output over 0..100 °C as a line plus a dot at the
/// current input. Draws precomputed <see cref="Samples"/> (one value per °C, evaluated by the controller with the
/// daemon's curve engine), so steps and splines look as they behave. Read-only; pure view mechanics.
/// </summary>
public sealed class MiniCurve : Control
{
    private const double Pad = 3.0;

    public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty =
        AvaloniaProperty.Register<MiniCurve, IReadOnlyList<double>?>(nameof(Samples));

    public static readonly StyledProperty<double> DotTemperatureProperty =
        AvaloniaProperty.Register<MiniCurve, double>(nameof(DotTemperature), double.NaN);

    public static readonly StyledProperty<double> DotPercentProperty =
        AvaloniaProperty.Register<MiniCurve, double>(nameof(DotPercent), double.NaN);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<MiniCurve, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> DotFillProperty =
        AvaloniaProperty.Register<MiniCurve, IBrush?>(nameof(DotFill));

    static MiniCurve() =>
        AffectsRender<MiniCurve>(SamplesProperty, DotTemperatureProperty, DotPercentProperty, StrokeProperty, DotFillProperty);

    /// <summary>Output in percent at 0, 1, …, n-1 °C (the x axis spans the samples).</summary>
    public IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>The input on the x axis (°C); NaN = no dot.</summary>
    public double DotTemperature
    {
        get => GetValue(DotTemperatureProperty);
        set => SetValue(DotTemperatureProperty, value);
    }

    /// <summary>The output at the input (percent); NaN = no dot.</summary>
    public double DotPercent
    {
        get => GetValue(DotPercentProperty);
        set => SetValue(DotPercentProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? DotFill
    {
        get => GetValue(DotFillProperty);
        set => SetValue(DotFillProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        IReadOnlyList<double>? samples = Samples;
        if (samples is not { Count: >= 2 } || Bounds.Width < 2 * Pad || Bounds.Height < 2 * Pad)
            return;

        double span = samples.Count - 1;
        double plotW = Bounds.Width - 2 * Pad;
        double plotH = Bounds.Height - 2 * Pad;
        Point At(double t, double pct) => new(Pad + t / span * plotW, Pad + (1 - Math.Clamp(pct, 0, 100) / 100) * plotH);

        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(At(0, samples[0]), false);
            for (int i = 1; i < samples.Count; i++)
                g.LineTo(At(i, samples[i]));
            g.EndFigure(false);
        }
        if (Stroke is { } stroke)
            ctx.DrawGeometry(null, new Pen(stroke, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);

        if (DotFill is { } dot && !double.IsNaN(DotTemperature) && !double.IsNaN(DotPercent))
            ctx.DrawEllipse(dot, null, At(Math.Clamp(DotTemperature, 0, span), DotPercent), 3, 3);
    }
}
