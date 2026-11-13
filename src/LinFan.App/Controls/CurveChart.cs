// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
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
/// <remarks>
/// Editing beyond dragging: a pressed handle becomes the selected point. Entering the chart with Tab selects the
/// first point (Shift+Tab the last), Tab/Shift+Tab then walk the points by temperature and leave the chart past
/// either end. The arrow keys nudge the selected point by 1 °C / 1 %, Delete/Backspace run its
/// <see cref="PointRow.RemoveCommand"/>, and a double-click on the empty plot raises <see cref="AddPointCommand"/>
/// and selects the point that appears. Keeping a dragged or nudged point between its neighbours is interaction
/// behaviour (handles must not cross under the pointer); the domain rules (point count, duplicate temperatures)
/// stay in the controller, which the chart consults only through <c>CanExecute</c>.
/// </remarks>
public sealed class CurveChart : Control
{
    // Achsenbereich.
    private const double MaxTemp = 100.0;
    private const double PadLeft = 36, PadRight = 14, PadTop = 14, PadBottom = 24;
    private const double HandleRadius = 6.0, HitRadius = 11.0;
    private const double FocusRingRadius = HandleRadius + 4;
    private const double AxisFontSize = 10.5, LabelFontSize = 10.0, LabelGap = 3.0, LabelInset = 4.0;
    private const double LimitBandOpacity = 0.16, HysteresisBandOpacity = 0.15;

    // Theme-abhängige Pinsel: aus den Farb-Tokens (SemanticColors.axaml) aufgelöst, sobald der Chart im
    // Visual-Tree hängt, und bei Theme-Wechsel neu aufgelöst. Die literalen Fallbacks (Dunkel) greifen,
    // solange (noch) keine App-Resourcen erreichbar sind - z. B. headless oder vor dem Attach.
    private IBrush _gridBrush = new SolidColorBrush(Color.Parse("#26262E"));
    private IBrush _axisTextBrush = new SolidColorBrush(Color.Parse("#71717A"));
    private IBrush _curveBrush = new SolidColorBrush(Color.Parse("#38BDF8"));
    private IBrush _clampBrush = new SolidColorBrush(Color.Parse("#356C82"));
    private IBrush _handleFill = new SolidColorBrush(Color.Parse("#FAFAFA"));
    private IBrush _liveBrush = new SolidColorBrush(Color.Parse("#F59E0B"));

    // Translucent band fills: the axis colour marks the fans' power limits, the live colour the hysteresis.
    private IBrush _limitFill = new SolidColorBrush(Color.Parse("#71717A"), LimitBandOpacity);
    private IBrush _hysteresisFill = new SolidColorBrush(Color.Parse("#F59E0B"), HysteresisBandOpacity);

    // Pinsel-abgeleitete Stifte, einmal je Pinsel-Auflösung gebaut statt pro Render-Frame (Render läuft bei
    // jeder Zeiger-Bewegung). Werden zusammen mit den Pinseln in BuildPens() neu erzeugt - im ctor mit den
    // Fallback-Pinseln, danach bei jeder ResolveBrushes()-Auflösung (Attach / Theme-Wechsel).
    private Pen _gridPen = null!;
    private Pen _curvePen = null!;
    private Pen _clampPen = null!;
    private Pen _handlePen = null!;
    private Pen _livePen = null!;
    private Pen _limitPen = null!;
    private Pen _focusPen = null!;

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

    public static readonly StyledProperty<double?> LowerLimitProperty =
        AvaloniaProperty.Register<CurveChart, double?>(nameof(LowerLimit));

    public static readonly StyledProperty<double?> UpperLimitProperty =
        AvaloniaProperty.Register<CurveChart, double?>(nameof(UpperLimit));

    public static readonly StyledProperty<string?> LowerLimitLabelProperty =
        AvaloniaProperty.Register<CurveChart, string?>(nameof(LowerLimitLabel));

    public static readonly StyledProperty<string?> UpperLimitLabelProperty =
        AvaloniaProperty.Register<CurveChart, string?>(nameof(UpperLimitLabel));

    public static readonly StyledProperty<double> HysteresisProperty =
        AvaloniaProperty.Register<CurveChart, double>(nameof(Hysteresis));

    public static readonly StyledProperty<string?> HysteresisLabelProperty =
        AvaloniaProperty.Register<CurveChart, string?>(nameof(HysteresisLabel));

