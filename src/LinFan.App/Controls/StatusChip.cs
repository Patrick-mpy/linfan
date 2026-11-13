// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using LinFan.App.Controllers;

namespace LinFan.App.Controls;

/// <summary>
/// A dot and a short word in a hairline pill („kalibriert", „läuft", „Sensor fehlt"). The look is the
/// <c>Border.statusChip</c> styles of Theme/Layout.axaml; <see cref="Tone"/> sets the matching tone class, so a view
/// binds one value instead of a class binding per tone.
/// </summary>
public sealed class StatusChip : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusChip, string?>(nameof(Text));

    public static readonly StyledProperty<StatusTone> ToneProperty =
        AvaloniaProperty.Register<StatusChip, StatusTone>(nameof(Tone));

    /// <summary>For an outcome long enough to need a second line in a narrow row (calibration, coupling).</summary>
    public static readonly StyledProperty<TextWrapping> TextWrappingProperty =
        TextBlock.TextWrappingProperty.AddOwner<StatusChip>();

    private readonly TextBlock _text = new();

    public StatusChip()
    {
        Classes.Add("statusChip");
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new Ellipse(), _text } };
        ApplyTone();
    }

    // Styled as a Border: the statusChip styles - and any rule written against them - apply unchanged.
    protected override Type StyleKeyOverride => typeof(Border);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _text.Text = Text;
        else if (change.Property == TextWrappingProperty)
            _text.TextWrapping = TextWrapping;
        else if (change.Property == ToneProperty)
            ApplyTone();
    }

    private void ApplyTone()
    {
        StatusTone tone = Tone;
        Classes.Set("ok", tone == StatusTone.Ok);
        Classes.Set("busy", tone == StatusTone.Busy);
        Classes.Set("warn", tone == StatusTone.Warn);
        Classes.Set("muted", tone == StatusTone.Muted);
    }
}
