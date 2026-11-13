// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.UiTests;

/// <summary>
/// The header in the real window: the area tabs (keyboard, hint counter), the settings gear, the profile button with its
/// menu, and switching the running profile from there - including the question while changes are unsaved.
/// </summary>
public class HeaderUiTests
{
    private const string Fan = "hwmon0/pwm1";

    /// <summary>"Silent" runs, "Loud" drives the same fan with its own curve.</summary>
    private static MonitorSnapshot Snapshot(string running = "p-silent", bool dryRun = false)
    {
        var quiet = new CurveConfig
        {
            Id = "quiet",
            Name = "Quiet",
            SourceSensorIds = new[] { "hwmon0/temp1" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var loud = new CurveConfig
        {
            Id = "loud",
            Name = "Loud-Kurve",
            SourceSensorIds = new[] { "hwmon0/temp1" },
            Points = new[] { new CurvePoint(30, 60), new CurvePoint(80, 100) },
        };
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU" } },
            Curves = new[] { quiet },
            Fans = new[]
            {
                new FanConfig
                {
                    FanId = Fan, Name = "CPU Fan", AssignedCurveId = "quiet",
                    Calibration = new FanCalibration { StartPwm = 40, MinRpm = 300, MaxRpm = 2000 },
                },
            },
            Profiles = new[]
            {
                new Profile { Id = "p-silent", Name = "Silent", Curves = new[] { quiet }, Assignments = new[] { new ProfileAssignment(Fan, "quiet") } },
                new Profile { Id = "p-loud", Name = "Loud", Curves = new[] { loud }, Assignments = new[] { new ProfileAssignment(Fan, "loud") } },
            },
            ActiveProfileId = "p-silent",
        };
        return new MonitorSnapshot(
            "Verbunden",
            new[] { new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0) },
            new[] { new FanReading(Fan, "CPU Fan", 1200, 120, CanControl: true, ControlState: FanControlState.Curve, ControlCurveId: "quiet") },
            ProfileService.Apply(config, running),
            Connected: true,
            DryRun: dryRun);
    }

