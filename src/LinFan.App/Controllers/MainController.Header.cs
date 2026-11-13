// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

// The header: the two areas (overview with its hint counter, profiles & curves), the running profile's button with its
// menu, and the gear for the settings. The tab control below only hosts the selected area - this is its navigation.
public partial class MainController
{
    private const int SettingsTab = 2;

    private ProfileRow? _headerProfile; // the running profile whose name the button shows (renames follow)

    /// <summary>The area selected in the header: 0 overview, 1 profiles &amp; curves, -1 while the settings are open.</summary>
    public int NavIndex
    {
        get => SelectedTabIndex < SettingsTab ? SelectedTabIndex : -1;
        set
        {
            if (value is >= 0 and < SettingsTab)
                SelectedTabIndex = value;
            else
                OnPropertyChanged(); // the list cleared its selection (Ctrl+click): an area stays selected
        }
    }

    /// <summary>The settings are open: the gear shows it (accent), the area tabs have no selection.</summary>
    public bool IsSettingsOpen => SelectedTabIndex == SettingsTab;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(NavIndex));
        OnPropertyChanged(nameof(IsSettingsOpen));
        if (value != SettingsTab)
            Editor.RevertAllManual(); // a fan test lives in the settings; out of sight it must not keep the fan
    }

    /// <summary>The gear. Opening it again while the settings are open changes nothing.</summary>
    [RelayCommand]
    private void OpenSettings() => SelectedTabIndex = SettingsTab;

    /// <summary>The overview tab's counter: <see cref="OverviewStatus.AttentionCount"/>, the number in the status tile's headline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNavHintBadge))]
    private int _navHintCount;

    /// <summary>The counter turns red while a hint is critical (the offline hint is one), orange otherwise.</summary>
    [ObservableProperty] private bool _navHintCritical;

    /// <summary>The counter's accessible name and tooltip: „1 Hinweis" / „3 Hinweise".</summary>
    [ObservableProperty] private string _navHintName = "";

    public bool ShowNavHintBadge => NavHintCount > 0;

    /// <summary>The overview tab's accessible name with its state: „Übersicht, 3 Hinweise".</summary>
    [ObservableProperty] private string _navOverviewName = "";

    /// <summary>„Profile &amp; Kurven, ungespeicherte Änderungen" while the curves hold unsaved changes.</summary>
    [ObservableProperty] private string _navCurvesName = "";

    /// <summary>The gear's accessible name: „Einstellungen, ungespeicherte Änderungen" while the settings hold some.</summary>
    [ObservableProperty] private string _settingsButtonName = "";

    /// <summary>Follows the overview's hints (<see cref="ApplyHints"/>) - the same list the status tile counts.</summary>
    private void ApplyNavBadge()
    {
        NavHintCount = OverviewStatus.AttentionCount;
        NavHintCritical = OverviewStatus.Level == OverviewLevel.Critical;
        NavHintName = NavHintCount == 1
            ? Localizer.Instance["Nav.HintOne"]
            : Localizer.Instance.Format("Nav.Hints", NavHintCount);
        RefreshNavNames();
    }

    /// <summary>The names screen readers hear for the areas: the dot and the counter are only visual otherwise.</summary>
    private void RefreshNavNames()
    {
        Localizer l = Localizer.Instance;
        string WithState(string key, bool show, string state) =>
            show ? l.Format("Nav.ItemState", l[key], state) : l[key];
        NavOverviewName = WithState("MainWindow.TabOverview", NavHintCount > 0, NavHintName);
        NavCurvesName = WithState("MainWindow.TabCurves", Editor.CurvesAreaDirty, l["DirtyDot.Name"]);
        SettingsButtonName = WithState("MainWindow.TabSettings", Editor.SettingsAreaDirty, l["DirtyDot.Name"]);
    }

    // --- Profile button ---------------------------------------------------------------------------------

    /// <summary>The button's dot: running, dry run or offline - neutral until the first connection attempt has an outcome.</summary>
    [ObservableProperty] private ProfileDot _profileDot = ProfileDot.Connecting;

    /// <summary>
    /// The small line above the profile name: „Profil läuft", „Profil · Dry-Run", „Profil · Dienst getrennt" (or
    /// „Profil · Daten unlesbar" while the service sends what this GUI cannot read).
    /// </summary>
    [ObservableProperty] private string _profileCaption = "";

    /// <summary>The running profile's name; „Verbinde …" before the first connection, "-" while it is unknown.</summary>
    [ObservableProperty] private string _profileName = "";

    /// <summary>The status line that used to sit under the logo, and what the button does.</summary>
    [ObservableProperty] private string _profileTooltip = "";

    /// <summary>What a screen reader says for the button: the caption and the name.</summary>
    [ObservableProperty] private string _profileButtonName = "";

    /// <summary>The menu behind the profile button, rebuilt as it opens (<see cref="RefreshProfileMenu"/>).</summary>
    public ObservableCollection<ProfileMenuEntry> ProfileMenu { get; } = new();

    /// <summary>Called from every snapshot and whenever the running profile (or its name) changes.</summary>
    private void RefreshProfileButton()
    {
        RefreshNavNames(); // with every snapshot, so the names follow a language switch
        Localizer l = Localizer.Instance;
        ProfileDot = Connection.Problem switch
        {
            ConnectionProblem.Connecting => ProfileDot.Connecting,
            ConnectionProblem.None => Connection.DryRun ? ProfileDot.DryRun : ProfileDot.Running,
            _ => ProfileDot.Offline,
        };
        ProfileCaption = ProfileDot switch
        {
            ProfileDot.Running => l["ProfileButton.Running"],
            ProfileDot.DryRun => l["ProfileButton.DryRun"],
            ProfileDot.Offline when Connection.Problem == ConnectionProblem.UnreadableData
                => l["ProfileButton.Unreadable"],
            ProfileDot.Offline => l["ProfileButton.Offline"],
            _ => l["ProfileButton.Caption"],
        };
        ProfileName = Editor.ActiveProfile?.Name is { Length: > 0 } name ? name
            : ProfileDot == ProfileDot.Connecting ? l["MainCtrl.Connecting"]
            : Tiles.ProfileName;
        ProfileTooltip = l.Format("ProfileButton.Tooltip", Status);
        ProfileButtonName = l.Format("ProfileButton.Name", ProfileCaption, ProfileName);
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CurveEditorController.CurvesAreaDirty) or nameof(CurveEditorController.SettingsAreaDirty))
            RefreshNavNames();
        if (e.PropertyName != nameof(CurveEditorController.ActiveProfile))
            return;
        if (_headerProfile is not null)
            _headerProfile.PropertyChanged -= OnHeaderProfileChanged;
        _headerProfile = Editor.ActiveProfile;
        if (_headerProfile is not null)
            _headerProfile.PropertyChanged += OnHeaderProfileChanged;
        RefreshProfileButton();
    }

    private void OnHeaderProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileRow.Name))
            RefreshProfileButton();
    }

    /// <summary>
    /// Builds the profile menu - the view calls it as the menu opens, so counts, marks and what is possible are current:
    /// „Profil wechseln", one entry per profile (switching goes through <see cref="CurveEditorController.RequestProfileSwitch"/>,
    /// which asks while changes are unsaved), then „Profile bearbeiten …" and „Neues Profil".
    /// </summary>
    public void RefreshProfileMenu()
    {
        Localizer l = Localizer.Instance;
        ProfileMenu.Clear();
        ProfileMenu.Add(ProfileMenuEntry.Heading(l["ProfileMenu.Heading"]));
        bool canSwitch = Editor.CanSwitchProfile;
        string blocked = Editor.ProfileSwitchBlockedReason;
        foreach (ProfileRow profile in Editor.Profiles)
        {
            string id = profile.Id;
            string detail = Editor.ProfileSummary(profile);
            var spoken = new List<string> { profile.Name, detail };
            if (profile.IsActive)
                spoken.Add(l["ProfileMenu.Running"]);
            if (profile.IsDirty)
                spoken.Add(l["DirtyDot.Name"]);
            ProfileMenu.Add(ProfileMenuEntry.Profile(profile.Name, detail, profile.IsActive, profile.IsDirty, ProfileDot,
                new RelayCommand(() => Editor.RequestProfileSwitch(id)), canSwitch, blocked, string.Join(", ", spoken)));
        }
        ProfileMenu.Add(ProfileMenuEntry.Separator());
        ProfileMenu.Add(ProfileMenuEntry.Action(l["ProfileMenu.Edit"], "IconRename", OpenRunningProfileCommand, Editor.IsReady));
        ProfileMenu.Add(ProfileMenuEntry.Action(l["ProfileMenu.New"], "IconPlus", NewProfileCommand, Editor.IsReady));
    }

    /// <summary>„Neues Profil": a draft profile, opened in the profile pane - not activated.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        if (!Editor.IsReady)
            return;
        Editor.AddProfileCommand.Execute(null);
        SelectedTabIndex = 1;
    }
}