    public static readonly StyledProperty<ICommand?> AddPointCommandProperty =
        AvaloniaProperty.Register<CurveChart, ICommand?>(nameof(AddPointCommand));

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

    /// <summary>
    /// Lowest output (%) the assigned fans run at; the range below is shaded. <c>null</c>, NaN or ≤ 0 draws no band.
    /// </summary>
    public double? LowerLimit
    {
        get => GetValue(LowerLimitProperty);
        set => SetValue(LowerLimitProperty, value);
    }

    /// <summary>
    /// Highest output (%) the assigned fans run at; the range above is shaded. <c>null</c>, NaN or ≥ 100 draws no band.
    /// </summary>
    public double? UpperLimit
    {
        get => GetValue(UpperLimitProperty);
        set => SetValue(UpperLimitProperty, value);
    }

    /// <summary>Caller-supplied caption of the lower band (e.g. "Min 25 %"); nothing is drawn when empty.</summary>
    public string? LowerLimitLabel
    {
        get => GetValue(LowerLimitLabelProperty);
        set => SetValue(LowerLimitLabelProperty, value);
    }

    /// <summary>Caller-supplied caption of the upper band; nothing is drawn when empty.</summary>
    public string? UpperLimitLabel
    {
        get => GetValue(UpperLimitLabelProperty);
        set => SetValue(UpperLimitLabelProperty, value);
    }

    /// <summary>
    /// Hysteresis (°C) shaded as <see cref="LiveTemperature"/> ± value; 0 (or no live temperature) draws no band.
    /// </summary>
    public double Hysteresis
    {
        get => GetValue(HysteresisProperty);
        set => SetValue(HysteresisProperty, value);
    }

    /// <summary>Caller-supplied caption of the hysteresis band (e.g. "±2°"); nothing is drawn when empty.</summary>
    public string? HysteresisLabel
    {
        get => GetValue(HysteresisLabelProperty);
        set => SetValue(HysteresisLabelProperty, value);
    }

    /// <summary>
    /// Raised by a double-click on the empty plot (not on a handle) with a <see cref="ChartPoint"/> (whole °C / %).
    /// Whether a point may be added there - e.g. whether the temperature is still free - is its <c>CanExecute</c>.
    /// </summary>
    public ICommand? AddPointCommand
    {
        get => GetValue(AddPointCommandProperty);
        set => SetValue(AddPointCommandProperty, value);
    }

    private PointRow? _drag;
    private PointRow? _selected; // target of the keyboard; only drawn as selected while the chart has focus
    private bool _focusedByPointer; // the current focus came from a press, see OnGotFocus

    static CurveChart()
    {
        AffectsRender<CurveChart>(LiveTemperatureProperty);
        AffectsRender<CurveChart>(InterpolationModeProperty);
        AffectsRender<CurveChart>(EvaluatorProperty);
        AffectsRender<CurveChart>(LowerLimitProperty, UpperLimitProperty, LowerLimitLabelProperty, UpperLimitLabelProperty,
            HysteresisProperty, HysteresisLabelProperty, IsFocusedProperty);
        FocusableProperty.OverrideDefaultValue<CurveChart>(true);
        PointsProperty.Changed.AddClassHandler<CurveChart>((c, e) => c.OnPointsChanged(e));
        IsReadOnlyProperty.Changed.AddClassHandler<CurveChart>((c, _) => c.OnIsReadOnlyChanged());
        RequestBringIntoViewEvent.AddClassHandler<CurveChart>((c, e) =>
        {
            if (c._focusedByPointer)
                e.Handled = true; // the chart was just pressed, so it is in view: do not scroll it under the pointer
        });
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
        ResourcesChanged += OnResourcesChanged;
        _attached = true;
        Resubscribe(Points); // Abos beim (Wieder-)Anhängen auf die aktuelle Sammlung setzen
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        ResourcesChanged -= OnResourcesChanged;
        Resubscribe(null); // Collection-/Punkt-Abos lösen: eine überlebende Sammlung darf den Chart nicht halten
        _attached = false;
        ClearInteraction(); // removals while detached go unnoticed, so do not carry a stale point over
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
        _gridBrush = ResolveBrush("GridColor", _gridBrush);
        _axisTextBrush = ResolveBrush("AxisColor", _axisTextBrush);
        _curveBrush = ResolveBrush("AccentColor", _curveBrush);
        _clampBrush = ResolveBrush("ClampColor", _clampBrush);
        _handleFill = ResolveBrush("HandleColor", _handleFill);
        _liveBrush = ResolveBrush("LiveColor", _liveBrush);
        _limitFill = ResolveBrush("AxisColor", _limitFill, LimitBandOpacity);
        _hysteresisFill = ResolveBrush("LiveColor", _hysteresisFill, HysteresisBandOpacity);
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
        _limitPen = new Pen(_axisTextBrush, 1, new DashStyle(new double[] { 3, 3 }, 0));
        _focusPen = new Pen(_curveBrush, 1.5);
    }

