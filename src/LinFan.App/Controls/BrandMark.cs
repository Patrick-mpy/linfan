// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LinFan.App.Controls;

/// <summary>
/// The LinFan symbol (three swept blades with a shaded spine around a hub) as a vector control - the
/// drawing mirrors <c>Assets/linfan-icon.svg</c> one to one. Avalonia cannot render SVG, and an SVG
/// library would be a dependency for a single image, so the mark is drawn here (theme brushes like
/// <see cref="CurveChart"/>). Purely decorative: no state, no interaction; size it via Width/Height.
/// </summary>
public sealed class BrandMark : Control
{
    // One blade, drawn in the 200×200 design space of the SVG and repeated at 120°/240° around the hub.
    private static readonly Geometry Blade =
        Geometry.Parse("M100 100 C122 88 126 54 100 30 C84 50 84 80 100 100 Z");

    // Spine of a blade: the mid-line between both blade edges, cut short of the tip. Stroked, not filled.
    private static readonly Geometry Spine =
        Geometry.Parse("M100 100 C102.6 85.9 104.5 59.4 101.5 38.3");

    private static readonly Point Hub = new(100, 100);

    // Ink box of the three blades: the tips sit at radius 70 around the hub, so the mark spans 100±61
    // horizontally and 30..136 vertically. Drawing into exactly this box keeps a lockup free of the
    // invisible padding the square SVG viewBox would add.
    private const double DesignLeft = 39;
    private const double DesignTop = 30;
    private const double DesignWidth = 122;
    private const double DesignHeight = 106;

    // Theme colours (SemanticColors.axaml; dark fallbacks before the control is attached / headless).
    private Color _accent = Color.Parse("#38BDF8");
    private Color _shade = Color.Parse("#0EA5E9");

    private IBrush _accentBrush = null!;
    private IBrush _shadeBrush = null!;
    private Pen _spinePen = null!;

    public BrandMark() => RebuildBrushes();

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
        _shade = ResolveColor("AccentShadeColor", _shade);
        RebuildBrushes();
    }

    private void RebuildBrushes()
    {
        _accentBrush = new SolidColorBrush(_accent);
        _shadeBrush = new SolidColorBrush(_shade);
        _spinePen = new Pen(_shadeBrush, 4) { LineCap = PenLineCap.Round };
    }

    private Color ResolveColor(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is Color c ? c : fallback;

    /// <summary>Keeps the mark's aspect ratio, so setting only Height (as in the header lockup) is enough.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        const double aspect = DesignWidth / DesignHeight;
        double w = availableSize.Width, h = availableSize.Height;

        if (double.IsInfinity(w) && double.IsInfinity(h))
            return new Size(DesignWidth, DesignHeight);
        if (double.IsInfinity(w))
            return new Size(h * aspect, h);
        if (double.IsInfinity(h))
            return new Size(w, w / aspect);
        return w / aspect <= h ? new Size(w, w / aspect) : new Size(h * aspect, h);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        double scale = Math.Min(Bounds.Width / DesignWidth, Bounds.Height / DesignHeight);
        if (scale <= 0)
            return;

        // Design space → control bounds: shift the ink box to the origin, scale uniformly, centre the rest.
        Matrix fit = Matrix.CreateTranslation(-DesignLeft, -DesignTop)
                     * Matrix.CreateScale(scale, scale)
                     * Matrix.CreateTranslation(
                         (Bounds.Width - DesignWidth * scale) / 2,
                         (Bounds.Height - DesignHeight * scale) / 2);

        using (ctx.PushTransform(fit))
        {
            // Same z-order as the SVG: blades, then the spines on top of them, then the hub covering both inner ends.
            for (int i = 0; i < 3; i++)
                using (ctx.PushTransform(BladeRotation(i)))
                    ctx.DrawGeometry(_accentBrush, null, Blade);

            for (int i = 0; i < 3; i++)
                using (ctx.PushTransform(BladeRotation(i)))
                    ctx.DrawGeometry(null, _spinePen, Spine);

            ctx.DrawEllipse(_accentBrush, null, Hub, 14, 14);
            ctx.DrawEllipse(_shadeBrush, null, Hub, 6.5, 6.5);
        }
    }

    private static Matrix BladeRotation(int index) =>
        Matrix.CreateTranslation(-Hub.X, -Hub.Y)
        * Matrix.CreateRotation(index * 2 * Math.PI / 3)
        * Matrix.CreateTranslation(Hub.X, Hub.Y);
}
