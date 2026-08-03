// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using LinFan.App.Controllers;
using LinFan.Core.Models;

namespace LinFan.App.Controls;

/// <summary>
/// Wiederverwendbare, interaktive Gehäuse-Vorschau zur Wahl einer <see cref="FanLocation"/>: zeichnet die
/// Silhouette mit anklickbaren Zonen und Bauteilen (Layout/Hit-Test in <see cref="FanLocationLayout"/>),
/// färbt sie nach Luftrichtung (<see cref="FanLocationOption.DirectionOf"/>) und hebt die Auswahl hervor.
/// Reine View-Mechanik — kennt nur die gebundene <see cref="SelectedLocation"/>, weder Geräte-Tab noch
/// Onboarding; dadurch an beiden Stellen identisch einsetzbar. Theme-Brushes wie <see cref="CurveChart"/>.
/// </summary>
public sealed class FanLocationDiagram : Control
{
    public static readonly StyledProperty<FanLocation> SelectedLocationProperty =
        AvaloniaProperty.Register<FanLocationDiagram, FanLocation>(
            nameof(SelectedLocation), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Aktuell gewählte Einbau-Position (Two-Way: Klick/Hover setzt sie, Bindung schreibt sie).</summary>
    public FanLocation SelectedLocation
    {
        get => GetValue(SelectedLocationProperty);
        set => SetValue(SelectedLocationProperty, value);
    }

    // Theme-abhängige Farben (aus SemanticColors.axaml; Dunkel-Fallbacks vor dem Attach / headless).
    private Color _accent = Color.Parse("#38BDF8");
    private Color _grid = Color.Parse("#26262E");
    private Color _label = Color.Parse("#A1A1AA");
    private Color _intake = Color.Parse("#38BDF8");
    private Color _exhaust = Color.Parse("#F59E0B");
    private Color _internal = Color.Parse("#6B7280");

    // Aus den Farben abgeleitete Pinsel/Stifte, einmal je Farb-Auflösung gebaut statt pro Render-Frame (Render
    // feuert bei jeder Hover-/Zeiger-Bewegung). Neu erzeugt in RebuildBrushes(): im ctor aus den Fallback-Farben,
    // danach bei jeder ResolveColors()-Auflösung (Attach / Theme-Wechsel).
    private Pen _gridPen = null!;
    private Pen _emphasisHoverPen = null!;    // Hover-Rahmen (Breite 1,5)
    private Pen _emphasisSelectedPen = null!; // Auswahl-Rahmen (Breite 2)
    private IBrush _accentBrush = null!;       // Auswahl-Label
    private IBrush _labelBrush = null!;        // übrige Labels
    // Füllpinsel je Richtungsfarbe × Zustands-Deckkraft (ausgewählt/hover/normal) — beide Achsen stammen aus
    // kleinen festen Mengen, daher vorberechnet statt pro Region neu (variable Deckkraft 0,34/0,22/0,14).
    private readonly Dictionary<Color, (IBrush Selected, IBrush Hover, IBrush Normal)> _fills = new();

    private FanLocation? _hover;

    static FanLocationDiagram()
    {
        AffectsRender<FanLocationDiagram>(SelectedLocationProperty);
    }

    public FanLocationDiagram()
    {
        ClipToBounds = true;
        MinWidth = 280;
        MinHeight = 240;
        Cursor = new Cursor(StandardCursorType.Hand);
        RebuildBrushes(); // Pinsel/Stifte aus den Fallback-Farben, damit ein Render vor dem Attach nicht auf null trifft
    }

    // --- Theme-Farben auflösen (App-Resourcen erst ab dem Attach erreichbar) -------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResolveColors();
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ResolveColors();
        InvalidateVisual();
    }

    private void ResolveColors()
    {
        _accent = ResolveColor("AccentColor", _accent);
        _grid = ResolveColor("GridColor", _grid);
        _label = ResolveColor("AxisColor", _label);
        _intake = ResolveColor("IntakeColor", _intake);
        _exhaust = ResolveColor("ExhaustColor", _exhaust);
        _internal = ResolveColor("InternalColor", _internal);
        RebuildBrushes(); // Pinsel/Stifte an die (neu) aufgelösten Farben koppeln
    }