    private IBrush ResolveBrush(string colorKey, IBrush fallback, double opacity = 1.0) =>
        this.TryFindResource(colorKey, ActualThemeVariant, out object? value) && value is Color c
            ? new SolidColorBrush(c, opacity)
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
        ClearInteraction(); // the selected/dragged row belongs to the old collection
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
        if (e.Action == NotifyCollectionChangedAction.Reset)
            ClearInteraction();
        else if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace
                 && e.OldItems is { } removed) // a Move lists the moved row as old item, too - it stays
        {
            if (_selected is not null && removed.Contains(_selected))
                _selected = null;
            if (_drag is not null && removed.Contains(_drag))
                _drag = null;
        }
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

    // --- Interaction: drag, select, add and remove points ---------------------

    private void OnIsReadOnlyChanged()
    {
        // A read-only preview is no tab stop and keeps no selection.
        SetCurrentValue(FocusableProperty, !IsReadOnly);
        if (IsReadOnly)
            ClearInteraction();
        InvalidateVisual(); // handles and the hit-test fill depend on it
    }

    private void ClearInteraction()
    {
        _drag = null;
        _selected = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsReadOnly || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return; // read-only preview or another button: the press passes through (Moved/Released stay no-ops)

        // Avalonia's FocusManager already focuses on the tunnelling press; take the focus explicitly anyway,
        // since the key handling depends on it and this press is marked handled below.
        Focus(NavigationMethod.Pointer, e.KeyModifiers);

        Point pos = e.GetPosition(this);
        if (HitTest(pos) is { } hit)
        {
            _selected = hit;
            _drag = hit;
            e.Pointer.Capture(this);
        }
        else
        {
            _selected = null;
            if (e.ClickCount == 2)
                TryAddPoint(pos);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null) return;

        Point p = e.GetPosition(this);
        // Pointer positions stay on the axis; only the keyboard may keep a point that lies beyond it.
        MovePoint(_drag, Math.Clamp(Math.Round(TempForX(p.X)), 0, MaxTemp), Math.Round(PctForY(p.Y)));
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

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Capture taken away mid-drag (e.g. the window lost activation): end the drag, or later hovering
        // without a pressed button would keep moving the point.
        if (_drag is not null)
        {
            _drag = null;
            InvalidateVisual();
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        // This runs before the event bubbles to an enclosing ScrollViewer, which brings the newly focused control
        // into view. After a press that scrolls the chart under the pointer, so the next move (or the second click
        // of a double-click) lands elsewhere; while the pointer gave the focus, the request is therefore dropped
        // (see the RequestBringIntoView class handler). Keyboard focus keeps the scrolling.
        _focusedByPointer = e.NavigationMethod == NavigationMethod.Pointer;
        base.OnGotFocus(e);
        // Keyboard entry selects an end point, so the keys work without a pointer: Tab the first, Shift+Tab the
        // last point - Tab/Shift+Tab then walk on from there.
        if (e.NavigationMethod == NavigationMethod.Tab && !IsReadOnly && _selected is null)
        {
            List<PointRow> ordered = OrderedRows();
            _selected = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? ordered.LastOrDefault() : ordered.FirstOrDefault();
            InvalidateVisual();
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _focusedByPointer = false;
        // The selection only serves the keyboard; dropping it here lets the next Tab entry start at an end again.
        _selected = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || IsReadOnly || _selected is not { } row)
            return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            return; // leave shortcuts (undo, save, ...) to the window

        switch (e.Key)
        {
            case Key.Tab:
                // Past the last (Shift: first) point the key stays unhandled and the focus leaves the chart.
                if (Adjacent(row, backwards: (e.KeyModifiers & KeyModifiers.Shift) != 0) is { } next)
                {
                    _selected = next;
                    e.Handled = true;
                    InvalidateVisual();
                }
                break;
            case Key.Delete or Key.Back:
                // The row's own command, so one CanExecute covers this key and the remove button in the list.
                if (row.RemoveCommand is { } remove && remove.CanExecute(null))
                {
                    remove.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.Left:
                MovePoint(row, (double)row.Temperature - 1, (double)row.Percent);
                e.Handled = true;
                break;
            case Key.Right:
                MovePoint(row, (double)row.Temperature + 1, (double)row.Percent);
                e.Handled = true;
                break;
            case Key.Up:
                MovePoint(row, (double)row.Temperature, (double)row.Percent + 1);
                e.Handled = true;
                break;
            case Key.Down:
                MovePoint(row, (double)row.Temperature, (double)row.Percent - 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The single write path of dragging and the arrow keys: the temperature stays between the neighbouring
    /// points (at least 1 °C apart, so handles can neither cross nor meet under the pointer) and never moves
    /// further beyond the axis; the percentage stays within 0-100.
    /// </summary>
    private void MovePoint(PointRow row, double temperature, double percent)
    {
        // An unchanged temperature is left alone, so nudging only the percentage never moves a point that
        // has no room between its neighbours (e.g. an existing duplicate).
        if (temperature != (double)row.Temperature)
        {
            (double lo, double hi) = TemperatureBounds(row);
            if (lo <= hi) // otherwise there is no room between the neighbours and the temperature stays
                row.Temperature = (decimal)Math.Clamp(temperature, lo, hi);
        }
        row.Percent = (decimal)Math.Clamp(percent, 0, 100);
    }

    private (double Lo, double Hi) TemperatureBounds(PointRow row)
    {
        // The config allows points beyond the axis (up to 120 °C): such a point may be nudged back towards the
        // axis, but a bound of MaxTemp would pull it onto the axis on any key.
        double lo = 0, hi = Math.Max(MaxTemp, (double)row.Temperature);
        foreach (PointRow other in Rows())
        {
            if (ReferenceEquals(other, row))
                continue;
            // A point at the very same temperature (an existing duplicate) is no neighbour: once the row
            // moves off it, it becomes one and keeps the row from coming back.
            if (other.Temperature < row.Temperature)
                lo = Math.Max(lo, Math.Ceiling((double)other.Temperature + 1));
            else if (other.Temperature > row.Temperature)
                hi = Math.Min(hi, Math.Floor((double)other.Temperature - 1));
        }
        return (lo, hi);
    }

    private void TryAddPoint(Point pos)
    {
        if (!PlotRect.Contains(pos))
            return; // a double-click on the axis labels adds nothing; inside the plot T and P are on the axes
        var point = new ChartPoint((decimal)Math.Round(TempForX(pos.X)), (decimal)Math.Round(PctForY(pos.Y)));
        if (AddPointCommand is not { } add || !add.CanExecute(point))
            return;

        var before = new HashSet<PointRow>(Rows());
        add.Execute(point);
        // Select what the command added, so the keys continue with the new point right away.
        _selected = Rows().FirstOrDefault(r => !before.Contains(r) && r.Temperature == point.Temperature);
    }

    /// <summary>The handle nearest to <paramref name="pos"/> within the hit radius, so overlapping handles stay reachable.</summary>
    private PointRow? HitTest(Point pos)
    {
        PointRow? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (PointRow row in Rows())
        {
            double distance = Distance(HandleCenter(row), pos);
            if (distance <= HitRadius && distance < nearestDistance)
            {
                nearest = row;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    /// <summary>The point after (or before) <paramref name="row"/> in temperature order, or <c>null</c> at the end.</summary>
    private PointRow? Adjacent(PointRow row, bool backwards)
    {
        List<PointRow> ordered = OrderedRows();
        int index = ordered.IndexOf(row) + (backwards ? -1 : 1);
        return index >= 0 && index < ordered.Count ? ordered[index] : null;
    }

    // --- Zeichnen --------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        if (Bounds.Width < 4 || Bounds.Height < 4)
            return;

        // Avalonia hit-tests a custom control only where it draws: without this fill, a press on the empty
        // plot (double-click to add, click to deselect and focus) would fall through to the parent.
        if (!IsReadOnly)
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        DrawGrid(ctx);

        var pts = SortedPoints();
        if (pts.Count == 0)
        {
            ctx.DrawText(Text(Localizer.Instance["CurveChart.NoPoints"], _axisTextBrush), new Point(PadLeft + 6, PadTop + 6));
            return;
        }

        // Bands first, so the curve, the live marker and the handles stay on top.
        DrawLimitBands(ctx);
        DrawHysteresisBand(ctx);

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
            ctx.DrawText(Text($"{t}°", _axisTextBrush), new Point(x - 8, Bounds.Height - PadBottom + 4));
        }
        for (int p = 0; p <= 100; p += 20)
        {
            double y = YForPct(p);
            ctx.DrawLine(_gridPen, new Point(PadLeft, y), new Point(Bounds.Width - PadRight, y));
            ctx.DrawText(Text($"{p}", _axisTextBrush), new Point(4, y - 7));
        }
    }

    private void DrawLimitBands(DrawingContext ctx)
    {
        Rect plot = PlotRect;
        if (LowerLimit is { } lower && double.IsFinite(lower) && lower > 0)
        {
            double y = YForPct(Math.Min(lower, 100));
            ctx.DrawRectangle(_limitFill, null, new Rect(plot.Left, y, plot.Width, plot.Bottom - y));
            ctx.DrawLine(_limitPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (!string.IsNullOrEmpty(LowerLimitLabel))
            {
                FormattedText label = Text(LowerLimitLabel, _axisTextBrush, LabelFontSize);
                ctx.DrawText(label, new Point(plot.Right - LabelInset - label.Width, y - LabelGap - label.Height));
            }
        }
        if (UpperLimit is { } upper && double.IsFinite(upper) && upper < 100)
        {
            double y = YForPct(Math.Max(upper, 0));
            ctx.DrawRectangle(_limitFill, null, new Rect(plot.Left, plot.Top, plot.Width, y - plot.Top));
            ctx.DrawLine(_limitPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (!string.IsNullOrEmpty(UpperLimitLabel))
            {
                FormattedText label = Text(UpperLimitLabel, _axisTextBrush, LabelFontSize);
                ctx.DrawText(label, new Point(plot.Right - LabelInset - label.Width, y + LabelGap));
            }
        }
    }

    private void DrawHysteresisBand(DrawingContext ctx)
    {
        double hysteresis = Hysteresis;
        double temp = LiveTemperature;
        if (!(hysteresis > 0) || double.IsNaN(temp) || double.IsInfinity(hysteresis))
            return;
        temp = Math.Clamp(temp, 0, MaxTemp); // same clamping as the live marker the band surrounds

        Rect plot = PlotRect;
        double x0 = XForTemp(Math.Max(0, temp - hysteresis));
        double x1 = XForTemp(Math.Min(MaxTemp, temp + hysteresis));
        ctx.DrawRectangle(_hysteresisFill, null, new Rect(x0, plot.Top, x1 - x0, plot.Height));
        if (!string.IsNullOrEmpty(HysteresisLabel))
        {
            FormattedText label = Text(HysteresisLabel, _liveBrush, LabelFontSize);
            double x = Math.Clamp((x0 + x1 - label.Width) / 2, plot.Left, Math.Max(plot.Left, plot.Right - label.Width));
            ctx.DrawText(label, new Point(x, plot.Bottom - LabelGap - label.Height));
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
            Point c = HandleCenter(row);
            // The selection only matters while the keys reach the chart, so it shows with the focus.
            bool selected = IsFocused && ReferenceEquals(row, _selected);
            bool active = selected || ReferenceEquals(row, _drag);
            if (selected)
                ctx.DrawEllipse(null, _focusPen, c, FocusRingRadius, FocusRingRadius);
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
        ctx.DrawText(Text($"{temp:0}° · {pct:0}%", _liveBrush),
            new Point(Math.Min(x + 6, Bounds.Width - PadRight - 54), PadTop + 2));
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
    private Rect PlotRect => new(PadLeft, PadTop, PlotW, PlotH);

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private Point HandleCenter(PointRow row) => new(XForTemp((double)row.Temperature), YForPct((double)row.Percent));

    /// <summary>The rows in temperature order; rows at the same temperature keep their collection order.</summary>
    private List<PointRow> OrderedRows() => Rows().OrderBy(r => r.Temperature).ToList();

    private static FormattedText Text(string text, IBrush brush, double size = AxisFontSize) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
}
