// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace LinFan.App.Converters;

/// <summary>
/// Löst einen Ressourcen-Schlüssel (String) zur Laufzeit in die zugehörige App-Ressource auf — z. B. eine
/// Icon-Geometrie aus <c>Icons.axaml</c>. Hält die Ressourcen-/Rendering-Abhängigkeit in der View-Schicht,
/// sodass Daten-Items (z. B. <c>SettingsSectionItem</c>) den Schlüssel nur als String führen und frei von
/// Avalonia-Rendering-Typen bleiben. One-Way; <c>null</c>/leer/unbekannt/ohne laufende App → <c>null</c>.
/// </summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    /// <summary>Singleton für die Nutzung per <c>{x:Static}</c> in XAML (keine Resource-Registrierung nötig).</summary>
    public static readonly ResourceKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key))
            return null;
        return Application.Current?.TryFindResource(key, out object? res) == true ? res : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("ResourceKeyConverter ist nur für One-Way-Bindings gedacht.");
}
