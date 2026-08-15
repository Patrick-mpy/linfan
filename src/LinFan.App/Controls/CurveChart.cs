// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controls;

/// <summary>
/// Dependency-freier Kurven-Editor als Graph: zeichnet Achsen/Grid, die Lüfterkurve (Temp → %) und
/// die Stützpunkte als ziehbare Griffe. Überlagert den aktuellen Arbeitspunkt (Live-Temperatur →
/// resultierendes %), ausgewertet über den vom Controller gesetzten <see cref="Evaluator"/> - dieselbe
/// CurveEngine-Auswertung wie im Daemon, aber über den Controller bezogen statt LinFan.Core.Services direkt.
/// Reine View-Mechanik - keine Domain-Logik: Ziehen schreibt nur in die gebundenen
/// <see cref="PointRow"/>-Objekte zurück.
/// </summary>
public sealed class CurveChart : Control
{
    // Achsenbereich.
    private const double MaxTemp = 100.0;
    private const double PadLeft = 36, PadRight = 14, PadTop = 14, PadBottom = 24;
    private const double HandleRadius = 6.0, HitRadius = 11.0;

    // Theme-abhängige Pinsel: aus den Farb-Tokens (SemanticColors.axaml) aufgelöst, sobald der Chart im
    // Visual-Tree hängt, und bei Theme-Wechsel neu aufgelöst. Die literalen Fallbacks (Dunkel) greifen,
    // solange (noch) keine App-Resourcen erreichbar sind - z. B. headless oder vor dem Attach.
    private IBrush _gridBrush = new SolidColorBrush(Color.Parse("#26262E"));
    private IBrush _axisTextBrush = new SolidColorBrush(Color.Parse("#71717A"));
    private IBrush _curveBrush = new SolidColorBrush(Color.Parse("#38BDF8"));
    private IBrush _clampBrush = new SolidColorBrush(Color.Parse("#356C82"));
    private IBrush _handleFill = new SolidColorBrush(Color.Parse("#FAFAFA"));
    private IBrush _liveBrush = new SolidColorBrush(Color.Parse("#F59E0B"));

    // Pinsel-abgeleitete Stifte, einmal je Pinsel-Auflösung gebaut statt pro Render-Frame (Render läuft bei
    // jeder Zeiger-Bewegung). Werden zusammen mit den Pinseln in BuildPens() neu erzeugt - im ctor mit den
    // Fallback-Pinseln, danach bei jeder ResolveBrushes()-Auflösung (Attach / Theme-Wechsel).
    private Pen _gridPen = null!;
    private Pen _curvePen = null!;
    private Pen _clampPen = null!;
    private Pen _handlePen = null!;
    private Pen _livePen = null!;

    public static readonly StyledProperty<IEnumerable?> PointsProperty =
        AvaloniaProperty.Register<CurveChart, IEnumerable?>(nameof(Points));

    public static readonly StyledProperty<double> LiveTemperatureProperty =
        AvaloniaProperty.Register<CurveChart, double>(nameof(LiveTemperature), double.NaN);

    public static readonly StyledProperty<InterpolationMode> InterpolationModeProperty =
        AvaloniaProperty.Register<CurveChart, InterpolationMode>(nameof(InterpolationMode), InterpolationMode.Linear);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<CurveChart, bool>(nameof(IsReadOnly));

    /// <summary>
    /// Kurven-Auswertung (Temp → %), vom Controller gesetzt (siehe
    /// <see cref="LinFan.App.Controllers.CurveEditRow.CurveEvaluator"/>). So bezieht die View den Wert über
    /// die Controller-Schicht, statt LinFan.Core.Services direkt zu referenzieren.
    /// </summary>
    public static readonly StyledProperty<Func<Curve, double, double>?> EvaluatorProperty =
        AvaloniaProperty.Register<CurveChart, Func<Curve, double, double>?>(nameof(Evaluator));

    /// <summary>Die <see cref="PointRow"/>-Sammlung der Kurve (Stützpunkte).</summary>
    public IEnumerable? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Aktuelle Quell-Temperatur (°C) für den Arbeitspunkt, oder NaN.</summary>
    public double LiveTemperature
    {
        get => GetValue(LiveTemperatureProperty);
        set => SetValue(LiveTemperatureProperty, value);
    }

