// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Data.Converters;

namespace LinFan.App.Converters;

/// <summary>
/// Text converter of the Stepper theme (<c>NumericUpDown.TextConverter</c>, Theme/Layout.axaml). Numbers are shown
/// invariant ("2.5", project rule), but input takes "," as well as "." as the decimal separator: NumericUpDown
/// commits on every keystroke, so a rejected "2," would silently leave the value at 2 while the user goes on
/// typing "2,5". Note the direction NumericUpDown uses: <see cref="Convert"/> is text → value,
/// <see cref="ConvertBack"/> is value → text.
/// </summary>
public sealed class StepperTextConverter : IValueConverter
{
    /// <summary>Singleton for <c>{x:Static}</c> in XAML.</summary>
    public static readonly StepperTextConverter Instance = new();

    /// <summary>
    /// Text → value. Throws for anything but a plain number (empty text included): NumericUpDown then keeps the
    /// previous value and restores the text on blur; its Minimum/Maximum check still applies to what parses.
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = (value as string ?? string.Empty).Trim().Replace(',', '.');
        return decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Value → text: at most two decimals and no trailing ".0" (a decimal keeps its scale: 2.5 + 0.5 = 3.0).</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal number ? number.ToString("0.##", CultureInfo.InvariantCulture) : null;
}
