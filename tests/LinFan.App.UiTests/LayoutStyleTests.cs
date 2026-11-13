// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Controls;

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
    [InlineData("itemCard", 10, 12, 12, 12, 12)]
    [InlineData("searchBox", 8, 10, 0, 10, 0)]
    [InlineData("toast", 10, 12, 10, 12, 10)]
    [InlineData("banner", 10, 14, 12, 14, 12)]
    [InlineData("unsavedBar", 12, 16, 10, 12, 10)]
    [InlineData("bulkBar", 8, 14, 8, 10, 8)]
    [InlineData("groupSection", 10, 0, 0, 0, 0)]
    [InlineData("block", 10, 14, 12, 14, 12)]
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
    [InlineData("banner", 0, 0, 0, 12)]
    [InlineData("unsavedBar", 16, 0, 16, 20)]
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

    // ── Surfaces: banner, unsaved bar, bulk bar, dirty dot, group section ──────

    [AvaloniaTheory]
    [InlineData("unsavedBar", "BgCardAlt")]
    [InlineData("bulkBar", "BgCardAlt")]
    public void Border_class_uses_its_surface_token(string cls, string bgKey)
    {
        var border = new Border { Classes = { cls } };
        Window window = Show(border);

        Assert.Equal(TokenColor(bgKey, window), Brush(border.Background).Color);
    }

    /// <summary>
    /// Surface, text and icon follow the severity class; info is the default, so a banner without (or with an
    /// explicit "info") class never renders naked. Buttons inside keep their own foreground - the banner only
    /// colours its text, otherwise "Reconnect" would turn into banner-coloured text on a button fill.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("", "InfoBg", "InfoText", "InfoAccent")]
    [InlineData("info", "InfoBg", "InfoText", "InfoAccent")]
    [InlineData("warning", "WarningBg", "WarningText", "WarningText")]
    [InlineData("danger", "DangerBg", "DangerText", "DangerAccent")]
    public void Banner_severity_drives_surface_text_and_icon(string severity, string bgKey, string textKey, string iconKey)
    {
        var title = new TextBlock { Classes = { "bannerTitle" }, Text = "Hintergrunddienst nicht erreichbar" };
        var body = new TextBlock { Classes = { "bannerText" }, Text = "Läuft der Dienst?" };
        var icon = new PathIcon { Classes = { "bannerIcon" } };
        var action = new Button { Content = "Erneut verbinden" };
        var banner = new Border { Classes = { "banner" }, Child = new StackPanel { Children = { icon, title, body, action } } };
        if (severity.Length > 0)
            banner.Classes.Add(severity);
        Window window = Show(banner);

        Assert.Equal(TokenColor(bgKey, window), Brush(banner.Background).Color);
        Assert.Equal(TokenColor(textKey, window), Brush(title.Foreground).Color);
        Assert.Equal(TokenColor(textKey, window), Brush(body.Foreground).Color);
        Assert.Equal(TokenColor(iconKey, window), Brush(icon.Foreground).Color);
        Assert.Equal(20, icon.Width);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        Assert.Equal(12, body.FontSize);
        Assert.NotEqual(TokenColor(textKey, window), Brush(action.Foreground).Color);
    }

    /// <summary>
    /// A block is a filled card with a hairline frame; .danger only reddens the frame, the surface stays the card's, so
    /// the text on it keeps its normal contrast. blockTitle/blockSub are its heading and the line under it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("", "Divider")]
    [InlineData("danger", "DangerAccent")]
    public void Block_frames_a_card_and_danger_only_reddens_the_frame(string variant, string frameKey)
    {
        var title = new TextBlock { Classes = { "blockTitle" }, Text = "Backup" };
        var sub = new TextBlock { Classes = { "blockSub" }, Text = "In einer Datei." };
        var block = new Border { Classes = { "block" }, Child = new StackPanel { Children = { title, sub } } };
        if (variant.Length > 0)
            block.Classes.Add(variant);
        Window window = Show(block);

        Assert.Equal(TokenColor("BgCard", window), Brush(block.Background).Color);
        Assert.Equal(TokenColor(frameKey, window), Brush(block.BorderBrush).Color);
        Assert.Equal(new Thickness(1), block.BorderThickness);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        Assert.Equal(TokenColor("TextHeading", window), Brush(title.Foreground).Color);
        Assert.Equal(12, sub.FontSize);
        Assert.Equal(TokenColor("TextFaint", window), Brush(sub.Foreground).Color);
        Assert.Equal(TextWrapping.Wrap, sub.TextWrapping);
    }

    /// <summary>
    /// The bar floats over the content (bottom, centred, capped width) and its shadow is a per-theme token -
    /// switching the window to light must swap the shadow, not keep the dark one.
    /// </summary>
    [AvaloniaFact]
    public void UnsavedBar_floats_with_warning_frame_and_theme_shadow()
    {
        var bar = new Border { Classes = { "unsavedBar" } };
        Window window = Show(bar);

        Assert.Equal(TokenColor("Warning", window), Brush(bar.BorderBrush).Color);
        Assert.Equal(new Thickness(1), bar.BorderThickness);
        Assert.Equal(820, bar.MaxWidth);
        Assert.Equal(VerticalAlignment.Bottom, bar.VerticalAlignment);
        Assert.Equal(Resource("ShadowLg", window), bar.BoxShadow);

        window.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Resource("ShadowLg", window), bar.BoxShadow);
    }

    [AvaloniaFact]
    public void DirtyDot_is_a_small_warning_dot()
    {
        var dot = new Ellipse { Classes = { "dirtyDot" } };
        Window window = Show(dot);

        Assert.Equal(7, dot.Width);
        Assert.Equal(7, dot.Height);
        Assert.Equal(TokenColor("Warning", window), Brush(dot.Fill).Color);
    }

    /// <summary>
    /// The view toggles <c>dropTarget</c> while a row is dragged over a section, so the class must switch the
    /// look both ways at runtime. The resting background is transparent, not null: only then does the empty
    /// area of a section take part in hit testing and receive the drop.
    /// </summary>
    [AvaloniaFact]
    public void GroupSection_highlights_while_it_is_the_drop_target()
    {
        var header = new Border { Classes = { "groupHeader" } };
        var section = new Border { Classes = { "groupSection" }, Child = new StackPanel { Children = { header } } };
        Window window = Show(section);

        Assert.Equal(TokenColor("Divider", window), Brush(section.BorderBrush).Color);
        Assert.Equal(Colors.Transparent, Brush(section.Background).Color);
        Assert.True(section.ClipToBounds);
        Assert.Equal(new Thickness(12, 6, 8, 6), header.Padding);
        Assert.Equal(new Thickness(0, 0, 0, 1), header.BorderThickness);
        Assert.Equal(TokenColor("BgCard", window), Brush(header.Background).Color);

        section.Classes.Add("dropTarget");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TokenColor("Accent", window), Brush(section.BorderBrush).Color);
        Assert.Equal(TokenColor("AccentTint", window), Brush(section.Background).Color);

        section.Classes.Remove("dropTarget");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TokenColor("Divider", window), Brush(section.BorderBrush).Color);
        Assert.Equal(Colors.Transparent, Brush(section.Background).Color);
    }

    /// <summary>A mouse-only handle: Tab must skip it (the prototype's grip is a non-focusable span).</summary>
    [AvaloniaFact]
    public void Grip_button_is_a_quiet_drag_handle()
    {
        var icon = new PathIcon();
        var grip = new Button { Classes = { "grip" }, Content = icon };
        Window window = Show(grip);

        Assert.False(grip.IsTabStop);
        Assert.Equal("SizeAll", grip.Cursor?.ToString());
        Assert.Equal(16, icon.Width);
        Assert.Equal(TokenColor("TextFaintest", window), Brush(icon.Foreground).Color);

        window.MouseMove(Center(grip, window));
        Dispatcher.UIThread.RunJobs();

        Assert.True(grip.IsPointerOver);
        Assert.Equal(TokenColor("TextSecondary", window), Brush(icon.Foreground).Color);
        ContentPresenter presenter = grip.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(Colors.Transparent, Brush(presenter.Background).Color);
    }

    [AvaloniaFact]
    public void Chip_is_an_inset_surface_that_stays_raised_while_its_menu_is_open()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = "Deutsch" });
        var chip = new Button { Classes = { "chip" }, Content = "Sprache", Flyout = menu };
        Window window = Show(chip);
        ContentPresenter presenter = chip.GetVisualDescendants().OfType<ContentPresenter>().First();

        Assert.Equal(0, chip.BorderThickness.Left);
        Assert.Equal(TokenColor("BgInset", window), Brush(presenter.Background).Color);

        menu.ShowAt(chip);
        Dispatcher.UIThread.RunJobs();

        Assert.True(menu.IsOpen);
        Assert.Equal(TokenColor("BgCardAlt", window), Brush(presenter.Background).Color);
    }

    // ── Segmented control (ListBox.seg) ─────────────────────────────────────────

    private static ListBox Segmented(out Window window)
    {
        var seg = new ListBox
        {
            Classes = { "seg" },
            SelectionMode = SelectionMode.Single | SelectionMode.AlwaysSelected,
            ItemsSource = new[] { "Linear", "Spline", "Stufen" },
            ItemTemplate = new FuncDataTemplate<string>((label, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new PathIcon(), new TextBlock { Text = label } },
            }),
        };
        window = Show(seg);
        return seg;
    }

    private static ListBoxItem Item(ListBox list, int index) =>
        Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));

    private static ContentPresenter Presenter(ListBoxItem item) =>
        item.GetVisualDescendants().OfType<ContentPresenter>().First();

    [AvaloniaFact]
    public void Seg_lays_its_options_out_in_a_row_on_an_inset_track()
    {
        ListBox seg = Segmented(out Window window);

        Assert.IsType<WrapPanel>(seg.ItemsPanelRoot);
        Assert.Equal(TokenColor("BgInset", window), Brush(seg.Background).Color);
        Assert.Equal(new CornerRadius(8), seg.CornerRadius);
        Assert.Equal(Item(seg, 0).Bounds.Y, Item(seg, 1).Bounds.Y);
        Assert.True(Item(seg, 1).Bounds.X > Item(seg, 0).Bounds.Right);
    }

    /// <summary>
    /// The selected option is a raised card (surface, hairline, bold) with an accent icon; the others stay muted
    /// on the track. Checked on DataTemplate content, which is what the views bind.
    /// </summary>
    [AvaloniaFact]
    public void Seg_selected_option_is_a_raised_card()
    {
        ListBox seg = Segmented(out Window window);
        Assert.Equal(0, seg.SelectedIndex);

        ListBoxItem selected = Item(seg, 0);
        ListBoxItem other = Item(seg, 1);
        Assert.Equal(TokenColor("BgCard", window), Brush(Presenter(selected).Background).Color);
        Assert.Equal(TokenColor("Divider", window), Brush(Presenter(selected).BorderBrush).Color);
        Assert.Equal(Resource("ShadowSm", window), Presenter(selected).BoxShadow);
        Assert.Equal(TokenColor("TextPrimary", window), Brush(selected.GetVisualDescendants().OfType<TextBlock>().First().Foreground).Color);
        Assert.Equal(FontWeight.SemiBold, selected.GetVisualDescendants().OfType<TextBlock>().First().FontWeight);
        Assert.Equal(TokenColor("Accent", window), Brush(selected.GetVisualDescendants().OfType<PathIcon>().First().Foreground).Color);

        Assert.Equal(Colors.Transparent, Brush(Presenter(other).Background).Color);
        Assert.Equal(Colors.Transparent, Brush(Presenter(other).BorderBrush).Color);
        Assert.Equal(TokenColor("TextMuted", window), Brush(other.GetVisualDescendants().OfType<TextBlock>().First().Foreground).Color);
        // Same ring width on both - selecting must not shift the label.
        Assert.Equal(Presenter(selected).BorderThickness, Presenter(other).BorderThickness);
    }

    [AvaloniaFact]
    public void Seg_hover_fills_only_unselected_options()
    {
        ListBox seg = Segmented(out Window window);

        window.MouseMove(Center(Item(seg, 1), window));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TokenColor("ControlHover", window), Brush(Presenter(Item(seg, 1)).Background).Color);

        window.MouseMove(Center(Item(seg, 0), window));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TokenColor("BgCard", window), Brush(Presenter(Item(seg, 0)).Background).Color);
    }

    [AvaloniaFact]
    public void Seg_arrow_keys_move_the_selection()
    {
        ListBox seg = Segmented(out Window window);
        Item(seg, 0).Focus();

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, seg.SelectedIndex);

        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, seg.SelectedIndex);
    }

    // ── Stepper (NumericUpDown theme) ──────────────────────────────────────────

    private static NumericUpDown Stepper(decimal value, decimal increment) => new()
    {
        Theme = Assert.IsType<ControlTheme>(Application.Current!.FindResource("Stepper")),
        Value = value,
        Minimum = 0,
        Maximum = 20,
        Increment = increment,
        Width = 130,
        InnerRightContent = "°C",
    };

    private static TextBox Field(NumericUpDown stepper) =>
        stepper.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "PART_TextBox");

    private static RepeatButton SpinButton(NumericUpDown stepper, string name) =>
        stepper.GetVisualDescendants().OfType<RepeatButton>().First(b => b.Name == name);

    private static void Click(Window window, Control target)
    {
        Point point = Center(target, window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Stepper_renders_minus_value_unit_plus()
    {
        NumericUpDown stepper = Stepper(2.5m, 0.5m);
        Window window = Show(stepper);

        TextBox field = Field(stepper);
        double Left(Control c) => c.TranslatePoint(default, stepper)!.Value.X;
        Assert.True(Left(SpinButton(stepper, "PART_DecreaseButton")) < Left(field));
        Assert.True(Left(field) < Left(SpinButton(stepper, "PART_IncreaseButton")));
        Assert.Equal("2.5", field.Text);

        TextBlock unit = field.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "°C");
        Assert.Equal(TokenColor("TextFaint", window), Brush(unit.Foreground).Color);
    }

    /// <summary>
    /// NumericUpDown commits on every keystroke, so input is typed one character at a time here: with "," read
    /// as a decimal separator "1" "," "5" ends at 1.5 (a rejected "1," would silently leave it at 1). Display
    /// stays invariant after blur; garbage is rejected and reverted.
    /// </summary>
    [AvaloniaFact]
    public void Stepper_takes_a_decimal_comma_typed_key_by_key()
    {
        NumericUpDown stepper = Stepper(5m, 0.5m);
        var other = new TextBox();
        Window window = Show(new StackPanel { Children = { other, stepper } });
        TextBox field = Field(stepper);

        void Type(params string[] keys)
        {
            field.Focus();
            field.SelectAll();
            foreach (string key in keys)
            {
                window.KeyTextInput(key);
                Dispatcher.UIThread.RunJobs();
            }

            other.Focus();
            Dispatcher.UIThread.RunJobs();
        }

        Type("1", ",", "5");
        Assert.Equal(1.5m, stepper.Value);
        Assert.Equal("1.5", field.Text);

        Type("7", ".", "5");
        Assert.Equal(7.5m, stepper.Value);

        Type("x");
        Assert.Equal(7.5m, stepper.Value);
        Assert.Equal("7.5", field.Text);

        stepper.Value += 0.5m;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("8", field.Text);
    }

    /// <summary>
    /// The wheel spins a stepper only while keyboard focus is inside it. A +/- click must therefore not put the
    /// focus into the stepper - otherwise scrolling the page past it right after a click would change the value
    /// instead of scrolling.
    /// </summary>
    [AvaloniaFact]
    public void Stepper_buttons_step_without_taking_focus_so_the_wheel_keeps_scrolling()
    {
        NumericUpDown stepper = Stepper(5m, 0.5m);
        var other = new TextBox();
        var page = new ScrollViewer
        {
            Height = 250,
            Content = new StackPanel { Children = { other, stepper, new Border { Height = 2000 } } },
        };
        Window window = Show(page);
        other.Focus();

        Click(window, SpinButton(stepper, "PART_IncreaseButton"));
        Assert.Equal(5.5m, stepper.Value);
        Assert.False(stepper.IsKeyboardFocusWithin);

        window.MouseWheel(Center(stepper, window), new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5.5m, stepper.Value);
        Assert.True(page.Offset.Y > 0, "The page should have scrolled.");
    }

    [AvaloniaFact]
    public void Stepper_up_key_steps_while_typing_and_the_frame_shows_focus()
    {
        NumericUpDown stepper = Stepper(5m, 0.5m);
        Window window = Show(stepper);
        TextBox field = Field(stepper);

        Click(window, field);
        Assert.True(field.IsFocused);
        Assert.Equal(TokenColor("Accent", window), Brush(stepper.BorderBrush).Color);
        Border fieldChrome = field.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
        Assert.Equal(new Thickness(0), fieldChrome.BorderThickness);

        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5.5m, stepper.Value);
    }

    /// <summary>
    /// A TextBox handles Ctrl+Z itself (control case below). The stepper's must not, so Ctrl+Z still reaches the
    /// window's undo after the user typed into a stepper.
    /// </summary>
    [AvaloniaFact]
    public void Stepper_leaves_ctrl_z_to_the_window()
    {
        NumericUpDown stepper = Stepper(5m, 1m);
        var plain = new TextBox();
        Window window = Show(new StackPanel { Children = { plain, stepper } });
        bool handled = false;
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Key == Key.Z) handled = e.Handled; },
            RoutingStrategies.Bubble, handledEventsToo: true);

        bool CtrlZHandledIn(TextBox box)
        {
            box.Focus();
            window.KeyTextInput("7");
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Dispatcher.UIThread.RunJobs();
            return handled;
        }

        Assert.True(CtrlZHandledIn(plain));
        Assert.False(CtrlZHandledIn(Field(stepper)));
    }

    [AvaloniaFact]
    public void Stepper_dims_the_button_at_its_limit()
    {
        NumericUpDown stepper = Stepper(20m, 1m);
        Window window = Show(stepper);

        RepeatButton increase = SpinButton(stepper, "PART_IncreaseButton");
        Assert.False(increase.IsEnabled);
        Assert.Equal(TokenColor("TextFaintest", window), Brush(increase.Foreground).Color);
        Assert.Equal(TokenColor("TextSecondary", window), Brush(SpinButton(stepper, "PART_DecreaseButton").Foreground).Color);
    }

    /// <summary>
    /// Disabled and in error the frame reads like a TextBox's (the stepper is a text field). The validation message goes
    /// below the frame: the frame keeps its height instead of being squeezed by it.
    /// </summary>
    [AvaloniaFact]
    public void Stepper_disabled_and_error_frames_read_like_a_text_box()
    {
        NumericUpDown disabled = Stepper(5m, 1m);
        disabled.IsEnabled = false;
        NumericUpDown error = Stepper(5m, 1m);
        Window window = Show(new StackPanel { Spacing = 8, Children = { disabled, error } });
        Color Fluent(string key) => Brush(Resource(key, window) as IBrush).Color;

        Assert.Equal(Fluent("TextControlBorderBrushDisabled"), Brush(disabled.BorderBrush).Color);
        Assert.Equal(Fluent("TextControlBackgroundDisabled"), Brush(disabled.Background).Color);

        double frameHeight = error.GetVisualDescendants().OfType<ButtonSpinner>().Single().Bounds.Height;
        DataValidationErrors.SetError(error, new DataValidationException("Ungültig"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Fluent("SystemControlErrorTextForegroundBrush"), Brush(error.BorderBrush).Color);
        Assert.Equal(32, frameHeight);
        Assert.Equal(frameHeight, error.GetVisualDescendants().OfType<ButtonSpinner>().Single().Bounds.Height);
    }

    // ── Status chip ───────────────────────────────────────────────────────────

    /// <summary>The chip is styled as a <c>Border.statusChip</c>; its tone picks exactly one tone class and its colours.</summary>
    [AvaloniaFact]
    public void StatusChip_carries_the_statusChip_look_in_its_tone()
    {
        var chip = new StatusChip { Text = "Sensor fehlt", Tone = StatusTone.Warn };
        Window window = Show(chip);
        Ellipse dot = chip.GetVisualDescendants().OfType<Ellipse>().Single();
        TextBlock text = chip.GetVisualDescendants().OfType<TextBlock>().Single();

        Assert.Equal("Sensor fehlt", text.Text);
        Assert.Equal(new CornerRadius(999), chip.CornerRadius);
        Assert.Equal(new Thickness(8, 1, 8, 1), chip.Padding);
        Assert.Equal(TokenColor("Divider", window), Brush(chip.BorderBrush).Color);
        Assert.Equal(TokenColor("Warning", window), Brush(dot.Fill).Color);
        Assert.Equal(TokenColor("WarningText", window), Brush(text.Foreground).Color);

        chip.Tone = StatusTone.Ok;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TokenColor("Success", window), Brush(dot.Fill).Color);
        Assert.Equal(TokenColor("TextSecondary", window), Brush(text.Foreground).Color);
        Assert.Equal(new[] { "statusChip", "ok" }, chip.Classes.Where(c => !c.StartsWith(':')));

        chip.Tone = StatusTone.Muted;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TokenColor("TextFaint", window), Brush(dot.Fill).Color);
        Assert.Equal(TokenColor("TextFaint", window), Brush(text.Foreground).Color);
    }

    // ── Menus ─────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void MenuFlyout_uses_the_app_surface()
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem { Header = "CPU" });
        flyout.Items.Add(new Separator());
        flyout.Items.Add(new MenuItem { Header = "Neue Gruppe …" });
        var anchor = new Button { Content = "Gruppe", Flyout = flyout };
        Window window = Show(anchor);

        flyout.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        try
        {
            var item = Assert.IsType<MenuItem>(flyout.Items[0]);
            MenuFlyoutPresenter presenter = item.FindAncestorOfType<MenuFlyoutPresenter>()
                                            ?? throw new InvalidOperationException("Menu is not open.");
            Assert.Equal(TokenColor("BgCardAlt", window), Brush(presenter.Background).Color);
            Assert.Equal(TokenColor("Divider", window), Brush(presenter.BorderBrush).Color);
            Assert.Equal(new CornerRadius(8), presenter.CornerRadius);
            Assert.Equal(new Thickness(4), presenter.Padding);

            Assert.Equal(new Thickness(12, 7, 12, 7), item.Padding);
            Border row = item.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
            Assert.Equal(new CornerRadius(6), row.CornerRadius);

            var separator = Assert.IsType<Separator>(flyout.Items[1]);
            Assert.Equal(1, separator.Height);
            Assert.Equal(TokenColor("Divider", window), Brush(separator.Background).Color);
        }
        finally
        {
            flyout.Hide();
        }
    }

    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Point Center(Control control, Visual relativeTo) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), relativeTo)
        ?? throw new InvalidOperationException("Control is not rendered.");

    private static ISolidColorBrush Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush);

    private static object Resource(string key, Window window)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out object? value), $"Ressource '{key}' fehlt.");
        return value!;
    }

    private static Color TokenColor(string key, Window window) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(Resource(key, window)).Color;
}