    /// <summary>Interpolationsmodus für Kurve und Live-Marker (Linear oder Spline).</summary>
    public InterpolationMode InterpolationMode
    {
        get => GetValue(InterpolationModeProperty);
        set => SetValue(InterpolationModeProperty, value);
    }

    /// <summary>Nur-Anzeige: keine ziehbaren Griffe, keine Maus-Interaktion (z. B. Dashboard-Vorschau).</summary>
    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>Kurven-Auswertung (Temp → %); <c>null</c> ⇒ Kurve und Live-Marker werden nicht gezeichnet (z. B. ungebunden im Designer).</summary>
    public Func<Curve, double, double>? Evaluator
    {
        get => GetValue(EvaluatorProperty);
        set => SetValue(EvaluatorProperty, value);
    }

    private PointRow? _drag;

    static CurveChart()
    {
        AffectsRender<CurveChart>(LiveTemperatureProperty);
        AffectsRender<CurveChart>(InterpolationModeProperty);
        AffectsRender<CurveChart>(EvaluatorProperty);
        PointsProperty.Changed.AddClassHandler<CurveChart>((c, e) => c.OnPointsChanged(e));
    }

    public CurveChart()
    {
        ClipToBounds = true;
        MinHeight = 80; // editierbare Nutzung setzt Height="240" explizit; klein genug für die kompakte Read-only-Vorschau
        BuildPens(); // Stifte aus den Fallback-Pinseln, damit ein Render vor dem Attach nicht auf null trifft
    }

