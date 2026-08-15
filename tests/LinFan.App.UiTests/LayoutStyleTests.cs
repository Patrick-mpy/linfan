// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace LinFan.App.UiTests;

/// <summary>
/// Sichert die Layout-Style-Klassen (Theme/Layout.axaml, in App.axaml inkludiert): jede Border-Klasse muss
/// zur Laufzeit ihre CornerRadius-/Padding-/Background-Setter anwenden. Ein vertippter Selector oder ein
/// nicht inkludiertes Styles-File würde die Karten „nackt" rendern, ohne dass der Build bricht.
/// </summary>
public class LayoutStyleTests
{
    [AvaloniaTheory]
    [InlineData("card", 12, 18, 18, 18, 18)]
    [InlineData("panel", 14, 20, 20, 20, 20)]
    [InlineData("chip", 6, 4, 1, 4, 1)]
    [InlineData("itemCard", 10, 12, 12, 12, 12)]
    [InlineData("inset", 6, 8, 2, 8, 2)]
    [InlineData("toast", 10, 12, 10, 12, 10)]
    public void Border_class_applies_corner_padding_and_background(
        string cls, double radius, double left, double top, double right, double bottom)
    {
        var border = new Border { Classes = { cls } };
        var window = new Window { Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new CornerRadius(radius), border.CornerRadius);
        Assert.Equal(new Thickness(left, top, right, bottom), border.Padding);
        Assert.IsAssignableFrom<IBrush>(border.Background); // DynamicResource aufgelöst → Theme-fest
    }

    [AvaloniaTheory]
    [InlineData("itemCard", 0, 3, 0, 3)]
    public void Role_class_carries_its_margin(string cls, double left, double top, double right, double bottom)
    {
        var border = new Border { Classes = { cls } };
        var window = new Window { Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Thickness(left, top, right, bottom), border.Margin);
    }

    /// <summary>
    /// Die Geräte-Listenzeile (Einstellungen → Sensoren/Lüfter) trägt statt einer gefüllten Karte nur eine
    /// untere Hairline: BorderThickness unten = 1, BorderBrush aus dem Divider-Token (Theme-fest aufgelöst).
    /// </summary>
    [AvaloniaFact]
    public void DeviceRow_has_bottom_divider()
    {
        var border = new Border { Classes = { "deviceRow" } };
        var window = new Window { Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Thickness(0, 0, 0, 1), border.BorderThickness);
        Assert.IsAssignableFrom<IBrush>(border.BorderBrush);
        Assert.Equal(new Thickness(8, 9, 8, 9), border.Padding);
    }

    // ── Toast-Schweregrad: Streifen + Icon ──────────────────────────────────────

    /// <summary>
    /// Streifen und Icon eines Toasts müssen dem Schweregrad folgen - Farbe UND Symbol, denn die
    /// Status-Toasts wechseln ihre Klasse zur Laufzeit (<c>Classes.danger</c> am Binding).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("", "InfoAccent", "IconCheckCircle")]
    [InlineData("danger", "DangerAccent", "IconAlertCircle")]
    public void Toast_severity_drives_stripe_and_icon(string severity, string colorKey, string iconKey)
    {
        var stripe = new Border { Classes = { "toastStripe" } };
        var icon = new PathIcon { Classes = { "toastIcon" } };
        var toast = new Border { Classes = { "toast" }, Child = new StackPanel { Children = { stripe, icon } } };
        if (severity.Length > 0)
            toast.Classes.Add(severity);

        var window = new Window { Content = toast };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(5, stripe.Width);
        Assert.Equal(TokenColor(colorKey, window), Assert.IsAssignableFrom<ISolidColorBrush>(stripe.Background).Color);
        Assert.Equal(TokenColor(colorKey, window), Assert.IsAssignableFrom<ISolidColorBrush>(icon.Foreground).Color);
        Assert.Same(Resource(iconKey, window), icon.Data);
    }

    // ── Gefüllte Aktions-Buttons ────────────────────────────────────────────────

    /// <summary>
    /// Fluent setzt Hintergrund und Schrift je Zustand auf dem ContentPresenter des Templates - ein Style,
    /// der nur den Button selbst setzt, würde zwar bauen, aber nichts färben. Ruhe- und Hover-Zustand daher
    /// am gerenderten Presenter prüfen.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("primary", "AccentFill", "OnAccent", "AccentHover")]
    [InlineData("danger", "DangerFill", "OnDanger", "DangerHover")]
    public void Action_button_class_fills_presenter(string cls, string bgKey, string fgKey, string hoverKey)
    {
        var button = new Button { Classes = { cls }, Content = "X", Width = 100, Height = 30 };
        var window = new Window { Content = button, Width = 200, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(TokenColor(bgKey, window), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
        Assert.Equal(TokenColor(fgKey, window), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color);

        // Zeiger über den Button: der Hover-Setter muss greifen, sonst fiele die Fläche auf Fluents Default zurück.
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
                       ?? throw new InvalidOperationException("Button ist nicht gerendert.");
        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();

        Assert.True(button.IsPointerOver);
        Assert.Equal(TokenColor(hoverKey, window), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
    }

    /// <summary>
    /// Regression: Buttons mit Icon+Text bringen ihre eigenen TextBlocks/PathIcons mit - die erben die
    /// Schriftfarbe des Presenters nicht und blieben sonst hell auf heller Fläche (real aufgetreten).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("primary", "OnAccent")]
    [InlineData("danger", "OnDanger")]
    public void Action_button_colors_its_own_icon_and_label(string cls, string fgKey)
    {
        var label = new TextBlock { Text = "Übernehmen" };
        var icon = new PathIcon();
        var button = new Button
        {
            Classes = { cls },
            Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { icon, label } },
        };
        var window = new Window { Content = button };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TokenColor(fgKey, window), Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);
        Assert.Equal(TokenColor(fgKey, window), Assert.IsAssignableFrom<ISolidColorBrush>(icon.Foreground).Color);
    }

    /// <summary>Ein gesperrter Aktions-Button darf nicht knallbunt stehen bleiben (Fluents :disabled läge sonst unter unserem Setter).</summary>
    [AvaloniaTheory]
    [InlineData("primary")]
    [InlineData("danger")]
    public void Action_button_disabled_loses_its_fill(string cls)
    {
        var button = new Button { Classes = { cls }, Content = "X", IsEnabled = false };
        var window = new Window { Content = button };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(TokenColor("Divider", window), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
        Assert.Equal(TokenColor("TextFaintest", window), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color);
    }

    private static object Resource(string key, Window window)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out object? value), $"Ressource '{key}' fehlt.");
        return value!;
    }

    private static Color TokenColor(string key, Window window) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(Resource(key, window)).Color;
}
