// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// Ein Eintrag im Einstellungen-Seitenmenü: Sektion + lokalisiertes Label/Gruppe + Icon-Ressourcenschlüssel.
/// <see cref="Group"/>/<see cref="Label"/> lesen live aus dem <see cref="Localizer"/> (der <c>SettingsController</c>
/// baut die Liste bei Sprachwechsel neu auf). <see cref="IconKey"/> ist ein reiner String - die View löst ihn
/// per <c>ResourceKeyConverter</c> zur Geometrie auf, sodass die Controller-Schicht frei von Avalonia-Rendering-
/// Typen bleibt (und der Typ ohne laufende App konstruierbar ist, z. B. in Unit-Tests).
/// </summary>
public sealed class SettingsSectionItem
{
    private readonly string _labelKey;
    private readonly string _groupKey;

    public SettingsSectionItem(SettingsSection section, string groupKey, string labelKey, string iconKey, bool isFirstInGroup)
    {
        Section = section;
        _groupKey = groupKey;
        _labelKey = labelKey;
        IconKey = iconKey;
        IsFirstInGroup = isFirstInGroup;
    }

    public SettingsSection Section { get; }

    /// <summary>True für den ersten Eintrag einer Gruppe → die View zeigt darüber die Gruppen-Überschrift.</summary>
    public bool IsFirstInGroup { get; }

    public string Group => Localizer.Instance[_groupKey];
    public string Label => Localizer.Instance[_labelKey];

    /// <summary>Ressourcen-Schlüssel des Icons (z. B. „IconFan"); die View löst ihn via <c>ResourceKeyConverter</c> auf.</summary>
    public string IconKey { get; }
}
