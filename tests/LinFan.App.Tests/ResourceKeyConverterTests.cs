// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using LinFan.App.Converters;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert den <see cref="ResourceKeyConverter"/> ab, der einen Ressourcen-Schlüssel (String) in die App-
/// Ressource auflöst (Icon-Geometrie). Damit bleibt die Controller-Schicht frei von Avalonia-Rendering-Typen.
/// Läuft ohne Avalonia-App (<c>Application.Current</c> ist hier null) → defensiver Null-Pfad ist prüfbar.
/// </summary>
public sealed class ResourceKeyConverterTests
{
    private static object? Convert(object? value) =>
        ResourceKeyConverter.Instance.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_Null_ReturnsNull() => Assert.Null(Convert(null));

    [Fact]
    public void Convert_EmptyString_ReturnsNull() => Assert.Null(Convert(""));

    [Fact]
    public void Convert_NonString_ReturnsNull() => Assert.Null(Convert(42));

    [Fact]
    public void Convert_KeyWithoutRunningApp_ReturnsNull() =>
        // Ohne laufende App (Unit-Test) darf der Lookup nicht werfen, sondern liefert null (Icon bleibt leer).
        Assert.Null(Convert("IconFan"));

    [Fact]
    public void ConvertBack_Throws() =>
        Assert.Throws<NotSupportedException>(() =>
            ResourceKeyConverter.Instance.ConvertBack(null, typeof(string), null, CultureInfo.InvariantCulture));
}
