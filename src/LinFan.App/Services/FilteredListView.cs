// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;

namespace LinFan.App.Services;

/// <summary>
/// Seiteneffektfreie Helfer für die gefilterten Geräte-Tab-Listen: Teilstring-Suche über mehrere
/// Felder und ein In-place-Abgleich einer <see cref="ObservableCollection{T}"/> auf eine Quellsequenz
/// (nur bei Abweichung, damit gebundene Listen nicht unnötig aufgefrischt werden).
/// </summary>
internal static class FilteredListView
{
    /// <summary>Case-insensitive Teilstring-Suche über mehrere Felder (leerer/whitespace Suchtext matcht alles).</summary>
    public static bool Matches(string text, params string?[] fields)
    {
        string q = text.Trim();
        return fields.Any(f => f is not null && f.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bringt eine Ziel-Collection auf den Inhalt von <paramref name="source"/> - nur bei Abweichung
    /// (kein unnötiges Auffrischen gebundener Listen). In-place Clear+Add genügt für die kleinen Geräte-Listen.</summary>
    public static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        List<T> list = source.ToList();
        if (target.SequenceEqual(list))
            return;
        target.Clear();
        foreach (T item in list)
            target.Add(item);
    }
}
