// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections;
using System.Globalization;
using System.Resources;
using LinFan.App.Localization;

namespace LinFan.App.Tests;

/// <summary>
/// Parität der Ressourcen-Tabellen: die neutrale (englische) und die deutsche .resx müssen exakt
/// dieselben Keys enthalten. Fängt vergessene Übersetzungen oder verwaiste Keys in einer der beiden
/// Dateien ab — billig und wertvoll, weil ein fehlender Key sonst still als Key-Literal in der UI
/// landet (Localizer-Fallback).
/// </summary>
public sealed class LocalizationParityTests
{
    private static readonly ResourceManager Resources =
        new("LinFan.App.Resources.Strings", typeof(TrExtension).Assembly);

    // tryParents:false ⇒ nur die Keys genau dieser Kultur, kein Merge mit dem Fallback. Nur so werden
    // Lücken (Key in der einen, nicht in der anderen Datei) überhaupt sichtbar.
    private static HashSet<string> KeysFor(CultureInfo culture)
    {
        ResourceSet set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"Keine Ressourcen für Kultur '{culture.Name}'.");
        return set.Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void NeutralAndGerman_HaveIdenticalKeys()
    {
        HashSet<string> english = KeysFor(CultureInfo.InvariantCulture); // neutral = Englisch
        HashSet<string> german = KeysFor(new CultureInfo("de"));

        Assert.NotEmpty(english); // Schutz davor, dass beide Sets leer sind (Resource gar nicht geladen)

        string[] missingInGerman = english.Except(german).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        string[] missingInEnglish = german.Except(english).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.True(
            missingInGerman.Length == 0 && missingInEnglish.Length == 0,
            $"Resx-Parität verletzt. Fehlt in Strings.de.resx: [{string.Join(", ", missingInGerman)}]; " +
            $"fehlt in Strings.resx (neutral/en): [{string.Join(", ", missingInEnglish)}].");
    }
}
