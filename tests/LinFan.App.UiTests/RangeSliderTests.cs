// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LinFan.App.Controls;

namespace LinFan.App.UiTests;

/// <summary>
/// The two-thumb slider through the real input pipeline: a press takes the nearer thumb, dragging moves it in whole
/// steps, and a thumb stops at the other one instead of passing it.
/// </summary>
public class RangeSliderTests
{
    // 218 px wide: the usable track is 200 px between the 9-px insets, so 1 % = 2 px.
    private static (Window Window, RangeSlider Slider) Show(double lower, double upper)
    {
        var slider = new RangeSlider { Width = 218, LowerValue = lower, UpperValue = upper, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var window = new Window { Width = 300, Height = 100, Content = slider };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, slider);
    }

    private static Point At(RangeSlider slider, Window window, double x) =>
        slider.TranslatePoint(new Point(x, slider.Bounds.Height - 11), window) ?? throw new InvalidOperationException();

    [AvaloniaFact]
    public void Drag_MovesTheNearerThumb_InWholeSteps()
    {
        var (window, slider) = Show(20, 80);

        window.MouseDown(At(slider, window, 51), MouseButton.Left); // near the lower thumb (x 49)
        window.MouseMove(At(slider, window, 70.6));
        window.MouseUp(At(slider, window, 70.6), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(31, slider.LowerValue); // (70.6 - 9) / 2 = 30.8 → 31
        Assert.Equal(80, slider.UpperValue);
    }

    [AvaloniaFact]
    public void Drag_StopsAtTheOtherThumb()
    {
        var (window, slider) = Show(20, 40);

        window.MouseDown(At(slider, window, 89), MouseButton.Left); // the upper thumb (x 89)
        window.MouseMove(At(slider, window, 10));                    // dragged far past the lower one
        window.MouseUp(At(slider, window, 10), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(20, slider.LowerValue);
        Assert.Equal(20, slider.UpperValue);
    }

    [AvaloniaFact]
    public void Disabled_IgnoresThePointer()
    {
        var (window, slider) = Show(20, 80);
        slider.IsEnabled = false;
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(At(slider, window, 49), MouseButton.Left);
        window.MouseMove(At(slider, window, 150));
        window.MouseUp(At(slider, window, 150), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(20, slider.LowerValue);
    }

    [AvaloniaFact]
    public void TakesNoFocus_TheSteppersBesideItAreTheKeyboardPath()
    {
        var (_, slider) = Show(20, 80);
        Assert.False(slider.Focusable);
    }
}
