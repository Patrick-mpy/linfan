// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// MVC-Controller für die GUI-lokalen Oberflächen-Einstellungen (Theme, Akzentfarbe, „in den Tray minimieren").
/// Persistiert über <see cref="UiSettingsStore"/> als <b>Load-modify-write</b>, damit die getrennt
/// gespeicherte Fenster-Geometrie nicht überschrieben wird. Das eigentliche Anwenden von Theme und Akzent
/// (<c>Application.RequestedThemeVariant</c>, <see cref="AccentResources"/>) liegt bewusst nicht hier, sondern in der
/// App-/View-Schicht - dieser Controller meldet die Änderung nur per <see cref="ObservableObject.PropertyChanged"/>.
/// </summary>
public partial class SettingsController : ObservableObject, IDisposable
{
    private readonly UiSettingsStore _store;
    private readonly bool _loaded; // unterdrückt das Zurückschreiben während des Ladens im ctor
    private ThemeChoice _theme;
    private AccentChoice _accent;
    private string? _accentCustom;

    public SettingsController(UiSettingsStore? store = null)
    {
        _store = store ?? new UiSettingsStore();
        UiSettings s = _store.Load();
        _accentHexText = "";
        _themeOptions = ThemeOption.Build();
        AccentSwatches = new[] { AccentChoice.Standard }
            .Concat(AccentPalette.Presets)
            .Append(AccentChoice.Custom)
            .Select(choice => new AccentSwatch(choice))
            .ToList();
        Theme = s.Theme;
        AccentCustom = s.AccentCustom;
        Accent = s.Accent;
        Language = s.Language;
        MinimizeToTray = s.MinimizeToTray;
        UpdateChecksEnabled = s.UpdateChecksEnabled;
        _loaded = true;

        _sections = BuildSections();
        _selectedSectionItem = _sections[0];
        _languageMenu = BuildLanguageMenu();
        RefreshAccentDisplay();

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

        // The theme labels are translated and live inside the option instances → rebuild them. List first
        // (ItemsSource), then announce the selection: the getter rebuilds as well and, being a record, is
        // value-equal to the matching list entry.
        ThemeOptions = ThemeOption.Build();
        OnPropertyChanged(nameof(SelectedThemeOption));
        RefreshAccentDisplay();

        // The „System" detail and the chip's accessible name are translated.
        LanguageMenu = BuildLanguageMenu();
        OnPropertyChanged(nameof(LanguageChipName));
    }

    /// <summary>Löst das Localizer-Abo (siehe ctor). Vom <see cref="MainController"/> beim Shutdown aufgerufen.</summary>
    public void Dispose() => Localizer.Instance.PropertyChanged -= OnLanguageChanged;

    /// <summary>The theme tiles (translated ⇒ rebuilt on a language change).</summary>
    [ObservableProperty] private IReadOnlyList<ThemeOption> _themeOptions;

    /// <summary>The language chip's menu: „System" (with the language it resolves to), then the languages.</summary>
    [ObservableProperty] private IReadOnlyList<MenuChoice> _languageMenu;

