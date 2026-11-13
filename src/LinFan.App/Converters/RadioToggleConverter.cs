// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace LinFan.App.Converters;

/// <summary>
/// One-way: <c>true</c> → <see cref="MenuItemToggleType.Radio"/>, otherwise <see cref="MenuItemToggleType.None"/>. Only
/// the value entries of a choice menu are radio items: a click checks a radio item locally, and the reused menu would
/// keep that mark on an action entry (diagram, new group) whose click changes no value and so rebuilds nothing.
/// </summary>
public sealed class RadioToggleConverter : IValueConverter
{
    public static readonly RadioToggleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? MenuItemToggleType.Radio : MenuItemToggleType.None;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("RadioToggleConverter is one-way.");
}
