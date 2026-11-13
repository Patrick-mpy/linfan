// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

namespace LinFan.App.UiTests;

/// <summary>
/// The unsaved-changes bar and the history in the real window: shortcuts (and where a text box keeps its own),
/// Esc on open popups, the scroll room under the floating bar, the settings dot.
/// </summary>
public class UnsavedChangesUiTests
{
    private const int TabCurves = 1;
    private const int TabSettings = 2;

    private static (MainController Ctrl, FakeLiveMonitor Fake, MainWindow Window) ShowMain(MonitorSnapshot? snapshot = null)
    {
        var fake = new FakeLiveMonitor(snapshot ?? UiTestHelpers.SampleSnapshot());
        var ctrl = UiTestHelpers.NewController(fake);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.Editor.IsConnected);
        return (ctrl, fake, window);
    }

    private static void SelectTab(MainWindow window, int index)
    {
        Assert.Single(window.Find<TabControl>()).SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(MainWindow window, Key key, PhysicalKey physical, string symbol,
                              RawInputModifiers modifiers = RawInputModifiers.Control)
    {
        window.KeyPress(key, modifiers, physical, symbol);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void CtrlZ_AndCtrlY_StepThroughTheHistory()
    {
        var (ctrl, _, window) = ShowMain();
        ctrl.Editor.Fans.Single().Name = "Umbenannt";
        Button gear = Assert.Single(window.Find<Button>(), b => b.Name == "SettingsButton");
        gear.Focus(); // a focused control that has no use for Ctrl+Z: the key bubbles up to the window
        Assert.True(gear.IsFocused);

        Press(window, Key.Z, PhysicalKey.Z, "z");
        Assert.Equal("CPU Fan", ctrl.Editor.Fans.Single().Name);

        Press(window, Key.Y, PhysicalKey.Y, "y");
        Assert.Equal("Umbenannt", ctrl.Editor.Fans.Single().Name);

        Press(window, Key.Z, PhysicalKey.Z, "z");
        Press(window, Key.Z, PhysicalKey.Z, "Z", RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal("Umbenannt", ctrl.Editor.Fans.Single().Name); // Ctrl+Shift+Z redoes as well
    }

    [AvaloniaFact]
    public void CtrlZ_InAFocusedTextBox_IsTheTextBoxsOwn()
    {
        var (ctrl, _, window) = ShowMain();
        SelectTab(window, TabSettings); // Sensoren: the search box is a plain text box
        TextBox search = Assert.Single(window.Find<TextBox>(), t => t.Name == "SensorSearchBox");
        ctrl.Editor.Fans.Single().Name = "Umbenannt";

        bool handledByTextBox = false;
        search.AddHandler(InputElement.KeyDownEvent, (_, e) => handledByTextBox |= e.Key == Key.Z && e.Handled,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        search.Focus();
        window.KeyTextInput("x");
        Dispatcher.UIThread.RunJobs();
        Press(window, Key.Z, PhysicalKey.Z, "z");

        Assert.True(handledByTextBox);                             // the text box took Ctrl+Z for itself …
        Assert.Equal("Umbenannt", ctrl.Editor.Fans.Single().Name); // … the editor history was not touched

        // Control: the same gesture outside the text box is the history undo.
        window.Focus();
        Press(window, Key.Z, PhysicalKey.Z, "z");
        Assert.Equal("CPU Fan", ctrl.Editor.Fans.Single().Name);
    }

    [AvaloniaFact]
    public void CtrlS_Saves_EvenFromATextBox_ButOnlyWhenSomethingIsUnsaved()
    {
        var (ctrl, fake, window) = ShowMain();
        window.Focus();
        Press(window, Key.S, PhysicalKey.S, "s");
        Assert.Empty(fake.ConfigCalls); // clean: nothing to save

        SelectTab(window, TabSettings);
        ctrl.Editor.Fans.Single().Name = "Umbenannt";
        Assert.Single(window.Find<TextBox>(), t => t.Name == "SensorSearchBox").Focus();
        Press(window, Key.S, PhysicalKey.S, "s");

        AppConfig sent = Assert.Single(fake.ConfigCalls);
        Assert.Equal("Umbenannt", sent.Fans.Single().Name);
        Assert.False(ctrl.Editor.HasUnsavedChanges);
    }

    /// <summary>
    /// A field that commits on LostFocus (a sensor group's name in its section header) must not lose what was typed
    /// last: Ctrl+S moves the focus away first, so the rename is in the editor before the save - and hands the focus
    /// back afterwards, to the same field (the rename keeps the section).
    /// </summary>
    [AvaloniaFact]
    public void CtrlS_CommitsAFieldThatWritesOnLostFocus_BeforeSaving()
    {
        MonitorSnapshot sample = UiTestHelpers.SampleSnapshot();
        var (ctrl, fake, window) = ShowMain(sample with
        {
            Config = sample.Config with { Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU", Group = "CPU" } } },
        });
        SelectTab(window, TabSettings); // Sensoren
        TextBox field = Assert.Single(window.Find<TextBox>(), t => t.Name == "GroupNameBox" && t.IsEffectivelyVisible);

        field.Focus();
        field.SelectAll();
        window.KeyTextInput("Kühlung");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Kühlung", field.Text);
        Assert.False(ctrl.Editor.HasUnsavedChanges); // typed, but not committed yet

        Press(window, Key.S, PhysicalKey.S, "s");

        AppConfig sent = Assert.Single(fake.ConfigCalls);
        Assert.Equal("Kühlung", sent.Sensors.Single().Group);
        Assert.Equal(new[] { "Kühlung" }, sent.SensorGroups);
        Assert.True(field.IsFocused); // typing can go on
    }

    [AvaloniaFact]
    public void Esc_ClosesAnOpenChipMenu()
    {
        var (ctrl, _, window) = ShowMain();
        SelectTab(window, TabSettings);
        ctrl.Settings.SelectedSectionItem = ctrl.Settings.Sections.Single(s => s.Section == SettingsSection.Appearance);
        Dispatcher.UIThread.RunJobs();
        Button chip = Assert.Single(window.Find<Button>(), b => b.Name == "LanguageChip");
        chip.Focus();
        var menu = Assert.IsAssignableFrom<PopupFlyoutBase>(chip.Flyout);
        menu.ShowAt(chip);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);

        Press(window, Key.Escape, PhysicalKey.Escape, "", RawInputModifiers.None);

        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void Esc_ClosesAnOpenFlyout()
    {
        // No window-level key binding may swallow Esc before an open menu sees it.
        var (_, _, window) = ShowMain();
        SelectTab(window, TabCurves);
        Button anchor = Assert.Single(window.Find<Button>(), b => b.Name == "CurveDeleteButton");
        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem { Header = "Eintrag" });
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        Assert.True(flyout.IsOpen);

        Press(window, Key.Escape, PhysicalKey.Escape, "", RawInputModifiers.None);

        Assert.False(flyout.IsOpen);
    }

    /// <summary>
    /// While the bar floats over the bottom of the tabs, every tab's scroll area has room below its content (so
    /// nothing has to stay hidden behind the bar), and none while there is no bar.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(TabCurves)]
    [InlineData(TabSettings)]
    public void ScrollAreas_GetRoomBelow_WhileTheBarIsShown(int tab)
    {
        var (ctrl, _, window) = ShowMain();
        SelectTab(window, tab);
        List<Control> contents = window.Find<ScrollViewer>()
            // the tab scrollers, not those inside list/text templates or nested in a tab (the fan table's)
            .Where(v => v.TemplatedParent is null && v.FindAncestorOfType<ScrollViewer>() is null)
            .Select(v => Assert.IsAssignableFrom<Control>(v.Content))
            .ToList();
        Assert.NotEmpty(contents);
        Assert.All(contents, c => Assert.Equal(0, c.Margin.Bottom));

        ctrl.Editor.Fans.Single().Name = "Umbenannt";
        Dispatcher.UIThread.RunJobs();

        Assert.All(contents, c => Assert.Equal(128, c.Margin.Bottom));
        if (tab != 0) // the side menus scroll on their own
            Assert.All(window.Find<ListBox>().Where(l => l.Classes.Contains("barInset")),
                l => Assert.Equal(128, l.Padding.Bottom));
    }

    [AvaloniaFact]
    public void SettingsDot_FollowsAFanEdit_AndTheBarSummarizesIt()
    {
        var (ctrl, _, window) = ShowMain();
        Ellipse settingsDot = Assert.Single(window.Find<Ellipse>(), e => e.Name == "SettingsTabDirtyDot");
        Ellipse curvesDot = Assert.Single(window.Find<Ellipse>(), e => e.Name == "CurvesTabDirtyDot");

        ctrl.Editor.Fans.Single().Name = "Umbenannt";
        Dispatcher.UIThread.RunJobs();

        Assert.True(settingsDot.IsEffectivelyVisible);
        Assert.False(curvesDot.IsEffectivelyVisible);
        TextBlock detail = Assert.Single(window.Find<TextBlock>(), t => t.Name == "UnsavedBarDetail");
        Assert.Equal("Geändert: Lüfter · noch nicht an den Hintergrunddienst übergeben.", detail.Text);

        Button redo = Assert.Single(window.Find<Button>(), b => b.Name == "RedoButton");
        Assert.False(redo.IsEffectivelyEnabled); // nothing undone yet
        Assert.Single(window.Find<Button>(), b => b.Name == "UndoButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(settingsDot.IsEffectivelyVisible);
    }
}