    /// <summary>Baut die gecachten Pinsel/Stifte aus den aktuellen Farben — exakt die Strichbreiten/Deckkräfte der Draw-Pfade.</summary>
    private void RebuildBrushes()
    {
        _accentBrush = new SolidColorBrush(_accent);
        _labelBrush = new SolidColorBrush(_label);
        _gridPen = new Pen(new SolidColorBrush(_grid), 1);
        _emphasisHoverPen = new Pen(_accentBrush, 1.5);
        _emphasisSelectedPen = new Pen(_accentBrush, 2);

        _fills.Clear();
        foreach (Color c in new[] { _intake, _exhaust, _internal, _label })
            _fills[c] = (new SolidColorBrush(c, 0.34), new SolidColorBrush(c, 0.22), new SolidColorBrush(c, 0.14));
    }

    private Color ResolveColor(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is Color c ? c : fallback;

    // --- Interaktion -----------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (FanLocationLayout.Hit(e.GetPosition(this), Bounds.Size) is { } loc)
        {
            // Klick auf den bereits gewählten Mount lässt dessen Richtung stehen (umschalten läuft über den Schalter).
            if (!FanLocationLayout.SameMount(loc, SelectedLocation))
                SelectedLocation = loc;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        FanLocation? loc = FanLocationLayout.Hit(e.GetPosition(this), Bounds.Size);
        if (loc != _hover)
        {
            _hover = loc;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    // --- Zeichnen --------------------------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        IReadOnlyList<FanLocationLayout.Region> regions = FanLocationLayout.Build(Bounds.Size);

        // Durchgang 1: Füllung, Grundrahmen und Beschriftung. Der hervorgehobene Rahmen kommt erst danach
        // oben drauf — sonst übermalt ihn der Rahmen des benachbarten (später gezeichneten) Felds an der
        // gemeinsamen Kante (das war das „Clipping" unter den nicht ausgewählten Feldern).
        foreach (FanLocationLayout.Region r in regions)
        {
            bool selected = FanLocationLayout.SameMount(r.Location, SelectedLocation);
            bool hover = !selected && _hover == r.Location;

            // Für die gewählte Zone die echte (ggf. umgeschaltete) Richtung zeigen, sonst die konventionelle.
            FanLocation effective = selected ? SelectedLocation : r.Location;
            (IBrush sel, IBrush hov, IBrush norm) = _fills[DirectionColor(effective)];
            IBrush fill = selected ? sel : (hover ? hov : norm);
            ctx.DrawRectangle(fill, _gridPen, r.Bounds, 5, 5);
            DrawLabel(ctx, FanLocationLayout.ShortLabel(effective), r.Bounds, selected ? _accentBrush : _labelBrush, selected);
        }

        // Durchgang 2: Hover- dann Auswahl-Rahmen zuletzt → liegen garantiert über allen Nachbarn.
        if (_hover is { } h && !FanLocationLayout.SameMount(h, SelectedLocation))
            DrawEmphasisBorder(ctx, regions, h, _emphasisHoverPen);
        DrawEmphasisBorder(ctx, regions, SelectedLocation, _emphasisSelectedPen);
    }

    private static void DrawEmphasisBorder(DrawingContext ctx,
        IReadOnlyList<FanLocationLayout.Region> regions, FanLocation loc, Pen pen)
    {
        foreach (FanLocationLayout.Region r in regions)
        {
            if (!FanLocationLayout.SameMount(r.Location, loc))
                continue;
            ctx.DrawRectangle(null, pen, r.Bounds, 5, 5);
            return;
        }
    }

    private Color DirectionColor(FanLocation loc) => FanLocationOption.DirectionOf(loc) switch
    {
        AirflowDirection.Intake => _intake,
        AirflowDirection.Exhaust => _exhaust,
        AirflowDirection.Internal => _internal,
        _ => _label, // Unbekannt (nicht zugeordnet / Sonstige) → neutral
    };

    private static void DrawLabel(DrawingContext ctx, string text, Rect area, IBrush brush, bool bold)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            11, brush)
        {
            MaxTextWidth = Math.Max(10, area.Width - 6),
            MaxTextHeight = Math.Max(10, area.Height),
            TextAlignment = TextAlignment.Center,
        };
        var origin = new Point(area.X + 3, area.Center.Y - ft.Height / 2);
        ctx.DrawText(ft, origin);
    }
}
