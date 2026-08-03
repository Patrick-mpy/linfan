// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LinFan.App.Controls;

/// <summary>
/// Schlanke, dependency-freie Verlaufskurve (Sparkline): zeichnet eine Zahlenreihe als normalisierte
/// Linie über die Breite. Read-only — auto-skaliert auf Min/Max der Daten. Reine View-Mechanik.
/// </summary>
public sealed class Sparkline : Control
{
    private const double Pad = 3.0;

    public static readonly StyledProperty<IEnumerable?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IEnumerable?>(nameof(Values));

    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush>(nameof(Stroke), new SolidColorBrush(Color.Parse("#38BDF8")));

    public IEnumerable? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    static Sparkline()
    {
        AffectsRender<Sparkline>(StrokeProperty);
        ValuesProperty.Changed.AddClassHandler<Sparkline>((s, e) => s.OnValuesChanged(e));
    }

    public Sparkline()
    {
        ClipToBounds = true;
        MinHeight = 26;
    }

    // Das CollectionChanged-Abo läuft nur, solange die Sparkline im Visual-Tree hängt UND auf genau der
    // aktuellen Values-Sammlung. Beim Detach lösen, beim (Wieder-)Attach neu setzen — sonst hielte eine
    // Sammlung, die die Sparkline überlebt (sie gehört dem Controller), sie über CollectionChanged am Leben.
    private INotifyCollectionChanged? _subscribed;
    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Resubscribe(Values);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Resubscribe(null);
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnValuesChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_attached) // im detachten Zustand abonniert erst das nächste Attach die dann aktuelle Sammlung
            Resubscribe(e.NewValue as IEnumerable);
        InvalidateVisual();
    }

    /// <summary>Verlegt das CollectionChanged-Abo von der aktuell abonnierten auf <paramref name="items"/> (null = lösen).</summary>
    private void Resubscribe(IEnumerable? items)
    {
        if (_subscribed is not null)
            _subscribed.CollectionChanged -= OnCollectionChanged;
        _subscribed = items as INotifyCollectionChanged;
        if (_subscribed is not null)
            _subscribed.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        if (Values is null || Bounds.Width < 4 || Bounds.Height < 4)
            return;

        var data = Values.OfType<double>().Where(double.IsFinite).ToList();
        if (data.Count < 2)
            return;

        double min = data.Min(), max = data.Max();
        double span = max - min;
        if (span < 1e-6)
            span = 1; // flache Linie → mittig

        double plotW = Bounds.Width - 2 * Pad;
        double plotH = Bounds.Height - 2 * Pad;
        double stepX = plotW / (data.Count - 1);

        var pen = new Pen(Stroke, 1.5, lineJoin: PenLineJoin.Round);
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            for (int i = 0; i < data.Count; i++)
            {
                double x = Pad + i * stepX;
                double y = Pad + (1 - (data[i] - min) / span) * plotH;
                var p = new Point(x, y);
                if (i == 0) g.BeginFigure(p, false);
                else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}