    /// <summary>
    /// Aktuell gewählter Theme-Modus (persistierte Quelle der Wahrheit). A value this build does not know (a newer
    /// GUI's settings or backup) falls back to <see cref="ThemeChoice.System"/>.
    /// </summary>
    public ThemeChoice Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, Enum.IsDefined(value) ? value : ThemeChoice.System))
                OnThemeChanged(_theme);
        }
    }

    /// <summary>
    /// The concrete theme on screen - <see cref="Theme"/>, or for "System" what the OS resolved it to. The app reports
    /// it (<c>ActualThemeVariant</c>); the accent choice shows its colours in this theme.
    /// </summary>
    [ObservableProperty] private ThemeChoice _activeTheme = ThemeChoice.Dark;

    /// <summary>The accent colour (persisted). An unknown value falls back to <see cref="AccentChoice.Standard"/>.</summary>
    public AccentChoice Accent
    {
        get => _accent;
        set
        {
            if (!Enum.IsDefined(value))
                value = AccentChoice.Standard;
            // Picking "own colour" before there is one starts from the colour shown right now - no jump. Seeded in
            // the field, so it is persisted and applied once, together with the choice.
            bool seeded = value == AccentChoice.Custom && _accentCustom is null;
            if (seeded)
            {
                _accentCustom = ColorContrast.ToHex(AccentPalette.AccentOf(ActiveTheme, _accent, OwnColor));
                OnPropertyChanged(nameof(AccentCustom));
            }
            if (!SetProperty(ref _accent, value) && !seeded)
                return;
            // A draft left in the hex field does not survive picking another dot.
            AccentHexText = _accentCustom ?? "";
            AccentHexInvalid = false;
            OnAccentChanged();
        }
    }

    /// <summary>
    /// The user's own accent as "#RRGGBB" (persisted; kept while another accent is chosen). Anything that is not six hex
    /// digits is dropped (<c>null</c>).
    /// </summary>
    public string? AccentCustom
    {
        get => _accentCustom;
        set
        {
            string? hex = ColorContrast.TryParseHex(value, out Color c) ? ColorContrast.ToHex(c) : null;
            if (!SetProperty(ref _accentCustom, hex))
                return;
            // The field keeps the user's spelling of the same colour ("38bdf8"); anything else shows the new value.
            if (!(ColorContrast.TryParseHex(AccentHexText, out Color shown) && ColorContrast.ToHex(shown) == hex))
                AccentHexText = hex ?? "";
            AccentHexInvalid = false;
            OnAccentChanged();
        }
    }

    /// <summary>The hex field. Six hex digits (with or without '#') take effect at once; anything else stays as typed, marked invalid.</summary>
    [ObservableProperty] private string _accentHexText;

    /// <summary>The hex field holds something that is no colour - it keeps a red frame, the accent stays as it was.</summary>
    [ObservableProperty] private bool _accentHexInvalid;

    /// <summary>
    /// The colour dots: the theme's own accent, the presets, the own colour - each as it looks in
    /// <see cref="ActiveTheme"/>. One fixed list, updated in place (see <see cref="AccentSwatch"/>).
    /// </summary>
    public IReadOnlyList<AccentSwatch> AccentSwatches { get; }

    /// <summary>Aktuell gewählte UI-Sprache (persistierte Quelle der Wahrheit).</summary>
    [ObservableProperty] private LanguageChoice _language;

    /// <summary>Ob das Schließen das Fenster ins Tray legt statt zu beenden.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    /// <summary>Ob beim Start auf neue Releases geprüft wird (Opt-out-Schalter im Anwendungs-Bereich).</summary>
    [ObservableProperty] private bool _updateChecksEnabled;

    // --- Seitenmenü-Navigation (reiner View-Zustand, nicht persistiert) --------------------------

    /// <summary>Einträge des linken Seitenmenüs (gruppiert Geräte/Anwendung).</summary>
    [ObservableProperty] private IReadOnlyList<SettingsSectionItem> _sections;

    /// <summary>ListBox-Bindungsziel: der aktuell gewählte Menüeintrag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSection))]
    private SettingsSectionItem? _selectedSectionItem;

    /// <summary>Die aktive Sektion - steuert per <c>EnumMatchConverter</c> das sichtbare rechte Panel.</summary>
    public SettingsSection SelectedSection => SelectedSectionItem?.Section ?? SettingsSection.Sensors;

    /// <summary>Shows another settings section (links between sections, e.g. „Airflow ansehen").</summary>
    [RelayCommand]
    public void OpenSection(SettingsSection section)
    {
        if (Sections.FirstOrDefault(s => s.Section == section) is { } item)
            SelectedSectionItem = item;
    }

    private static IReadOnlyList<SettingsSectionItem> BuildSections() => new[]
    {
        new SettingsSectionItem(SettingsSection.Sensors,    "Settings.GroupDevices", "Settings.SectionSensors",    "IconThermometer", isFirstInGroup: true),
        new SettingsSectionItem(SettingsSection.Fans,       "Settings.GroupDevices", "Settings.SectionFans",       "IconFan",         isFirstInGroup: false),
        new SettingsSectionItem(SettingsSection.Airflow,    "Settings.GroupDevices", "Settings.SectionAirflow",    "IconWind",        isFirstInGroup: false),
        new SettingsSectionItem(SettingsSection.Appearance, "Settings.GroupApp",     "Settings.SectionAppearance", "IconPalette",     isFirstInGroup: true),
        new SettingsSectionItem(SettingsSection.Background, "Settings.GroupApp",     "Settings.SectionBackground", "IconWindow",      isFirstInGroup: false),
        new SettingsSectionItem(SettingsSection.Backup,     "Settings.GroupApp",     "Settings.SectionBackup",     "IconArchive",     isFirstInGroup: false),
    };

    /// <summary>Selection of the theme tiles: <see cref="Theme"/> as a display option.</summary>
    public ThemeOption SelectedThemeOption
    {
        get => ThemeOption.For(Theme);
        set
        {
            if (value is not null)
                Theme = value.Value;
        }
    }

    /// <summary>What the language chip shows: <see cref="Language"/> as a display option.</summary>
    public LanguageOption SelectedLanguageOption => LanguageOption.For(Language);

    /// <summary>Accessible name of the language chip, which shows the value only as text inside („Sprache: Deutsch").</summary>
    public string LanguageChipName => Localizer.Instance.Format("Settings.LanguageChipName", SelectedLanguageOption.Display);

    private IReadOnlyList<MenuChoice> BuildLanguageMenu() => LanguageOption.All
        .Select(o => MenuChoice.Value(o.Name, new RelayCommand(() => Language = o.Value), o.Value == Language,
            automationName: o.Display,
            detail: o.SystemLanguage is { } resolved ? Localizer.Instance.Format("Settings.LanguageSystemDetail", resolved) : ""))
        .ToList();

    private void OnThemeChanged(ThemeChoice value)
    {
        OnPropertyChanged(nameof(SelectedThemeOption));
        // A fixed theme is on screen right away; for "System" the app reports what the OS resolves it to.
        if (value != ThemeChoice.System)
            ActiveTheme = value;
        Persist();
    }

    partial void OnActiveThemeChanged(ThemeChoice value) => RefreshAccentDisplay();

    // --- Accent colour -------------------------------------------------------------------------

    /// <summary>Selection of the colour dots: <see cref="Accent"/> as its dot.</summary>
    public AccentSwatch? SelectedAccentSwatch
    {
        get => AccentSwatches.FirstOrDefault(s => s.Choice == Accent);
        set
        {
            if (value is not null)
                Accent = value.Choice;
        }
    }

    /// <summary>The own-colour row (hex field, derived colours) shows while the own colour is chosen.</summary>
    public bool IsCustomAccent => Accent == AccentChoice.Custom;

    /// <summary>Lead-in of the derived colours: "In the theme „Dark“: lines".</summary>
    public string AccentDerivedText => Localizer.Instance.Format("Accent.Derived", ThemeOption.For(ActiveTheme).Display);

    /// <summary>The own colour as a line in <see cref="ActiveTheme"/>.</summary>
    public Color AccentLineColor => CustomTokens.Accent;

    public string AccentLineHex => ColorContrast.ToHex(AccentLineColor);

    /// <summary>The own colour as a button fill in <see cref="ActiveTheme"/>.</summary>
    public Color AccentFillColor => CustomTokens.Fill;

    public string AccentFillHex => ColorContrast.ToHex(AccentFillColor);

    /// <summary>The own colour had to be lightened or darkened to read in <see cref="ActiveTheme"/>.</summary>
    public bool AccentAdjusted => AccentPalette.IsAdjusted(ActiveTheme, OwnColor);

    /// <summary>A note when the chosen accent comes close to a status colour; empty for none.</summary>
    public string AccentWarning => AccentPalette.ClashOf(Accent, OwnColor) switch
    {
        AccentClash.Warning => Localizer.Instance["Accent.ClashWarning"],
        AccentClash.Danger => Localizer.Instance["Accent.ClashDanger"],
        AccentClash.Success => Localizer.Instance["Accent.ClashSuccess"],
        _ => "",
    };

    public bool HasAccentWarning => AccentWarning.Length > 0;

    private Color OwnColor =>
        ColorContrast.TryParseHex(_accentCustom, out Color c) ? c : AccentPalette.StandardAccent(ActiveTheme);

    private AccentTokens CustomTokens => AccentPalette.Derive(ActiveTheme, AccentChoice.Custom, OwnColor)!;

    partial void OnAccentHexTextChanged(string value)
    {
        // The field is only shown while the own colour is chosen; whoever fills it otherwise clears the mark after.
        if (!ColorContrast.TryParseHex(value, out _))
        {
            AccentHexInvalid = true;
            return;
        }
        AccentHexInvalid = false;
        AccentCustom = value;
    }

    private void OnAccentChanged()
    {
        OnPropertyChanged(nameof(IsCustomAccent));
        RefreshAccentDisplay();
        Persist();
    }

    /// <summary>Updates the dots and announces everything derived from accent, theme on screen and language.</summary>
    private void RefreshAccentDisplay()
    {
        UpdateAccentSwatches();
        OnPropertyChanged(nameof(SelectedAccentSwatch));
        OnPropertyChanged(nameof(AccentDerivedText));
        OnPropertyChanged(nameof(AccentLineHex));
        OnPropertyChanged(nameof(AccentLineColor));
        OnPropertyChanged(nameof(AccentFillHex));
        OnPropertyChanged(nameof(AccentFillColor));
        OnPropertyChanged(nameof(AccentAdjusted));
        OnPropertyChanged(nameof(AccentWarning));
        OnPropertyChanged(nameof(HasAccentWarning));
    }

    private void UpdateAccentSwatches()
    {
        string theme = ThemeOption.For(ActiveTheme).Display;
        foreach (AccentSwatch swatch in AccentSwatches)
        {
            swatch.Name = Localizer.Instance[$"Accent.{swatch.Choice}"];
            swatch.Color = AccentPalette.AccentOf(ActiveTheme, swatch.Choice, OwnColor);
            swatch.Tooltip = swatch.Choice switch
            {
                AccentChoice.Standard => Localizer.Instance.Format("Accent.StandardTooltip", theme),
                AccentChoice.Custom => Localizer.Instance["Accent.CustomTooltip"],
                _ => Localizer.Instance.Format("Accent.PresetTooltip", swatch.Name, theme, ColorContrast.ToHex(swatch.Color)),
            };
        }
    }

    partial void OnLanguageChanged(LanguageChoice value)
    {
        OnPropertyChanged(nameof(SelectedLanguageOption));
        OnPropertyChanged(nameof(LanguageChipName));
        if (_loaded)
            LanguageMenu = BuildLanguageMenu();
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
            Accent = Accent,
            AccentCustom = AccentCustom,
            Language = Language,
            MinimizeToTray = MinimizeToTray,
            UpdateChecksEnabled = UpdateChecksEnabled,
        });
    }
}
