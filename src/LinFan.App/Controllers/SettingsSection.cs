// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Die Einträge des Einstellungen-Seitenmenüs (linke Spalte). Der ausgewählte Wert steuert per
/// <c>EnumMatchConverter</c>, welches rechte Panel sichtbar ist. Gruppiert dargestellt
/// (Geräte / Anwendung / Verwaltung) — siehe <see cref="SettingsSectionItem"/>.
/// </summary>
public enum SettingsSection
{
    Sensors,
    Fans,
    Airflow,
    Appearance,
    Language,
    Background,
    Backup,
    Onboarding,
}
