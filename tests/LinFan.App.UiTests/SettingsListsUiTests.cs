// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Settings › Sensoren and › Lüfter in the real window: sections per group / position, the header name field, the
/// choice menus (headings, separator, current mark), the bulk bars, the identify chip in the row. OS drag and drop
/// cannot be driven headless - the drop path is covered at the controller (CurveEditorGroupsTests).
/// </summary>
public class SettingsListsUiTests
{
    private const int TabSettings = 2;

    private static MonitorSnapshot Snapshot(bool connected = true) => new(
        "Verbunden",
        new[]
        {
            new SensorReading("k10temp/temp1", "Tctl", SensorKind.Temperature, "°C", 50),
            new SensorReading("amdgpu/temp1", "edge", SensorKind.Temperature, "°C", 45),
            new SensorReading("nvme/temp1", "Composite", SensorKind.Temperature, "°C", 40),
        },
        new[]
        {
            new FanReading("nct/pwm1", "CPU Fan", 1200, 120, CanControl: true),
            new FanReading("nct/pwm2", "Front", 900, 100, CanControl: true),
            new FanReading("nct/fan3", "Pump", 2000, 0, CanControl: false),
        },
        new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "k10temp/temp1", Name = "Tctl", Group = "CPU" } },
            SensorGroups = new[] { "CPU", "Leer" },
            Fans = new[]
            {
                new FanConfig { FanId = "nct/pwm1", Name = "CPU Fan", Location = FanLocation.CpuCooler },
                new FanConfig { FanId = "nct/pwm2", Name = "Front", Location = FanLocation.CaseFrontIntake },
            },
        },
        Connected: connected);

    private static (MainController Ctrl, MainWindow Window) Show(SettingsSection section, bool connected = true)
    {
        var monitor = new FakeLiveMonitor(Snapshot(connected: true));
        var (ctrl, window) = Show(section, monitor);
        if (!connected)
        {
            // Offline after having been connected - an offline snapshot carries no devices of its own.
            monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
            UiTestHelpers.PumpUntil(() => !ctrl.Connected);
        }
        return (ctrl, window);
    }

    private static (MainController Ctrl, MainWindow Window) Show(SettingsSection section, FakeLiveMonitor monitor)
    {
        var ctrl = UiTestHelpers.NewController(monitor);
        // Tall enough for the longest menu (the position menu) to open below its chip inside the window.
        var window = new MainWindow { DataContext = ctrl, Height = 800 };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        Assert.Single(window.Find<TabControl>()).SelectedIndex = TabSettings;
        ctrl.Settings.SelectedSectionItem = ctrl.Settings.Sections.Single(s => s.Section == section);
        Dispatcher.UIThread.RunJobs();
        return (ctrl, window);
    }

    private static void Click(MainWindow window, Control target)
    {
        Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
                       ?? throw new InvalidOperationException("Not rendered.");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Opens the flyout of <paramref name="button"/> and returns its entries.</summary>
    private static List<MenuItem> OpenMenu(MainWindow window, Button button)
    {
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<MenuItem>().ToList();
    }

    [AvaloniaFact]
    public void Sensors_OneSectionPerGroup_WithItsRows_TheFixedUngroupedLast()
    {
        var (ctrl, window) = Show(SettingsSection.Sensors);

        List<Border> sections = window.Find<Border>().Where(b => b.Classes.Contains("groupSection")).ToList();
        Assert.Equal(new[] { "CPU", "Leer", "Ungruppiert" },
                     sections.Select(b => Assert.IsType<SensorGroupSection>(b.DataContext).Name));
        Assert.True(DragDrop.GetAllowDrop(sections[0])); // a drop target for the grip drag

        // Rows sit in their section; the fixed section has no name field, no ↑/↓/delete.
        Border ungrouped = sections[2];
        Assert.Equal(new[] { "amdgpu/temp1", "nvme/temp1" },
                     ungrouped.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("deviceRow"))
                         .Select(b => ((SensorOption)b.DataContext!).Id));
        Assert.DoesNotContain(ungrouped.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "GroupNameBox" && t.IsEffectivelyVisible);
        Assert.Contains(ungrouped.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ungruppiert" && t.IsEffectivelyVisible);
        Assert.DoesNotContain(ungrouped.GetVisualDescendants().OfType<Button>(),
                              b => b.Command == ctrl.Editor.SensorGroupSections[2].DeleteCommand && b.IsEffectivelyVisible);

        // The empty configured group explains how to fill it.
        Assert.Contains(sections[1].GetVisualDescendants().OfType<TextBlock>(),
                        t => t.IsEffectivelyVisible && t.Text!.StartsWith("Noch leer", StringComparison.Ordinal));
        Assert.Empty(window.Find<AutoCompleteBox>()); // the old group chip is gone
    }

    [AvaloniaFact]
    public void GroupName_WarnsWhileTyping_AndEnterMergesIntoTheExistingGroup()
    {
        var (ctrl, window) = Show(SettingsSection.Sensors);
        TextBox name = window.Find<TextBox>().Single(t => t.Name == "GroupNameBox" && t.DataContext is SensorGroupSection { Name: "Leer" });
        TextBlock warning = window.Find<TextBlock>().Single(t => t.Text == "Name doppelt" && t.DataContext == name.DataContext);
        Assert.False(warning.IsEffectivelyVisible);

        name.Focus();
        name.Text = "cpu";
        Dispatcher.UIThread.RunJobs();
        Assert.True(warning.IsEffectivelyVisible);
        Assert.Equal(new[] { "CPU", "Leer" }, ctrl.Editor.SensorGroups); // not committed yet

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "CPU" }, ctrl.Editor.SensorGroups);
        Assert.True(ctrl.Editor.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void GroupMenu_MarksTheCurrentGroup_HasASeparator_AndAClickMovesTheSensor()
    {
        var (ctrl, window) = Show(SettingsSection.Sensors);
        SensorOption edge = ctrl.Editor.Sensors.Single(s => s.Id == "amdgpu/temp1");
        Button chip = window.Find<Button>().Single(b => b.Name == "SensorGroupChip" && b.DataContext == edge);
        chip.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(chip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ungruppiert");

        List<MenuItem> items = OpenMenu(window, chip);

        Assert.Equal(new[] { "CPU", "Leer", "Ungruppiert", "-", "+ Neue Gruppe …" }, items.Select(i => i.Header as string));
        Assert.True(items.Single(i => (string?)i.Header == "Ungruppiert").IsChecked);
        Assert.False(items.Single(i => (string?)i.Header == "CPU").IsChecked);
        Assert.Contains(items.Single(i => (string?)i.Header == "-").GetVisualDescendants(), v => v is Separator);

        Click(window, items.Single(i => (string?)i.Header == "Leer"));

        Assert.Equal("Leer", edge.Group);
        // The row now lives in the "Leer" section - a new row, so look its chip up again.
        Button moved = window.Find<Button>().Single(b => b.Name == "SensorGroupChip" && b.DataContext == edge);
        Border section = moved.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("groupSection"));
        Assert.Equal("Leer", Assert.IsType<SensorGroupSection>(section.DataContext).Name);
        Assert.Contains(moved.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Leer");
    }

    [AvaloniaFact]
    public void BulkBar_FollowsTheRowChecks_AndClearsThem()
    {
        var (ctrl, window) = Show(SettingsSection.Sensors);
        Border bar = window.Find<Border>().Single(b => b.Name == "SensorBulkBar");
        Assert.False(bar.IsEffectivelyVisible);

        CheckBox rowCheck = window.Find<CheckBox>().Single(c => c.DataContext is SensorOption { Id: "nvme/temp1" });
        rowCheck.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Click(window, rowCheck);

        Assert.True(bar.IsEffectivelyVisible);
        Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1 Sensor ausgewählt");
        Assert.False(ctrl.Editor.HasUnsavedChanges);

        Button clear = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Command == ctrl.Editor.ClearSensorSelectionCommand);
        Click(window, clear);
        Assert.False(bar.IsEffectivelyVisible);
        Assert.False(rowCheck.IsChecked);
    }

    [AvaloniaFact]
    public void Fans_OneSectionPerPosition_WithTheDirectionColour()
    {
        var (ctrl, window) = Show(SettingsSection.Fans);

        List<Border> sections = window.Find<Border>().Where(b => b.Classes.Contains("groupSection")).ToList();
        Assert.Equal(new[] { FanLocation.CpuCooler, FanLocation.CaseFrontIntake, FanLocation.Unspecified },
                     sections.Select(b => Assert.IsType<FanLocationSection>(b.DataContext).Location));
        Assert.Equal(Token(window, "InternalBrush"), FlowColour(sections[0]));
        Assert.Equal(Token(window, "IntakeBrush"), FlowColour(sections[1]));
        Assert.Equal(Token(window, "TextFaintest"), FlowColour(sections[2])); // no position: neutral
    }

    private static Color Token(MainWindow window, string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, window.ActualThemeVariant, out object? value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color FlowColour(Border section) => Assert.IsAssignableFrom<ISolidColorBrush>(
        section.GetVisualDescendants().OfType<PathIcon>().Single(p => p.Classes.Contains("flow")).Foreground).Color;

    [AvaloniaFact]
    public void IdentifyChip_SitsInTheRow_OnlyForControllableFans_AndIsGoneFromAdvanced()
    {
        var monitor = new FakeLiveMonitor(Snapshot());
        var (ctrl, window) = Show(SettingsSection.Fans, monitor);
        foreach (FanAssignRow f in ctrl.Editor.Fans)
            f.ShowAdvanced = true;
        Dispatcher.UIThread.RunJobs();

        List<Button> chips = window.Find<Button>().Where(b => b.Name == "IdentifyChip").ToList();
        Assert.Equal(3, chips.Count);
        foreach (Button chip in chips)
            Assert.Equal(((FanAssignRow)chip.DataContext!).CanControl, chip.IsEffectivelyVisible);
        // "Advanced" is open, and still no other "Identify" button anywhere.
        Assert.All(window.Find<Button>().Where(b => UiTestHelpers.ButtonLabel(b) == "Identifizieren"),
                   b => Assert.Equal("IdentifyChip", b.Name));

        Button cpuFan = chips.Single(c => ((FanAssignRow)c.DataContext!).FanId == "nct/pwm1");
        Click(window, cpuFan);
        Assert.Equal(new[] { "nct/pwm1" }, monitor.IdentifyCalls);
    }

    [AvaloniaFact]
    public void IdentifyChip_Offline_IsDisabled_Faded_AndItsTooltipSaysWhy()
    {
        var (_, window) = Show(SettingsSection.Fans, connected: false);

        Button chip = window.Find<Button>().First(b => b.Name == "IdentifyChip" && b.IsVisible);
        Assert.False(chip.IsEffectivelyEnabled);
        Assert.Equal(0.42, chip.Opacity, 3);
        Assert.True(ToolTip.GetShowOnDisabled(chip));
        Assert.Equal("Gesperrt - der Hintergrunddienst ist nicht erreichbar.", ToolTip.GetTip(chip));
        Assert.Equal("Identifizieren", AutomationProperties.GetName(chip));
    }

    [AvaloniaFact]
    public void HeaderCheck_OfAGroupWithoutShownRows_IsOff_AndAClickLeavesItUnticked()
    {
        var (ctrl, window) = Show(SettingsSection.Sensors);
        CheckBox Header(string group) => window.Find<CheckBox>()
            .Single(c => c.Classes.Contains("triState") && c.DataContext is SensorGroupSection s && s.Name == group);

        CheckBox empty = Header("Leer");
        Assert.False(empty.IsEffectivelyEnabled);
        Assert.Equal("Gruppe Leer: alle auswählen", AutomationProperties.GetName(empty));

        // All of the group hidden by "hide here too": nothing to select either - a click must not leave it ticked.
        ctrl.Editor.Sensors.Single(s => s.Id == "k10temp/temp1").Visible = false;
        ctrl.Editor.ShowHiddenSensors = false;
        Dispatcher.UIThread.RunJobs();
        CheckBox cpu = Header("CPU");
        cpu.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Click(window, cpu);

        Assert.False(cpu.IsChecked);
        Assert.False(ctrl.Editor.HasSensorSelection);
    }

    [AvaloniaFact]
    public void PositionMenu_DiagramEntry_OpensTheDialog_AndCancelLeavesTheMarkOnThePosition()
    {
        var (ctrl, window) = Show(SettingsSection.Fans);
        FanAssignRow front = ctrl.Editor.Fans.Single(f => f.FanId == "nct/pwm2");
        Button chip = window.Find<Button>().Single(b => b.Name == "PositionChip" && b.DataContext == front);
        Assert.Equal("Position: Gehäuse vorn · Einlass", AutomationProperties.GetName(chip));

        Click(window, OpenMenu(window, chip).Last());
        FanLocationDialog dialog = Assert.Single(window.OwnedWindows.OfType<FanLocationDialog>());
        dialog.Close(); // cancel
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FanLocation.CaseFrontIntake, front.Location.Value);

        // The reused menu: the action entry carries no mark, the current position still does.
        List<MenuItem> items = OpenMenu(window, chip);
        Assert.False(items.Last().IsChecked);
        Assert.Equal(MenuItemToggleType.None, items.Last().ToggleType);
        Assert.Equal("Vorn", Assert.Single(items, i => i.IsChecked).Header);
    }

    [AvaloniaFact]
    public void BulkPositionMenu_HasNoMarks_BeforeOrAfterAPick()
    {
        var (ctrl, window) = Show(SettingsSection.Fans);
        ctrl.Editor.Fans.Single(f => f.FanId == "nct/pwm2").IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        Button bulk = window.Find<Button>().Single(b => b.Name == "FanBulkPositionButton");

        Click(window, OpenMenu(window, bulk).First(i => (string?)i.Header == "Netzteil"));
        Assert.Equal(FanLocation.Psu, ctrl.Editor.Fans.Single(f => f.FanId == "nct/pwm2").Location.Value);

        ctrl.Editor.Fans.Single(f => f.FanId == "nct/pwm1").IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(OpenMenu(window, bulk), i => i.IsChecked);
    }

    [AvaloniaFact]
    public void PositionMenu_HeadingsAreNotPickable_AndAPickMovesTheFanToThatSection()
    {
        var (ctrl, window) = Show(SettingsSection.Fans);
        FanAssignRow front = ctrl.Editor.Fans.Single(f => f.FanId == "nct/pwm2");
        Button chip = window.Find<Button>().Single(b => b.Name == "PositionChip" && b.DataContext == front);

        List<MenuItem> items = OpenMenu(window, chip);

        MenuItem heading = items.Single(i => (string?)i.Header == "Gehäuse · Einlass");
        Assert.False(heading.IsEffectivelyEnabled);
        Assert.True(items.Single(i => i.IsChecked).Header is "Vorn");
        Assert.Equal("Im Gehäuse-Diagramm wählen …", items.Last().Header);

        Click(window, items.First(i => (string?)i.Header == "Netzteil"));

        Assert.Equal(FanLocation.Psu, front.Location.Value);
        Assert.Contains(window.Find<Border>(), b => b.DataContext is FanLocationSection { Location: FanLocation.Psu });
    }
}
