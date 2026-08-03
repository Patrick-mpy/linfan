// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// MVC-Controller für die GUI-lokalen Oberflächen-Einstellungen (Theme, „in den Tray minimieren").
/// Persistiert über <see cref="UiSettingsStore"/> als <b>Load-modify-write</b>, damit die getrennt
/// gespeicherte Fenster-Geometrie nicht überschrieben wird. Das eigentliche Anwenden des Themes
/// (<c>Application.RequestedThemeVariant</c>) liegt bewusst nicht hier, sondern in der App-/View-Schicht
/// — dieser Controller meldet die Änderung nur per <see cref="ObservableObject.PropertyChanged"/>.
/// </summary>
public partial class SettingsController : ObservableObject, IDisposable
{
    private readonly UiSettingsStore _store;
    private readonly bool _loaded; // unterdrückt das Zurückschreiben während des Ladens im ctor

    public SettingsController(UiSettingsStore? store = null)
    {
        _store = store ?? new UiSettingsStore();
        UiSettings s = _store.Load();
        Theme = s.Theme;
        Language = s.Language;
        MinimizeToTray = s.MinimizeToTray;
        UpdateChecksEnabled = s.UpdateChecksEnabled;
        _loaded = true;

        _sections = BuildSections();
        _selectedSectionItem = _sections[0];

        // Bei Sprachwechsel die Seitenmenü-Einträge neu aufbauen (Label/Gruppe lesen live aus dem Localizer;
        // die ListBox bindet frische Instanzen). Die Auswahl über die Sektion rekonstruieren (nicht die Instanz).
        // Benannter Handler + Unsubscribe in Dispose: der Localizer ist ein app-lebenslanges Singleton, ein
        // anonymes Dauer-Abo würde jeden je erzeugten Controller am Leben halten.
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        SettingsSection current = SelectedSection;
        Sections = BuildSections();
        SelectedSectionItem = Sections.FirstOrDefault(x => x.Section == current) ?? Sections[0];
    }

    /// <summary>Löst das Localizer-Abo (siehe ctor). Vom <see cref="MainController"/> beim Shutdown aufgerufen.</summary>
    public void Dispose() => Localizer.Instance.PropertyChanged -= OnLanguageChanged;

    /// <summary>Auswahlliste für den Header-Umschalter.</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions => ThemeOption.All;

    /// <summary>Auswahlliste für den Sprach-Umschalter.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions => LanguageOption.All;

    /// <summary>Aktuell gewählter Theme-Modus (persistierte Quelle der Wahrheit).</summary>
    [ObservableProperty] private ThemeChoice _theme;

    /// <summary>Aktuell gewählte UI-Sprache (persistierte Quelle der Wahrheit).</summary>
    [ObservableProperty] private LanguageChoice _language;

    /// <summary>Ob das Schließen das Fenster ins Tray legt statt zu beenden.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    /// <summary>Ob beim Start auf neue Releases geprüft wird (Opt-out-Schalter im Anwendungs-Bereich).</summary>
    [ObservableProperty] private bool _updateChecksEnabled;

    // --- Seitenmenü-Navigation (reiner View-Zustand, nicht persistiert) --------------------------

    /// <summary>Einträge des linken Seitenmenüs (gruppiert Geräte/Anwendung/Verwaltung).</summary>
    [ObservableProperty] private IReadOnlyList<SettingsSectionItem> _sections;

    /// <summary>ListBox-Bindungsziel: der aktuell gewählte Menüeintrag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSection))]
    private SettingsSectionItem? _selectedSectionItem;

    /// <summary>Die aktive Sektion — steuert per <c>EnumMatchConverter</c> das sichtbare rechte Panel.</summary>
    public SettingsSection SelectedSection => SelectedSectionItem?.Section ?? SettingsSection.Sensors;

    private static IReadOnlyList<SettingsSectionItem> BuildSections() => new[]
    {
        new SettingsSectionItem(SettingsSection.Sensors,    "Settings.GroupDevices", "Settings.SectionSensors",    "IconThermometer", isFirstInGroup: true),
        new SettingsSectionItem(SettingsSection.Fans,       "Settings.GroupDevices", "Settings.SectionFans",       "IconFan",         isFirstInGroup: false),
        new SettingsSectionItem(SettingsSection.Airflow,    "Settings.GroupDevices", "Settings.SectionAirflow",    "IconWind",        isFirstInGroup: false),
        new SettingsSectionItem(SettingsSection.Appearance, "Settings.GroupApp",     "Settings.SectionAppearance", "IconPalette",     isFirstInGroup: true),
        new SettingsSectionItem(SettingsSection.Language,   "Settings.GroupApp",     "Settings.SectionLanguage",   "IconGlobe",       isFirstInGroup: false),
        new SettingsSectionItem(SettingsSection.Background, "Settings.GroupApp",     "Settings.SectionBackground", "IconWindow",      isFirstInGroup: false),
        new SettingsSectionItem(SettingsSection.Backup,     "Settings.GroupManage",  "Settings.SectionBackup",     "IconArchive",     isFirstInGroup: true),
        new SettingsSectionItem(SettingsSection.Onboarding, "Settings.GroupManage",  "Settings.SectionOnboarding", "IconSparkle",     isFirstInGroup: false),
    };

    /// <summary>ComboBox-Bindungsziel: kapselt <see cref="Theme"/> als Anzeige-Option.</summary>
    public ThemeOption SelectedThemeOption
    {
        get => ThemeOption.For(Theme);
        set
        {
            if (value is not null)
                Theme = value.Value;
        }
    }

    /// <summary>ComboBox-Bindungsziel: kapselt <see cref="Language"/> als Anzeige-Option.</summary>
    public LanguageOption SelectedLanguageOption
    {
        get => LanguageOption.For(Language);
        set
        {
            if (value is not null)
                Language = value.Value;
        }
    }

    partial void OnThemeChanged(ThemeChoice value)
    {
        OnPropertyChanged(nameof(SelectedThemeOption));
        Persist();
    }

    partial void OnLanguageChanged(LanguageChoice value)
    {
        OnPropertyChanged(nameof(SelectedLanguageOption));
        Persist();
    }

    partial void OnMinimizeToTrayChanged(bool value) => Persist();

    partial void OnUpdateChecksEnabledChanged(bool value) => Persist();

    private void Persist()
    {
        if (!_loaded)
            return;
        // Load-modify-write: erhält die separat gespeicherte Fenster-Geometrie UND die vom UpdateController
        // verwaltete DismissedUpdateVersion (die hier bewusst nicht angefasst wird).
        _store.Save(_store.Load() with
        {
            Theme = Theme,
            Language = Language,
            MinimizeToTray = MinimizeToTray,
            UpdateChecksEnabled = UpdateChecksEnabled,
        });
    }
}
