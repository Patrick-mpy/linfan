// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// The curves tab separates "open in the editor" from "regulating right now": the side menu lists the profiles, a
/// click only opens one. Checked here is what lies outside the controller - the side menu's wiring (one list, one
/// highlighted row, mouse and keyboard), the banner above a profile that is not running, the curve editor's bindings
/// and where the dashboard's curves come from.
/// </summary>
public class CurveTabProfileTests
{
    private const int TabCurves = 1;

    [AvaloniaFact]
    public void ClickingAProfile_OpensTheProfileEditor_ClickingACurveComesBack()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        ProfileRow silent = controller.Editor.Profiles.Single(p => p.Id == "p-silent");

        ClickNavRow(window, i => i is ProfileNavItem p && p.Profile == silent);
        Assert.Equal(CurveTabPane.Profile, controller.Editor.Pane);
        AssertOneHighlightedRow(window, controller);

        ClickNavRow(window, i => i is CurveNavItem);
        Assert.Equal(CurveTabPane.Curve, controller.Editor.Pane);
        AssertOneHighlightedRow(window, controller);

        ClickNavRow(window, i => i is ProfileNavItem p && p.Profile == silent);
        Assert.Equal(CurveTabPane.Profile, controller.Editor.Pane);
        AssertOneHighlightedRow(window, controller);
    }

    /// <summary>
    /// Opening another profile changes the list the click came from: it must be adjusted in place - a reset in the
    /// middle of the list box's own selection change would drop the click.
    /// </summary>
    [AvaloniaFact]
    public void ClickingAnotherProfile_ExpandsIt_WithoutResettingTheList()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        var actions = new List<NotifyCollectionChangedAction>();
        controller.Editor.NavItems.CollectionChanged += (_, e) => actions.Add(e.Action);

        ClickNavRow(window, i => i is ProfileNavItem p && p.Profile.Id == "p-loud");

        Assert.Equal("p-loud", controller.Editor.SelectedProfile!.Id);
        Assert.Equal(CurveTabPane.Profile, controller.Editor.Pane);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(new[] { "Silent", "Loud", "Loud-Kurve", "+" }, NavLabels(controller));
        AssertOneHighlightedRow(window, controller);
    }

    [AvaloniaFact]
    public void TheAddCurveRow_NeverTakesTheSelection()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        CurveTabNavItem before = controller.Editor.SelectedNavItem!;

        ClickNavRow(window, i => i is AddCurveNavItem);

        Assert.Same(before, controller.Editor.SelectedNavItem);
        AssertOneHighlightedRow(window, controller);
        Button add = Assert.Single(window.Find<Button>(), b => b.Name == "AddCurveButton");
        Assert.True(add.Flyout!.IsOpen); // the click opened the template menu instead
        add.Flyout.Hide();
    }

    [AvaloniaFact]
    public void EnterOnTheAddCurveRow_OpensTheTemplateMenu()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        CurveTabNavItem before = controller.Editor.SelectedNavItem!;
        ListBoxItem row = window.Find<ListBoxItem>().Single(i => i.DataContext is AddCurveNavItem);
        Assert.True(row.Focus());

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Button add = Assert.Single(window.Find<Button>(), b => b.Name == "AddCurveButton");
        Assert.True(add.Flyout!.IsOpen);
        add.Flyout.Hide();
        Assert.Same(before, controller.Editor.SelectedNavItem);
        AssertOneHighlightedRow(window, controller);
    }

    /// <summary>
    /// Down from the "add curve" row opens the next profile - and the keyboard stays on that row: opening moves the
    /// "+" row below the new profile, which must not move (and so recycle) the focused profile row.
    /// </summary>
    [AvaloniaFact]
    public void ArrowingFromTheAddRowToTheNextProfile_KeepsTheFocusThere()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        ListBoxItem addRow = window.Find<ListBoxItem>().Single(i => i.DataContext is AddCurveNavItem);
        Assert.True(addRow.Focus());

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("p-loud", controller.Editor.SelectedProfile!.Id);
        ListBoxItem focused = Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
        Assert.Equal("p-loud", Assert.IsType<ProfileNavItem>(focused.DataContext).Profile.Id);
        Assert.True(focused.IsAttachedToVisualTree());
        AssertOneHighlightedRow(window, controller);
    }

    /// <summary>The rows and the steppers carry names for assistive tech.</summary>
    [AvaloniaFact]
    public void TheRowsAndSteppers_HaveAccessibleNames()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());

        Assert.Equal(new[] { "Silent, läuft", "Quiet - CPU", "Kurve hinzufügen", "Loud" },
            controller.Editor.NavItems.Select(i => i.ToString()));

        controller.Editor.SelectedCurve!.ShowPoints = true;
        Dispatcher.UIThread.RunJobs();
        TextBox pointTemperature = window.Find<TextBox>()
            .First(t => t.Name == "PART_TextBox" && t.TemplatedParent is NumericUpDown { DataContext: PointRow });
        Assert.Equal("Temperatur von Punkt 1 in °C", AutomationProperties.GetName(pointTemperature));
        RepeatButton plus = window.Find<RepeatButton>().First(b => b.Name == "PART_IncreaseButton");
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(plus));
    }

    /// <summary>Arrowing onto the "add curve" row moves the keyboard there, but the highlight stays on what is open.</summary>
    [AvaloniaFact]
    public void ArrowingOntoTheAddCurveRow_KeepsTheHighlight()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        ListBoxItem curveRow = window.Find<ListBoxItem>().Single(i => i.DataContext is CurveNavItem);
        Assert.True(curveRow.Focus());
        CurveTabNavItem before = controller.Editor.SelectedNavItem!;

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<AddCurveNavItem>(Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement()).DataContext);
        Assert.Same(before, controller.Editor.SelectedNavItem);
        AssertOneHighlightedRow(window, controller);

        // The list still follows the controller after it put its row back.
        controller.Editor.Pane = CurveTabPane.Profile;
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<ProfileNavItem>(NavList(window).SelectedItem);
        AssertOneHighlightedRow(window, controller);
    }

    [AvaloniaFact]
    public void TheTemplateMenu_AddsTheChosenTemplate()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        Button add = Assert.Single(window.Find<Button>(), b => b.Name == "AddCurveButton");
        Assert.Equal("Kurve hinzufügen", AutomationProperties.GetName(add));

        add.Flyout!.ShowAt(add);
        Dispatcher.UIThread.RunJobs();
        // The entries are generated from CurveTemplates.Menu: the header line (off), then the four templates.
        List<MenuItem> entries = window.Find<MenuItem>().ToList();
        Assert.Equal(CurveTemplates.Menu.Count, entries.Count);
        Assert.False(entries[0].IsEffectivelyEnabled);
        Assert.Equal("Leise", AutomationProperties.GetName(entries[1]));
        MenuItem performance = entries.Single(m => m.CommandParameter is CurveTemplate.Performance);
        Assert.True(performance.IsEffectivelyEnabled);
        Assert.True(performance.Command!.CanExecute(performance.CommandParameter));
        performance.Command.Execute(performance.CommandParameter);
        add.Flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, controller.Editor.Curves.Count);
        Assert.Equal("Neue Kurve · Leistung", controller.Editor.SelectedCurve!.Name);
        Assert.Equal(CurveTabPane.Curve, controller.Editor.Pane);
        AssertOneHighlightedRow(window, controller);
    }

    [AvaloniaFact]
    public void TheBanner_ShowsOnAnInactiveProfile_AndActivatesIt()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        Border banner = Assert.Single(window.Find<Border>(), b => b.Name == "InactiveProfileBanner");
        Assert.False(banner.IsVisible); // the running profile is open

        controller.Editor.SelectedProfile = controller.Editor.Profiles.Single(p => p.Id == "p-loud");
        Dispatcher.UIThread.RunJobs();
        Assert.True(banner.IsVisible);
        Assert.Equal("Du bearbeitest „Loud“ - aktiv ist „Silent“", controller.Editor.EditingInactiveProfileText);

        Button activate = Assert.Single(window.Find<Button>(), b => b.Name == "ActivateProfileButton");
        Assert.True(activate.IsEffectivelyEnabled);
        activate.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("p-loud", controller.Editor.ActiveProfile!.Id);
        Assert.False(banner.IsVisible);
    }

    /// <summary>
    /// Offline nothing can switch the running profile - not the header's menu, not the banner button: the command would
    /// go nowhere, while the editor would already treat the other profile as running.
    /// </summary>
    [AvaloniaFact]
    public void NothingSwitchesTheRunningProfile_WhileOffline()
    {
        var monitor = new FakeLiveMonitor(TwoProfileSnapshot());
        var controller = UiTestHelpers.NewController(monitor, monitor);
        var window = new MainWindow { DataContext = controller };
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);
        window.Find<TabControl>().Single().SelectedIndex = TabCurves;
        controller.Editor.SelectedProfile = controller.Editor.Profiles.Single(p => p.Id == "p-loud");
        Dispatcher.UIThread.RunJobs();
        Button activate = Assert.Single(window.Find<Button>(), b => b.Name == "ActivateProfileButton");
        Assert.True(activate.IsEffectivelyEnabled);

        monitor.Current = TwoProfileSnapshot() with { Connected = false };
        UiTestHelpers.PumpUntil(() => !controller.Editor.IsConnected);
        Dispatcher.UIThread.RunJobs();

        Assert.False(activate.IsEffectivelyEnabled);
        controller.RefreshProfileMenu();
        ProfileMenuEntry loud = controller.ProfileMenu.Single(e => e.IsProfile && e.Name == "Loud");
        Assert.False(loud.IsEnabled);
        Assert.StartsWith("Nicht verbunden", loud.Tooltip);
        loud.Command!.Execute(null);                 // even a direct call is refused
        controller.Editor.RequestProfileSwitch("p-loud");
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(monitor.ActiveProfileCalls);
        Assert.Empty(monitor.ConfigCalls);
        Assert.Equal("p-silent", controller.Editor.ActiveProfile!.Id);

        monitor.Current = TwoProfileSnapshot();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsConnected);
        Dispatcher.UIThread.RunJobs();
        Assert.True(activate.IsEffectivelyEnabled);
    }

    /// <summary>Unsaved changes do not lock the banner button any more - activating asks what to do with them.</summary>
    [AvaloniaFact]
    public void TheActivateButton_AsksWhileChangesAreUnsaved()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot(), height: 800);
        controller.Editor.SelectedProfile = controller.Editor.Profiles.Single(p => p.Id == "p-loud");
        controller.Editor.Curves.Single().Name = "Entwurf";
        Dispatcher.UIThread.RunJobs();

        Button activate = Assert.Single(window.Find<Button>(), b => b.Name == "ActivateProfileButton");
        Assert.True(activate.IsEffectivelyEnabled);
        Assert.EndsWith("ob sie vorher übernommen werden.", controller.Editor.ActivateProfileHint);
        Click(window, activate);
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count > 0);

        UnsavedChangesDialog dialog = Assert.IsType<UnsavedChangesDialog>(Assert.Single(window.OwnedWindows));
        Assert.Equal("Auf „Loud“ wechseln?", dialog.Title);
        dialog.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("p-silent", controller.Editor.ActiveProfile!.Id); // closing the dialog is "cancel"
        Assert.True(controller.Editor.HasUnsavedChanges);
    }

    /// <summary>The chart's bands and commands are bound to the curve row - including the decimal → double hysteresis.</summary>
    [AvaloniaFact]
    public void TheChart_IsWiredToTheCurveRow()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        CurveEditRow curve = controller.Editor.SelectedCurve!;
        curve.Hysteresis = 2.5m;
        controller.Editor.Fans.Single().MinPwm = 64; // 25 %
        Dispatcher.UIThread.RunJobs();

        CurveChart chart = Assert.Single(window.Find<CurveChart>(), c => c.Name == "CurveEditorChart");
        Assert.Equal(2.5, chart.Hysteresis);
        Assert.Equal("±2.5°", chart.HysteresisLabel);
        Assert.Equal(25, chart.LowerLimit);
        Assert.Equal("Min 25 % · CPU Fan", chart.LowerLimitLabel);
        Assert.Null(chart.UpperLimit);
        Assert.Same(curve.AddPointAtCommand, chart.AddPointCommand);
    }

    [AvaloniaFact]
    public void TheMix_ShowsWithTwoSources_AndItsSwitchWritesTheAggregation()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        CurveEditRow curve = controller.Editor.SelectedCurve!;
        ListBox mix = Assert.Single(window.Find<ListBox>(), l => l.Name == "AggregationSwitch");
        Assert.False(mix.IsEffectivelyVisible); // one source

        curve.SensorChecks.Single(c => c.Sensor.Id == "hwmon0/temp2").Selected = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(mix.IsEffectivelyVisible);

        mix.SelectedItem = AggregationOption.For(SensorAggregation.Avg);
        Assert.Equal(SensorAggregation.Avg, curve.Aggregation);

        ListBox shape = Assert.Single(window.Find<ListBox>(), l => l.Name == "InterpolationSwitch");
        shape.SelectedIndex = 1; // Spline - the second option
        Assert.Equal(InterpolationMode.Spline, curve.InterpolationMode);
    }

    [AvaloniaFact]
    public void TheSourceSections_OpenWithASelection_AndTheirHeaderSelectsTheGroup()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        CurveEditRow curve = controller.Editor.SelectedCurve!;
        SensorCheckGroup cpu = Assert.Single(controller.Editor.SourceSensorSections, g => g.Name == "CPU");
        Assert.True(cpu.IsOpen);            // holds the selected source
        Assert.Null(cpu.AllState);          // 1 of 2

        CheckBox header = window.Find<CheckBox>().Single(c => c.DataContext == cpu && c.Classes.Contains("triState"));
        Assert.Null(header.IsChecked);
        Click(window, header);

        Assert.Equal(2, curve.Sources.Count);
        Assert.True(header.IsChecked);
        Assert.Equal("2 von 2 gewählt", cpu.Summary);
    }

    [AvaloniaFact]
    public void EditingANonActiveProfile_LeavesTheDashboardOnTheRunningCurves()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        LoopRow before = Assert.Single(controller.Loops);
        Assert.Equal("quiet", before.Curve.Id); // p-silent läuft

        controller.Editor.SelectedProfile = controller.Editor.Profiles.Single(p => p.Id == "p-loud");
        controller.Editor.Curves.Single().Name = "Entwurf";
        UiTestHelpers.PumpUntil(() => false, timeoutMs: 60); // ein paar Live-Ticks abwarten
        Dispatcher.UIThread.RunJobs();

        LoopRow after = Assert.Single(controller.Loops);
        Assert.Equal("quiet", after.Curve.Id);   // unverändert das laufende Profil
        Assert.NotEqual("Entwurf", after.Curve.Name);
        Assert.Equal("p-silent", controller.Editor.ActiveProfile!.Id);
        Assert.False(controller.Editor.SelectedProfileIsActive);
        Assert.False(controller.Editor.Curves.Single().IsActive); // Badge: regelt nicht
    }

    /// <param name="height">A taller window keeps the content above the floating unsaved-changes bar.</param>
    private static (MainController Controller, MainWindow Window) ShowCurvesTab(MonitorSnapshot snapshot, double? height = null)
    {
        var controller = UiTestHelpers.NewController(new FakeLiveMonitor(snapshot));
        var window = new MainWindow { DataContext = controller };
        if (height is { } h)
            window.Height = h;
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);
        window.Find<TabControl>().Single().SelectedIndex = TabCurves;
        Dispatcher.UIThread.RunJobs();
        return (controller, window);
    }

    private static ListBox NavList(MainWindow window) => Assert.Single(window.Find<ListBox>(), l => l.Name == "CurveNavList");

    private static string[] NavLabels(MainController controller) => controller.Editor.NavItems.Select(i => i switch
    {
        ProfileNavItem p => p.Profile.Name,
        CurveNavItem c => c.Curve.Name,
        _ => "+",
    }).ToArray();

    /// <summary>The list box and the controller agree, and exactly one container is drawn as selected.</summary>
    private static void AssertOneHighlightedRow(MainWindow window, MainController controller)
    {
        ListBox list = NavList(window);
        Assert.NotNull(controller.Editor.SelectedNavItem);
        Assert.Same(controller.Editor.SelectedNavItem, list.SelectedItem);
        Assert.Single(list.GetVisualDescendants().OfType<ListBoxItem>(), i => i.IsSelected);
    }

    /// <summary>Clicks the side menu row whose entry satisfies <paramref name="match"/>.</summary>
    private static void ClickNavRow(MainWindow window, Func<CurveTabNavItem, bool> match)
    {
        ListBoxItem row = NavList(window).GetVisualDescendants().OfType<ListBoxItem>()
            .First(i => i.DataContext is CurveTabNavItem item && match(item));
        Click(window, row);
    }

    private static void Click(MainWindow window, Control target)
    {
        Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
                       ?? throw new InvalidOperationException("The row is not rendered.");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Two profiles with a curve each; "p-silent" runs and drives the one fan. Two CPU sensors, so the source group "CPU"
    /// has a partial and a full state.
    /// </summary>
    private static MonitorSnapshot TwoProfileSnapshot()
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
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU", Group = "CPU" },
                new SensorConfig { SensorId = "hwmon0/temp2", Name = "CPU 2", Group = "CPU" },
            },
            Curves = new[] { quiet },
            Fans = new[] { new FanConfig { FanId = "hwmon0/pwm1", Name = "CPU Fan", AssignedCurveId = "quiet" } },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p-silent", Name = "Silent", Curves = new[] { quiet },
                    Assignments = new[] { new ProfileAssignment("hwmon0/pwm1", "quiet") },
                },
                new Profile
                {
                    Id = "p-loud", Name = "Loud", Curves = new[] { loud },
                    Assignments = new[] { new ProfileAssignment("hwmon0/pwm1", "loud") },
                },
            },
            ActiveProfileId = "p-silent",
        };

        return new MonitorSnapshot(
            "Verbunden",
            new[]
            {
                new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0),
                new SensorReading("hwmon0/temp2", "CPU 2", SensorKind.Temperature, "°C", 47.0),
                new SensorReading("hwmon0/fan1", "CPU Fan", SensorKind.FanRpm, "RPM", 1200),
            },
            new[] { new FanReading("hwmon0/pwm1", "CPU Fan", 1200, 120, CanControl: true) },
            config,
            Connected: true);
    }
}