    // --- Theme-abhängige Pinsel auflösen ---------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResolveBrushes(); // App-Resourcen sind erst ab hier erreichbar, nicht im ctor
        ActualThemeVariantChanged += OnThemeChanged;
        _attached = true;
        Resubscribe(Points); // Abos beim (Wieder-)Anhängen auf die aktuelle Sammlung setzen
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        Resubscribe(null); // Collection-/Punkt-Abos lösen: eine überlebende Sammlung darf den Chart nicht halten
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ResolveBrushes();
        InvalidateVisual();
    }

    private void ResolveBrushes()
    {
        _gridBrush = ResolveBrush("GridColor", _gridBrush);
        _axisTextBrush = ResolveBrush("AxisColor", _axisTextBrush);
        _curveBrush = ResolveBrush("AccentColor", _curveBrush);
        _clampBrush = ResolveBrush("ClampColor", _clampBrush);
        _handleFill = ResolveBrush("HandleColor", _handleFill);
        _liveBrush = ResolveBrush("LiveColor", _liveBrush);
        BuildPens(); // Stifte an die (neu) aufgelösten Pinsel koppeln - sonst zeigten sie noch auf die alten
    }

    /// <summary>Baut die gecachten Stifte aus den aktuellen Pinseln - exakt die Strichbreiten/Dash-Stile der Draw-Pfade.</summary>
    private void BuildPens()
    {
        _gridPen = new Pen(_gridBrush, 1);
        _curvePen = new Pen(_curveBrush, 2.5);
        _clampPen = new Pen(_clampBrush, 2, new DashStyle(new double[] { 3, 3 }, 0));
        _handlePen = new Pen(_curveBrush, 2); // Griff-Rand (Breite 2), separat vom Kurven-Stift (2.5)
        _livePen = new Pen(_liveBrush, 1.5, new DashStyle(new double[] { 2, 2 }, 0));
    }

    private IBrush ResolveBrush(string colorKey, IBrush fallback) =>
        this.TryFindResource(colorKey, ActualThemeVariant, out object? value) && value is Color c
            ? new SolidColorBrush(c)
            : fallback;

    // --- Datenbindung an die veränderliche Punkt-Sammlung ----------------------
    //
    // Handler laufen nur, solange der Chart im Visual-Tree hängt UND genau auf der aktuellen Points-Sammlung.
    // Beim Detach werden sie gelöst und beim (Wieder-)Attach neu gesetzt - sonst hielte eine Sammlung, die den
    // Chart überlebt (sie gehört dem Controller), ihn über CollectionChanged/PropertyChanged am Leben.

    private IEnumerable? _subscribed; // Sammlung, auf der aktuell Handler hängen (null = keine)
    private bool _attached;

    private void OnPointsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_attached) // im detachten Zustand nur die Referenz vergessen; das Attach abonniert die dann aktuelle Sammlung
            Resubscribe(e.NewValue as IEnumerable);
        InvalidateVisual();
    }

    /// <summary>Verlegt die Collection-/Punkt-Abos von der aktuell abonnierten auf <paramref name="items"/> (null = alle lösen).</summary>
    private void Resubscribe(IEnumerable? items)
    {
        if (_subscribed is INotifyCollectionChanged oldCol)
            oldCol.CollectionChanged -= OnCollectionChanged;
        Unsubscribe(_subscribed);

        _subscribed = items;

        if (items is INotifyCollectionChanged newCol)
            newCol.CollectionChanged += OnCollectionChanged;
        Subscribe(items);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Unsubscribe(e.OldItems);
        Subscribe(e.NewItems);
        InvalidateVisual();
    }

    private void Subscribe(IEnumerable? items)
    {
        if (items is null) return;
        foreach (object item in items)
            if (item is INotifyPropertyChanged npc)
                npc.PropertyChanged += OnPointPropertyChanged;
    }

    private void Unsubscribe(IEnumerable? items)
    {
        if (items is null) return;
        foreach (object item in items)
            if (item is INotifyPropertyChanged npc)
                npc.PropertyChanged -= OnPointPropertyChanged;
    }

    private void OnPointPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // --- Interaktion: Stützpunkte ziehen ---------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsReadOnly)
            return; // Nur-Anzeige: kein Ziehen (Moved/Released sind ohne aktiven Drag ohnehin No-Ops)
        if (HitTest(e.GetPosition(this)) is { } hit)
        {
            _drag = hit;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null) return;

        Point p = e.GetPosition(this);
        _drag.Temperature = (decimal)Math.Round(Math.Clamp(TempForX(p.X), 0, MaxTemp));
        _drag.Percent = (decimal)Math.Round(Math.Clamp(PctForY(p.Y), 0, 100));
        e.Handled = true; // InvalidateVisual läuft über das PropertyChanged des Punkts
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not null)
        {
            _drag = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    private PointRow? HitTest(Point pos)
    {
        foreach (PointRow row in Rows())
        {
            var screen = new Point(XForTemp((double)row.Temperature), YForPct((double)row.Percent));
            if (Distance(screen, pos) <= HitRadius)
                return row;
        }
        return null;
    }

    // --- Zeichnen --------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        if (Bounds.Width < 4 || Bounds.Height < 4)
            return;

        DrawGrid(ctx);

        var pts = SortedPoints();
        if (pts.Count == 0)
        {
            DrawText(ctx, Localizer.Instance["CurveChart.NoPoints"], new Point(PadLeft + 6, PadTop + 6), _axisTextBrush);
            return;
        }

        Func<Curve, double, double>? evaluate = Evaluator;
        if (evaluate is not null)
        {
            DrawCurve(ctx, pts, evaluate);
            DrawLiveMarker(ctx, pts, evaluate);
        }
        DrawHandles(ctx);
    }

    private void DrawGrid(DrawingContext ctx)
    {
        for (int t = 0; t <= MaxTemp; t += 20)
        {
            double x = XForTemp(t);
            ctx.DrawLine(_gridPen, new Point(x, PadTop), new Point(x, Bounds.Height - PadBottom));
            DrawText(ctx, $"{t}°", new Point(x - 8, Bounds.Height - PadBottom + 4), _axisTextBrush);
        }
        for (int p = 0; p <= 100; p += 20)
        {
            double y = YForPct(p);
            ctx.DrawLine(_gridPen, new Point(PadLeft, y), new Point(Bounds.Width - PadRight, y));
            DrawText(ctx, $"{p}", new Point(4, y - 7), _axisTextBrush);
        }
    }

    private void DrawCurve(DrawingContext ctx, List<(double T, double P)> pts, Func<Curve, double, double> evaluate)
    {
        // Flache Verlängerungen (entsprechen dem Clamping in der CurveEngine: vor erstem/nach letztem Punkt konstant).
        var first = pts[0];
        var last = pts[^1];
        ctx.DrawLine(_clampPen, new Point(PadLeft, YForPct(first.P)), new Point(XForTemp(first.T), YForPct(first.P)));
        ctx.DrawLine(_clampPen, new Point(XForTemp(last.T), YForPct(last.P)),
            new Point(Bounds.Width - PadRight, YForPct(last.P)));

        // Kurve abtasten: ~1 px Schrittweite über die Temperatur-Achse → für Spline glatte Kurve.
        var curve = BuildCoreCurve(pts);
        double plotW = PlotW;
        int steps = Math.Max(2, (int)plotW);
        double tMin = pts[0].T;
        double tMax = pts[^1].T;
        double tRange = tMax - tMin;
        if (tRange <= 0)
            tRange = 1;

        var polyline = new PolylineGeometry();
        for (int i = 0; i <= steps; i++)
        {
            double t = tMin + i / (double)steps * tRange;
            double p = evaluate(curve, t);
            polyline.Points.Add(new Point(XForTemp(t), YForPct(p)));
        }
        ctx.DrawGeometry(null, _curvePen, polyline);
    }

    private void DrawHandles(DrawingContext ctx)
    {
        if (IsReadOnly)
            return; // keine ziehbaren Griffe in der Nur-Anzeige (Kurve + Live-Marker genügen)
        foreach (PointRow row in Rows())
        {
            var c = new Point(XForTemp((double)row.Temperature), YForPct((double)row.Percent));
            bool active = ReferenceEquals(row, _drag);
            ctx.DrawEllipse(active ? _curveBrush : _handleFill, _handlePen, c, HandleRadius, HandleRadius);
        }
    }

    private void DrawLiveMarker(DrawingContext ctx, List<(double T, double P)> pts, Func<Curve, double, double> evaluate)
    {
        double temp = LiveTemperature;
        if (double.IsNaN(temp))
            return;
        temp = Math.Clamp(temp, 0, MaxTemp);

        var curve = BuildCoreCurve(pts);
        double pct = evaluate(curve, temp);
        double x = XForTemp(temp);
        double y = YForPct(pct);

        ctx.DrawLine(_livePen, new Point(x, PadTop), new Point(x, Bounds.Height - PadBottom));
        ctx.DrawEllipse(_liveBrush, null, new Point(x, y), 4, 4);
        DrawText(ctx, $"{temp:0}° · {pct:0}%", new Point(Math.Min(x + 6, Bounds.Width - PadRight - 54), PadTop + 2),
            _liveBrush);
    }

    // --- Helfer ----------------------------------------------------------------

    /// <summary>Baut ein <see cref="Curve"/>-Core-Objekt aus den aktuellen Punkten und dem gewählten Modus.</summary>
    private Curve BuildCoreCurve(List<(double T, double P)> pts)
    {
        var corePoints = pts.Select(p => new CurvePoint(p.T, p.P)).ToList();
        return new Curve("chart", corePoints, InterpolationMode);
    }

    private IEnumerable<PointRow> Rows() => Points?.OfType<PointRow>() ?? Enumerable.Empty<PointRow>();

    private List<(double T, double P)> SortedPoints() =>
        Rows().Select(r => ((double)r.Temperature, (double)r.Percent)).OrderBy(p => p.Item1).ToList();

    private double XForTemp(double t) => PadLeft + t / MaxTemp * PlotW;
    private double YForPct(double p) => PadTop + (1 - p / 100.0) * PlotH;
    private double TempForX(double x) => (x - PadLeft) / PlotW * MaxTemp;
    private double PctForY(double y) => (1 - (y - PadTop) / PlotH) * 100.0;
    private double PlotW => Math.Max(1, Bounds.Width - PadLeft - PadRight);
    private double PlotH => Math.Max(1, Bounds.Height - PadTop - PadBottom);

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 10.5, brush);
        ctx.DrawText(ft, at);
    }
}