    private static (MainController Ctrl, MainWindow Window, FakeLiveMonitor Monitor) Show(MonitorSnapshot? snapshot = null,
        TimeSpan? poll = null, double width = 1000)
    {
        var monitor = new FakeLiveMonitor(snapshot ?? Snapshot());
        var ctrl = UiTestHelpers.NewController(monitor, pollInterval: poll);
        // Tall enough that the floating unsaved-changes bar covers nothing a test clicks.
        var window = new MainWindow { DataContext = ctrl, Width = width, Height = 800 };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.HasLiveData);
        return (ctrl, window, monitor);
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), c => c.Name == name);

    private static void Click(TopLevel top, Control target)
    {
        Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top)
                       ?? throw new InvalidOperationException("Not rendered.");
        top.MouseDown(center, MouseButton.Left);
        top.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(TopLevel top, Key key, PhysicalKey physical)
    {
        top.KeyPress(key, RawInputModifiers.None, physical, "");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Opens the profile button's menu and returns its entries.</summary>
    private static List<MenuItem> OpenProfileMenu(MainWindow window)
    {
        Button button = Named<Button>(window, "ProfileButton");
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<MenuItem>().ToList();
    }

    private static UnsavedChangesDialog WaitForDialog(MainWindow window)
    {
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count > 0);
        return Assert.IsType<UnsavedChangesDialog>(Assert.Single(window.OwnedWindows));
    }

    // --- Area tabs and gear ---------------------------------------------------------------------------------

    /// <summary>←/→ (wrapping), Home and End move between the areas while a tab has the focus - the list does that itself.</summary>
    [AvaloniaFact]
    public void AreaTabs_ArrowKeysHomeAndEnd_SwitchTheArea()
    {
        var (ctrl, window, _) = Show();
        ListBoxItem overview = Named<ListBoxItem>(window, "NavOverview");
        overview.Focus();
        Assert.Equal(0, ctrl.SelectedTabIndex);

        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(1, ctrl.SelectedTabIndex);
        Press(window, Key.Right, PhysicalKey.ArrowRight); // wraps around
        Assert.Equal(0, ctrl.SelectedTabIndex);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(1, ctrl.SelectedTabIndex);
        Press(window, Key.Home, PhysicalKey.Home);
        Assert.Equal(0, ctrl.SelectedTabIndex);
        Press(window, Key.End, PhysicalKey.End);
        Assert.Equal(1, ctrl.SelectedTabIndex);
        Assert.True(Named<ListBoxItem>(window, "NavCurves").IsFocused); // the focus follows
        Assert.Equal(1, Assert.IsType<TabControl>(window.Find<TabControl>().Single()).SelectedIndex);
    }

    /// <summary>No window-wide arrow handler: elsewhere ←/→ belong to the focused control, not to the areas.</summary>
    [AvaloniaFact]
    public void ArrowKeys_OutsideTheTabs_LeaveTheAreaAlone()
    {
        var (ctrl, window, _) = Show();
        Button gear = Named<Button>(window, "SettingsButton");
        gear.Focus();

        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);

        Assert.Equal(0, ctrl.SelectedTabIndex);
    }

    [AvaloniaFact]
    public void Gear_OpensTheSettings_InTheAccent_AndTheTabsLoseTheirSelection()
    {
        var (ctrl, window, _) = Show();
        Button gear = Named<Button>(window, "SettingsButton");
        ListBox tabs = Named<ListBox>(window, "NavTabs");
        Assert.DoesNotContain("current", gear.Classes);
        Assert.Equal("Einstellungen", AutomationProperties.GetName(gear));

        Click(window, gear);

        Assert.Equal(2, ctrl.SelectedTabIndex);
        Assert.True(ctrl.IsSettingsOpen);
        Assert.Contains("current", gear.Classes);
        Assert.Equal(-1, tabs.SelectedIndex);
        Assert.True(Named<StackPanel>(window, "SettingsHeading").IsEffectivelyVisible);

        Click(window, gear); // stays - the gear is no toggle
        Assert.Equal(2, ctrl.SelectedTabIndex);

        Click(window, Named<ListBoxItem>(window, "NavCurves"));
        Assert.Equal(1, ctrl.SelectedTabIndex);
        Assert.DoesNotContain("current", gear.Classes);
    }

    /// <summary>The overview tab counts the hints that need a look: orange for notices, red once one is critical (offline is).</summary>
    [AvaloniaFact]
    public void HintCounter_CountsNoticesAndCriticalHints_OrangeOrRed()
    {
        var (ctrl, window, monitor) = Show(Snapshot(dryRun: true), poll: TimeSpan.FromMilliseconds(20));
        Border badge = Named<Border>(window, "NavHintBadge");

        Assert.True(badge.IsEffectivelyVisible); // the dry-run notice
        Assert.Equal(1, ctrl.NavHintCount);
        Assert.Equal("1", Assert.IsType<TextBlock>(badge.Child).Text);
        Assert.DoesNotContain("critical", badge.Classes);
        Assert.Equal("1 Hinweis", AutomationProperties.GetName(badge));

        monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
        UiTestHelpers.PumpUntil(() => ctrl.NavHintCritical);

        Assert.Contains("critical", badge.Classes); // the offline hint is critical
        Assert.Equal(1, ctrl.NavHintCount);

        monitor.Current = Snapshot();
        UiTestHelpers.PumpUntil(() => ctrl.Connected && ctrl.NavHintCount == 0);
        Assert.False(badge.IsEffectivelyVisible); // nothing but infos: no counter
    }

    /// <summary>
    /// Screen readers hear the areas with their state (the counter and the dots are visual), and the area list is the only
    /// tab list: the tab control below is announced as a pane.
    /// </summary>
    [AvaloniaFact]
    public void ScreenReaders_HearTheAreasWithTheirState_AndOneTabList()
    {
        var (ctrl, window, _) = Show(Snapshot(dryRun: true));
        ListBoxItem overview = Named<ListBoxItem>(window, "NavOverview");
        ListBoxItem curves = Named<ListBoxItem>(window, "NavCurves");
        Button gear = Named<Button>(window, "SettingsButton");

        Assert.Equal("Übersicht, 1 Hinweis", AutomationProperties.GetName(overview));
        Assert.Equal("Profile & Kurven", AutomationProperties.GetName(curves));
        Assert.Equal("Einstellungen", AutomationProperties.GetName(gear));
        Assert.Equal(AutomationControlType.TabItem, ControlAutomationPeer.CreatePeerForElement(overview).GetAutomationControlType());

        ctrl.Editor.Curves.Single().Name = "Entwurf";
        ctrl.Editor.Fans.Single().Name = "Umbenannt";
        // While already dirty, the next edit's markers follow with the next tick (the comparison is coalesced).
        UiTestHelpers.PumpUntil(() => ctrl.Editor.SettingsAreaDirty);
        Assert.Equal("Profile & Kurven, ungespeicherte Änderungen", AutomationProperties.GetName(curves));
        Assert.Equal("Einstellungen, ungespeicherte Änderungen", AutomationProperties.GetName(gear));

        AutomationPeer host = ControlAutomationPeer.CreatePeerForElement(window.Find<TabControl>().Single());
        Assert.Equal(AutomationControlType.Pane, host.GetAutomationControlType());
    }

    // --- Profile button ---------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void ProfileButton_SaysConnecting_BeforeTheFirstOutcome()
    {
        var monitor = new FakeLiveMonitor(MonitorSnapshot.Unavailable("Verbinde …", ConnectionProblem.Connecting));
        var ctrl = UiTestHelpers.NewController(monitor);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ProfileDot.Connecting, ctrl.ProfileDot);
        Assert.Equal("Profil", ctrl.ProfileCaption);
        Assert.Equal("Verbinde …", Named<TextBlock>(window, "ProfileButtonText").Text);
        Ellipse dot = Named<Ellipse>(window, "ProfileDotMark");
        Assert.DoesNotContain(dot.Classes, c => c is "running" or "dryRun" or "offline"); // neutral
        ctrl.RefreshProfileMenu();
        Assert.DoesNotContain(ctrl.ProfileMenu, e => e.IsEnabled); // nothing to pick yet
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void ProfileButton_FollowsTheConnection_AndKeepsTheStatusAsTooltip()
    {
        var (ctrl, window, monitor) = Show(poll: TimeSpan.FromMilliseconds(20));
        Ellipse dot = Named<Ellipse>(window, "ProfileDotMark");
        Button button = Named<Button>(window, "ProfileButton");

        Assert.Equal("Profil läuft", ctrl.ProfileCaption);
        Assert.Equal("Silent", Named<TextBlock>(window, "ProfileButtonText").Text);
        Assert.Contains("running", dot.Classes);
        Assert.Equal("Verbunden - Profil wechseln", ToolTip.GetTip(button));
        Assert.Equal("Profil läuft: Silent", AutomationProperties.GetName(button));

        monitor.Current = Snapshot(dryRun: true);
        UiTestHelpers.PumpUntil(() => ctrl.ProfileDot == ProfileDot.DryRun);
        Assert.Equal("Profil · Dry-Run", ctrl.ProfileCaption);
        Assert.Contains("dryRun", dot.Classes);

        monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
        UiTestHelpers.PumpUntil(() => ctrl.ProfileDot == ProfileDot.Offline);
        Assert.Equal("Profil · Dienst getrennt", ctrl.ProfileCaption);
        Assert.Contains("offline", dot.Classes);
        Assert.Equal("Silent", ctrl.ProfileName); // the last known profile
        Assert.Equal("Nicht verbunden - Profil wechseln", ToolTip.GetTip(button));
    }

    [AvaloniaFact]
    public void ProfileButton_FollowsARenameOfTheRunningProfile()
    {
        var (ctrl, window, _) = Show();
        ctrl.Editor.ActiveProfile!.Name = "Nachts";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Nachts", Named<TextBlock>(window, "ProfileButtonText").Text);
    }

    // --- Profile menu -----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void ProfileMenu_ListsTheProfiles_WithTheRunningOneChecked()
    {
        var (ctrl, window, _) = Show();
        ctrl.Editor.SelectedProfile = ctrl.Editor.Profiles.Single(p => p.Id == "p-loud");
        ctrl.Editor.Curves.Single().Name = "Entwurf"; // "Loud" holds a draft
        Dispatcher.UIThread.RunJobs();

        List<MenuItem> items = OpenProfileMenu(window);

        Assert.Equal(6, items.Count); // heading, 2 profiles, separator, 2 actions
        Assert.False(items[0].IsEnabled); // the heading
        Assert.Equal("Profil wechseln", AutomationProperties.GetName(items[0]));
        MenuItem silent = items[1], loud = items[2];
        Assert.Equal(MenuItemToggleType.Radio, silent.ToggleType);
        Assert.True(silent.IsChecked);
        Assert.False(loud.IsChecked);
        Assert.Equal("Silent, 1 Kurve · 1 Lüfter, läuft", AutomationProperties.GetName(silent));
        Assert.Equal("Loud, 1 Kurve · 1 Lüfter, ungespeicherte Änderungen", AutomationProperties.GetName(loud));
        Assert.Contains(silent.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "läuft" && t.IsEffectivelyVisible);
        Assert.Contains(loud.GetVisualDescendants().OfType<Ellipse>(), e => e.Classes.Contains("dirtyDot") && e.IsEffectivelyVisible);
        Assert.Contains(items[3].GetVisualDescendants(), v => v is Separator);
        Assert.Equal(MenuItemToggleType.None, items[4].ToggleType);
        Assert.Equal("Profile bearbeiten …", AutomationProperties.GetName(items[4]));
        Assert.Equal("Neues Profil", AutomationProperties.GetName(items[5]));
    }

    /// <summary>A click in the menu switches right away while nothing is unsaved: one activation, the toast offers to switch back.</summary>
    [AvaloniaFact]
    public void ProfileMenu_Click_SwitchesLive_AndTheToastSwitchesBack()
    {
        var (ctrl, window, monitor) = Show();
        List<MenuItem> items = OpenProfileMenu(window);

        Click(window, items[2]); // "Loud"

        Assert.Equal(new[] { "p-loud" }, monitor.ActiveProfileCalls);
        Assert.Empty(monitor.ConfigCalls);
        Assert.Equal("Loud", Named<TextBlock>(window, "ProfileButtonText").Text);
        Border toast = Named<Border>(window, "EditorStatusToast");
        Assert.True(toast.IsEffectivelyVisible);
        PathIcon icon = toast.GetVisualDescendants().OfType<PathIcon>().First(p => p.Classes.Contains("toastIcon"));
        Assert.True(window.TryFindResource("IconSwap", window.ActualThemeVariant, out object? swap));
        Assert.Same(swap, icon.Data);

        Click(window, Named<Button>(window, "StatusActionButton")); // „Rückgängig"

        Assert.Equal(new[] { "p-loud", "p-silent" }, monitor.ActiveProfileCalls);
        Assert.Equal("Silent", Named<TextBlock>(window, "ProfileButtonText").Text);
        Assert.False(ctrl.Editor.UndoCommand.CanExecute(null)); // neither switch is a history step
    }

    [AvaloniaFact]
    public void ProfileMenu_NewProfile_IsADraft_OpenedInTheCurvesTab()
    {
        var (ctrl, window, monitor) = Show();
        List<MenuItem> items = OpenProfileMenu(window);

        Click(window, items[5]);

        Assert.Equal(3, ctrl.Editor.Profiles.Count);
        Assert.Same(ctrl.Editor.Profiles.Last(), ctrl.Editor.SelectedProfile);
        Assert.Equal("p-silent", ctrl.Editor.ActiveProfile!.Id); // not activated
        Assert.Equal(1, ctrl.SelectedTabIndex);
        Assert.Equal(CurveTabPane.Profile, ctrl.Editor.Pane);
        Assert.Empty(monitor.ActiveProfileCalls);
        Assert.Empty(monitor.ConfigCalls);
    }

    [AvaloniaFact]
    public void ProfileMenu_EditProfiles_OpensTheRunningProfile()
    {
        var (ctrl, window, _) = Show();
        ctrl.Editor.SelectedProfile = ctrl.Editor.Profiles.Single(p => p.Id == "p-loud");
        List<MenuItem> items = OpenProfileMenu(window);

        Click(window, items[4]);

        Assert.Equal("p-silent", ctrl.Editor.SelectedProfile!.Id);
        Assert.Equal(CurveTabPane.Profile, ctrl.Editor.Pane);
        Assert.Equal(1, ctrl.SelectedTabIndex);
    }

    [AvaloniaFact]
    public void Esc_ClosesTheProfileMenu()
    {
        var (_, window, monitor) = Show();
        Button button = Named<Button>(window, "ProfileButton");
        button.Focus();
        OpenProfileMenu(window);
        Assert.True(button.Flyout!.IsOpen);

        Press(window, Key.Escape, PhysicalKey.Escape);

        Assert.False(button.Flyout.IsOpen);
        Assert.Empty(monitor.ActiveProfileCalls);
    }

    // --- The question while changes are unsaved -------------------------------------------------------------

    private static (MainController Ctrl, MainWindow Window, FakeLiveMonitor Monitor, UnsavedChangesDialog Dialog) AskToSwitch()
    {
        var (ctrl, window, monitor) = Show();
        ctrl.Editor.Curves.Single().Name = "Quiet 2"; // a draft of the running profile
        Dispatcher.UIThread.RunJobs();
        Click(window, OpenProfileMenu(window)[2]);  // "Loud"
        return (ctrl, window, monitor, WaitForDialog(window));
    }

    [AvaloniaFact]
    public void SwitchDialog_NamesTheTargetAndTheChanges()
    {
        var (_, _, monitor, dialog) = AskToSwitch();

        Assert.Equal("Auf „Loud“ wechseln?", dialog.Title);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text ==
            "Der Dienst aktiviert den gespeicherten Stand des Profils. Deine Änderungen (1 Kurve) sind noch nicht übernommen.");
        Assert.Equal("Verwerfen & wechseln", Named<Button>(dialog, "DiscardButton").Content);
        Assert.Equal("Übernehmen & wechseln", Named<Button>(dialog, "SaveButton").Content);
        Assert.True(Named<Button>(dialog, "DiscardButton").IsEffectivelyEnabled);
        Assert.Empty(monitor.ActiveProfileCalls); // nothing goes out before the answer
        Assert.Empty(monitor.ConfigCalls);
        dialog.Close();
    }

    /// <summary>The safe answer has the focus (Enter/Space without a Tab first); afterwards the focus is back on the profile button.</summary>
    [AvaloniaFact]
    public void SwitchDialog_FocusesCancel_AndHandsTheFocusBack()
    {
        var (_, window, _, dialog) = AskToSwitch();
        UiTestHelpers.PumpUntil(() => Named<Button>(dialog, "CancelButton").IsFocused);
        Assert.True(Named<Button>(dialog, "CancelButton").IsFocused);

        Press(dialog, Key.Escape, PhysicalKey.Escape);
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count == 0);

        Assert.True(Named<Button>(window, "ProfileButton").IsFocused);
    }

    [AvaloniaFact]
    public void SwitchDialog_Cancel_ChangesNothing()
    {
        var (ctrl, window, monitor, dialog) = AskToSwitch();

        Click(dialog, Named<Button>(dialog, "CancelButton"));
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count == 0);

        Assert.Empty(window.OwnedWindows);
        Assert.Empty(monitor.ActiveProfileCalls);
        Assert.Empty(monitor.ConfigCalls);
        Assert.Equal("p-silent", ctrl.Editor.ActiveProfile!.Id);
        Assert.Equal("Quiet 2", ctrl.Editor.Curves.Single().Name);
    }

    [AvaloniaFact]
    public void SwitchDialog_Esc_IsCancel()
    {
        var (ctrl, window, monitor, dialog) = AskToSwitch();

        Press(dialog, Key.Escape, PhysicalKey.Escape);
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count == 0);

        Assert.Empty(window.OwnedWindows);
        Assert.Empty(monitor.ActiveProfileCalls);
        Assert.Empty(monitor.ConfigCalls);
        Assert.True(ctrl.Editor.HasUnsavedChanges);
    }

    /// <summary>„Übernehmen &amp; wechseln" = ONE config with the target running - never a save plus an activation.</summary>
    [AvaloniaFact]
    public void SwitchDialog_ApplyAndSwitch_SendsOneConfig_AndNoActivation()
    {
        var (ctrl, window, monitor, dialog) = AskToSwitch();

        Click(dialog, Named<Button>(dialog, "SaveButton"));
        UiTestHelpers.PumpUntil(() => monitor.ConfigCalls.Count > 0 && ctrl.Editor.CanSwitchProfile);

        AppConfig sent = Assert.Single(monitor.ConfigCalls);
        Assert.Equal("p-loud", sent.ActiveProfileId);
        Assert.Equal("Quiet 2", sent.Profiles.Single(p => p.Id == "p-silent").Curves.Single().Name);
        Assert.Empty(monitor.ActiveProfileCalls);
        Assert.False(ctrl.Editor.HasUnsavedChanges);
        Assert.Equal("Loud", Named<TextBlock>(window, "ProfileButtonText").Text);
        Assert.Equal("Auf „Loud“ gewechselt.", ctrl.Editor.Status.Text);
    }

    [AvaloniaFact]
    public void SwitchDialog_DiscardAndSwitch_SwitchesLive_AndCtrlZBringsTheDraftBack()
    {
        var (ctrl, window, monitor, dialog) = AskToSwitch();

        Click(dialog, Named<Button>(dialog, "DiscardButton"));
        UiTestHelpers.PumpUntil(() => monitor.ActiveProfileCalls.Count > 0);

        Assert.Equal(new[] { "p-loud" }, monitor.ActiveProfileCalls);
        Assert.Empty(monitor.ConfigCalls);
        Assert.False(ctrl.Editor.HasUnsavedChanges);
        Assert.Equal("Auf „Loud“ gewechselt - Änderungen verworfen (Strg+Z holt sie zurück).", ctrl.Editor.Status.Text);

        Named<Button>(window, "SettingsButton").Focus(); // a control without its own Ctrl+Z
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Dispatcher.UIThread.RunJobs();

        Assert.True(ctrl.Editor.HasUnsavedChanges);
        Assert.Equal("Quiet 2", ctrl.Editor.Curves.Single().Name);
        Assert.Equal("p-loud", ctrl.Editor.ActiveProfile!.Id); // the undo does not switch back
        Assert.Single(monitor.ActiveProfileCalls);
    }

    [AvaloniaFact]
    public void SwitchDialog_DiscardIsOff_ForAProfileThatIsOnlyADraft()
    {
        var (ctrl, window, monitor) = Show();
        ctrl.Editor.AddProfileCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        string draft = ctrl.Editor.SelectedProfile!.Id;

        ctrl.Editor.RequestProfileSwitch(draft);
        UnsavedChangesDialog dialog = WaitForDialog(window);

        Button discard = Named<Button>(dialog, "DiscardButton");
        Assert.False(discard.IsEffectivelyEnabled);
        Assert.StartsWith("„Neues Profil“ gibt es nur in den ungespeicherten Änderungen", (string?)ToolTip.GetTip(discard));
        Assert.True(ToolTip.GetShowOnDisabled(discard));
        dialog.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(monitor.ActiveProfileCalls);
    }

    // --- The daemon has the last word ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void ASwitchTheDaemonDoesNotShow_IsReported_AndTheHeaderFollowsIt()
    {
        var (ctrl, window, monitor) = Show(poll: TimeSpan.FromMilliseconds(10));
        monitor.RefuseActivations = true;

        ctrl.Editor.RequestProfileSwitch("p-loud");
        UiTestHelpers.PumpUntil(() => ctrl.Editor.Status.IsError);

        Assert.Equal("p-silent", ctrl.Editor.ActiveProfile!.Id);
        Assert.Equal("Silent", Named<TextBlock>(window, "ProfileButtonText").Text);
        Assert.Equal("„Loud“ wurde nicht aktiviert - der Dienst regelt weiter mit „Silent“.", ctrl.Editor.Status.Text);
    }

    [AvaloniaFact]
    public void ASwitchMadeElsewhere_MovesTheHeaderAlong()
    {
        var (ctrl, window, monitor) = Show(poll: TimeSpan.FromMilliseconds(10));

        monitor.Current = Snapshot(running: "p-loud");
        UiTestHelpers.PumpUntil(() => ctrl.Editor.ActiveProfile?.Id == "p-loud");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Loud", Named<TextBlock>(window, "ProfileButtonText").Text);
        Assert.Empty(monitor.ActiveProfileCalls);
        Assert.False(ctrl.Editor.HasUnsavedChanges);
    }
}
