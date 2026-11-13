// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using LinFan.App.Controllers;

namespace LinFan.App.Controls;

/// <summary>
/// Colours a temperature by its level in the theme on screen: <c>ctrl:TemperatureColor.Value="{Binding Max}"</c> ties the
/// element's foreground (a sparkline's stroke) to the resource TempCool, TempWarm, TempHot or TempNone, so the colour
/// follows a theme switch as well as the value. The marks are <see cref="TemperatureLevels"/>, shared with the hot icon
/// and the hints.
/// </summary>
public static class TemperatureColor
{
    public static readonly AttachedProperty<double> ValueProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Value", typeof(TemperatureColor), double.NaN);

    // The resource the element is tied to right now - a new tie only when the level changes, not on every reading.
    private static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Key", typeof(TemperatureColor));

    static TemperatureColor() => ValueProperty.Changed.AddClassHandler<Control>((control, _) => Tie(control));

    public static double GetValue(Control control) => control.GetValue(ValueProperty);

    public static void SetValue(Control control, double value) => control.SetValue(ValueProperty, value);

    /// <summary>The resource of a temperature's colour: no reading, cool, warm (from 60 °C) or hot (from 80 °C).</summary>
    public static string KeyOf(double temperatureC) =>
        double.IsNaN(temperatureC) ? "TempNone"
        : temperatureC >= TemperatureLevels.HotC ? "TempHot"
        : temperatureC >= TemperatureLevels.WarmC ? "TempWarm"
        : "TempCool";

    private static void Tie(Control control)
    {
        string key = KeyOf(GetValue(control));
        if (control.GetValue(KeyProperty) == key)
            return;
        control.SetValue(KeyProperty, key);
        if (control is Sparkline)
            control.Bind(Sparkline.StrokeProperty, control.GetResourceObservable(key));
        else
            control.Bind(TextElement.ForegroundProperty, control.GetResourceObservable(key));
    }
}
