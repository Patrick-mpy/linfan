// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Settings › Sensoren and › Lüfter: the sensor group sections (order, auto-group, new, rename/merge, reorder, delete,
/// menus, selection, the drop path) and the fan position sections (order, position menu, bulk actions, identify).
/// Every group or position change is an editor edit with its own undo step; the selection never is. OS drag and drop
/// cannot run headless - the drop path is covered through the commands the view calls.
/// </summary>
public sealed class CurveEditorGroupsTests
{
    private const string Cpu = "k10temp/temp1";      // classifier: CPU
    private const string Gpu = "amdgpu/temp1";       // GPU
    private const string Nvme = "nvme/temp1";        // Storage
    private const string Board = "nct6798/temp1";    // Mainboard
    private const string Acpi = "acpitz/temp1";      // Mainboard

    private static MonitorSnapshot Snapshot(AppConfig config, bool connected = true, IdentifyStatus? identify = null,
                                            CalibrationStatus? calibration = null) => new(
        "test",
        new[]
        {
            new SensorReading(Cpu, "Tctl", SensorKind.Temperature, "°C", 50),
            new SensorReading(Gpu, "edge", SensorKind.Temperature, "°C", 45),
            new SensorReading(Nvme, "Composite", SensorKind.Temperature, "°C", 40),
            new SensorReading(Board, "SYSTIN", SensorKind.Temperature, "°C", 35),
            new SensorReading(Acpi, "acpitz", SensorKind.Temperature, "°C", 30),
        },
        new[]
        {
            new FanReading("nct6798/pwm1", "CPU Fan", 1200, 120, CanControl: true),
            new FanReading("nct6798/pwm2", "Front", 900, 100, CanControl: true),
            new FanReading("nct6798/pwm3", "Rear", 800, 90, CanControl: true),
            new FanReading("nct6798/fan4", "Pump", 2000, 0, CanControl: false),
        },
        config,
        Connected: connected,
        Calibration: calibration,
        Identify: identify);

    /// <summary>
    /// Groups: "Zone" configured (listed first although empty), "Mainboard" configured, "CPU" used but not listed; the
    /// GPU, NVMe and ACPI sensors are ungrouped. Fans: one CPU cooler, one front intake, one rear exhaust, one unplaced.
    /// </summary>
    private static AppConfig Config() => new()
    {
        Sensors = new[]
        {
            new SensorConfig { SensorId = Cpu, Name = "Tctl", Group = "CPU" },
            new SensorConfig { SensorId = Board, Name = "SYSTIN", Group = "Mainboard" },
        },
        SensorGroups = new[] { "Zone", "Mainboard" },
        Fans = new[]
        {
            new FanConfig { FanId = "nct6798/pwm1", Name = "CPU Fan", Location = FanLocation.CpuCooler },
            new FanConfig { FanId = "nct6798/pwm2", Name = "Front", Location = FanLocation.CaseFrontIntake },
            new FanConfig { FanId = "nct6798/pwm3", Name = "Rear", Location = FanLocation.CaseRearExhaust },
        },
        Curves = new[]
        {
            new CurveConfig
            {
                Id = "c1", Name = "Quiet", SourceSensorIds = new[] { Cpu },
                Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
            },
        },
    };

    private static (CurveEditorController Ctrl, ManualClock Clock, List<AppConfig> Saved) Editor(AppConfig? config = null)
    {
        var saved = new List<AppConfig>();
        var clock = new ManualClock();
        var identified = new List<string>();
        var ctrl = new CurveEditorController(
            (c, _) => { saved.Add(c); return Task.FromResult(true); },
            identify: id => { identified.Add(id); return Task.CompletedTask; },
            clock: clock)
        {
            IsConnected = true,
        };
        ctrl.Initialize(Snapshot(config ?? Config()));
        return (ctrl, clock, saved);
    }

    private static string[] SectionNames(CurveEditorController ctrl) => ctrl.SensorGroupSections.Select(s => s.Name).ToArray();

    private static SensorGroupSection Section(CurveEditorController ctrl, string name) =>
        Assert.Single(ctrl.SensorGroupSections, s => s.Name == name);

    private static SensorOption Sensor(CurveEditorController ctrl, string id) => ctrl.Sensors.Single(s => s.Id == id);

    // --- Sections --------------------------------------------------------------------------------

    [Fact]
    public void Sections_ConfiguredOrderFirst_ThenUnlistedAlphabetically_UngroupedLast()
    {
        var (ctrl, _, _) = Editor(Config() with
        {
            Sensors = Config().Sensors.Append(new SensorConfig { SensorId = Nvme, Name = "Composite", Group = "Aux" }).ToList(),
        });

        Assert.Equal(new[] { "Zone", "Mainboard", "Aux", "CPU", "Ungruppiert" }, SectionNames(ctrl));
        SensorGroupSection ungrouped = ctrl.SensorGroupSections.Last();
        Assert.True(ungrouped.IsUngrouped);
        Assert.Equal(new[] { Gpu, Acpi }, ungrouped.Rows.Select(r => r.Id));
        Assert.False(ctrl.HasUnsavedChanges); // building the sections is no edit
    }

    [Fact]
    public void Sections_CountTexts_AndTheEmptyHint()
    {
        var (ctrl, _, _) = Editor();

        Assert.Equal("leer", Section(ctrl, "Zone").CountText);
        Assert.True(Section(ctrl, "Zone").IsEmpty);
        Assert.Equal("1 Sensor", Section(ctrl, "CPU").CountText);
        Assert.Equal("3 Sensoren", Section(ctrl, "Ungruppiert").CountText);

        Sensor(ctrl, Cpu).Visible = false;
        ctrl.ShowHiddenSensors = false;
        Assert.Equal("1 ausgeblendet", Section(ctrl, "CPU").CountText);
        Assert.False(Section(ctrl, "CPU").IsEmpty);
    }

    [Fact]
    public void Sections_WhileSearching_HideGroupsWithoutAMatch()
    {
        var (ctrl, _, _) = Editor();

        ctrl.SensorSearch = "Tctl";

        Assert.Equal(new[] { "CPU" }, SectionNames(ctrl)); // the empty "Zone" and the other groups go away
        ctrl.SensorSearch = "";
        Assert.Contains("Zone", SectionNames(ctrl));
    }

    [Fact]
    public void Ungrouped_ShowsOnlyWithSensors_OrWhileDragging()
    {
        var (ctrl, _, _) = Editor();
        ctrl.AutoGroupSensorsCommand.Execute(null);
        Assert.DoesNotContain("Ungruppiert", SectionNames(ctrl));

        IReadOnlyList<string> ids = ctrl.BeginSensorDrag(Sensor(ctrl, Cpu));
        Assert.Equal(new[] { Cpu }, ids);
        Assert.True(Sensor(ctrl, Cpu).IsDragged);
        Assert.Equal("Ungruppiert", SectionNames(ctrl).Last()); // a target while the drag runs

        ctrl.EndSensorDrag();
        Assert.DoesNotContain("Ungruppiert", SectionNames(ctrl));
        Assert.False(Sensor(ctrl, Cpu).IsDragged);
    }

    [Fact]
    public void Section_SurvivesARebuild_AsTheSameObject()
    {
        var (ctrl, _, _) = Editor();
        SensorGroupSection cpu = Section(ctrl, "CPU");

        ctrl.MoveSensors(new[] { Gpu }, "CPU");

        Assert.Same(cpu, Section(ctrl, "CPU"));
        Assert.Equal(new[] { Cpu, Gpu }, cpu.Rows.Select(r => r.Id));
    }

    // --- Auto-group --------------------------------------------------------------------------------

    [Fact]
    public void AutoGroup_OnlyTouchesUngrouped_JoinsAnExistingGroup_AndIsOneUndoStep()
    {
        var (ctrl, _, _) = Editor(Config() with { SensorGroups = new[] { "Zone", "mainboard" } });
        Assert.True(ctrl.AutoGroupSensorsCommand.CanExecute(null));

        ctrl.AutoGroupSensorsCommand.Execute(null);

        Assert.Equal("CPU", Sensor(ctrl, Cpu).Group);              // grouped already - stays
        Assert.Equal("Mainboard", Sensor(ctrl, Board).Group);      // stays
        Assert.Equal("GPU", Sensor(ctrl, Gpu).Group);
        Assert.Equal("Speicher", Sensor(ctrl, Nvme).Group);
        Assert.Equal("mainboard", Sensor(ctrl, Acpi).Group);       // joins the configured spelling
        Assert.Equal(new[] { "Zone", "mainboard", "CPU", "GPU", "Speicher" }, ctrl.SensorGroups);
        Assert.False(ctrl.AutoGroupSensorsCommand.CanExecute(null)); // nothing ungrouped any more
        Assert.Equal("Alle Sensoren sind bereits gruppiert.", ctrl.AutoGroupTooltip);
        Assert.Equal("3 Sensoren automatisch gruppiert.", ctrl.Status.Text);
        Assert.True(ctrl.HasUnsavedChanges);

        ctrl.UndoCommand.Execute(null);

        Assert.Equal("", Sensor(ctrl, Gpu).Group);
        Assert.Equal("", Sensor(ctrl, Acpi).Group);
        Assert.Equal(new[] { "Zone", "mainboard" }, ctrl.SensorGroups);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.False(ctrl.UndoCommand.CanExecute(null));
    }

    // --- New group ---------------------------------------------------------------------------------

    [Fact]
    public void NewGroup_IsUniquelyNamed_AppendedAndBrieflyHighlighted()
    {
        var (ctrl, clock, _) = Editor(Config() with { SensorGroups = new[] { "neue gruppe" } });

        ctrl.AddSensorGroupCommand.Execute(null);
        ctrl.AddSensorGroupCommand.Execute(null);

        Assert.Equal(new[] { "neue gruppe", "CPU", "Mainboard", "Neue Gruppe 2", "Neue Gruppe 3" }, ctrl.SensorGroups); // unlisted ones alphabetically
        SensorGroupSection added = Section(ctrl, "Neue Gruppe 3");
        Assert.True(added.IsEmpty);
        Assert.True(added.IsHighlighted);
        Assert.False(Section(ctrl, "Neue Gruppe 2").IsHighlighted); // only the latest one
        Assert.Equal("Gruppe „Neue Gruppe 3“ angelegt - Namen direkt im Gruppenkopf ändern.", ctrl.Status.Text);

        clock.AdvanceMs(3500);
        Assert.False(added.IsHighlighted);

        ctrl.UndoCommand.Execute(null);
        Assert.DoesNotContain("Neue Gruppe 3", ctrl.SensorGroups); // one step per group
        Assert.Contains("Neue Gruppe 2", ctrl.SensorGroups);
    }

    // --- Rename / merge ------------------------------------------------------------------------------

    [Fact]
    public void Rename_RewritesTheMembers_KeepsThePlace_AndTheSection()
    {
        var (ctrl, _, saved) = Editor();
        SensorGroupSection cpu = Section(ctrl, "CPU");

        cpu.DraftName = "  Prozessor ";
        Assert.False(cpu.IsDuplicateName);
        Assert.False(ctrl.HasUnsavedChanges); // typing only drafts
        cpu.CommitNameCommand.Execute(null);

        Assert.Equal("Prozessor", Sensor(ctrl, Cpu).Group);
        Assert.Equal(new[] { "Zone", "Mainboard", "Prozessor" }, ctrl.SensorGroups);
        Assert.Same(cpu, Section(ctrl, "Prozessor"));
        Assert.Equal("Prozessor", cpu.DraftName);
        ctrl.SaveCommand.Execute(null);
        Assert.Equal("Prozessor", saved.Single().Sensors.Single(s => s.SensorId == Cpu).Group);
    }

    [Fact]
    public void Rename_ToAnotherGroupsName_WarnsWhileTyping_MergesOnCommit_AndUndoSeparatesThemAgain()
    {
        var (ctrl, _, _) = Editor();
        SensorGroupSection cpu = Section(ctrl, "CPU");

        cpu.DraftName = "mainBOARD";
        Assert.True(cpu.IsDuplicateName);
        cpu.CommitNameCommand.Execute(null);

        Assert.Equal("Mainboard", Sensor(ctrl, Cpu).Group); // the existing spelling wins
        Assert.Equal(new[] { "Zone", "Mainboard" }, ctrl.SensorGroups);
        Assert.DoesNotContain("CPU", SectionNames(ctrl));
        Assert.Equal("Gruppe „CPU“ mit „Mainboard“ zusammengeführt.", ctrl.Status.Text);
        Assert.True(ctrl.Status.HasAction); // „Rückgängig"

        ctrl.Status.ActionCommand!.Execute(null);

        Assert.Equal("CPU", Sensor(ctrl, Cpu).Group);
        Assert.Equal(new[] { "Zone", "Mainboard", "CPU" }, SectionNames(ctrl).Take(3));
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CPU")]
    public void Rename_EmptyOrUnchanged_OnlyResetsTheField(string draft)
    {
        var (ctrl, _, _) = Editor();
        SensorGroupSection cpu = Section(ctrl, "CPU");

        cpu.DraftName = draft;
        cpu.CommitNameCommand.Execute(null);

        Assert.Equal("CPU", cpu.DraftName);
        Assert.Equal("CPU", Sensor(ctrl, Cpu).Group);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.False(ctrl.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void Rename_CasingOnly_IsARename()
    {
        var (ctrl, _, _) = Editor();
        SensorGroupSection cpu = Section(ctrl, "CPU");

        cpu.DraftName = "cpu";
        Assert.False(cpu.IsDuplicateName); // its own name in another spelling
        cpu.CommitNameCommand.Execute(null);

        Assert.Equal("cpu", Sensor(ctrl, Cpu).Group);
        Assert.Same(cpu, Section(ctrl, "cpu"));
    }

    // --- Reorder / delete ------------------------------------------------------------------------------

    [Fact]
    public void Reorder_ChangesTheList_AndTheCurveEditorsSourceSections()
    {
        var (ctrl, _, _) = Editor(Config() with { SensorGroups = new[] { "Zone", "Mainboard", "CPU" } });
        ctrl.SelectedCurve!.SensorChecks.Single(c => c.Sensor.Id == Board).Selected = true;
        Assert.Equal(new[] { "Mainboard", "CPU", "" }, ctrl.SourceSensorSections.Select(s => s.Key));

        SensorGroupSection cpu = Section(ctrl, "CPU");
        Assert.False(cpu.MoveDownCommand.CanExecute(null)); // last group
        cpu.MoveUpCommand.Execute(null);

        Assert.Equal(new[] { "Zone", "CPU", "Mainboard" }, ctrl.SensorGroups);
        Assert.Equal(new[] { "Zone", "CPU", "Mainboard", "Ungruppiert" }, SectionNames(ctrl));
        Assert.Equal(new[] { "CPU", "Mainboard", "" }, ctrl.SourceSensorSections.Select(s => s.Key));
        Assert.True(cpu.MoveDownCommand.CanExecute(null));
        Assert.False(Section(ctrl, "Zone").MoveUpCommand.CanExecute(null));
        Assert.False(Section(ctrl, "Ungruppiert").MoveUpCommand.CanExecute(null));

        ctrl.UndoCommand.Execute(null);
        Assert.Equal(new[] { "Zone", "Mainboard", "CPU" }, ctrl.SensorGroups);
        Assert.Equal(new[] { "Mainboard", "CPU", "" }, ctrl.SourceSensorSections.Select(s => s.Key));
    }

    [Fact]
    public void Delete_UngroupsTheMembers_WithoutAsking_AndTheToastUndoesIt()
    {
        var (ctrl, _, _) = Editor();

        Section(ctrl, "CPU").DeleteCommand.Execute(null);

        Assert.Equal("", Sensor(ctrl, Cpu).Group);
        Assert.Equal(new[] { "Zone", "Mainboard" }, ctrl.SensorGroups);
        Assert.Contains(ctrl.SensorGroupSections.Last().Rows, r => r.Id == Cpu);
        Assert.Equal("Gruppe „CPU“ gelöscht - 1 Sensor ist jetzt „Ungruppiert“.", ctrl.Status.Text);
        Assert.Equal("Rückgängig", ctrl.Status.ActionLabel);

        ctrl.Status.ActionCommand!.Execute(null);

        Assert.Equal("CPU", Sensor(ctrl, Cpu).Group);
        Assert.Contains("CPU", SectionNames(ctrl));
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Delete_AnEmptyGroup_SaysSo()
    {
        var (ctrl, _, saved) = Editor();

        Section(ctrl, "Zone").DeleteCommand.Execute(null);
        Assert.Equal("Gruppe „Zone“ gelöscht.", ctrl.Status.Text);
        ctrl.SaveCommand.Execute(null);

        Assert.DoesNotContain("Zone", saved.Single().SensorGroups);
    }

    // --- Moves: menus, bulk, drop ------------------------------------------------------------------------

    [Fact]
    public void GroupMenu_ListsTheGroups_MarksTheCurrent_AndAPickMovesTheSensor()
    {
        var (ctrl, _, _) = Editor();
        SensorOption tctl = Sensor(ctrl, Cpu);

        Assert.Equal(new[] { "Zone", "Mainboard", "CPU", "Ungruppiert", "-", "+ Neue Gruppe …" },
                     tctl.GroupMenu.Select(m => m.Header));
        Assert.Equal("CPU", Assert.Single(tctl.GroupMenu, m => m.IsCurrent).Header);
        MenuChoice separator = Assert.Single(tctl.GroupMenu, m => m.Header == "-");
        Assert.False(separator.IsEnabled);
        Assert.Equal("", separator.AutomationName); // nothing for a screen reader to say
        // The groups are values (radio items, the current one marked); "+ Neue Gruppe …" is an action and never marked.
        Assert.All(tctl.GroupMenu.Take(4), m => Assert.True(m.IsValue));
        Assert.False(tctl.GroupMenu.Last().IsValue);
        Assert.Equal("Ungruppiert", Assert.Single(Sensor(ctrl, Gpu).GroupMenu, m => m.IsCurrent).Header);

        tctl.GroupMenu.Single(m => m.Header == "Zone").Command!.Execute(null);

        Assert.Equal("Zone", tctl.Group);
        Assert.Equal("Zone", tctl.GroupLabel);
        Assert.Equal("Zone", Assert.Single(tctl.GroupMenu, m => m.IsCurrent).Header);
        Assert.Equal("Sensor nach „Zone“ verschoben.", ctrl.Status.Text);
        Assert.Contains("CPU", ctrl.SensorGroups); // the emptied group stays, as an empty one
        Assert.True(Section(ctrl, "CPU").IsEmpty);
    }

    [Fact]
    public void GroupMenu_NewGroupEntry_CreatesTheGroupWithTheSensorInIt_InOneStep()
    {
        var (ctrl, _, _) = Editor();

        Sensor(ctrl, Gpu).GroupMenu.Single(m => m.Header == "+ Neue Gruppe …").Command!.Execute(null);

        Assert.Equal("Neue Gruppe", Sensor(ctrl, Gpu).Group);
        Assert.Equal("Neue Gruppe", ctrl.SensorGroups.Last());
        ctrl.UndoCommand.Execute(null);
        Assert.Equal("", Sensor(ctrl, Gpu).Group);
        Assert.DoesNotContain("Neue Gruppe", ctrl.SensorGroups);
    }

    [Fact]
    public void BulkMove_MovesTheSelection_AsOneStep_AndClearsIt()
    {
        var (ctrl, _, _) = Editor();
        Sensor(ctrl, Gpu).IsSelected = true;
        Sensor(ctrl, Nvme).IsSelected = true;
        Assert.True(ctrl.HasSensorSelection);
        Assert.Equal("2 Sensoren ausgewählt", ctrl.SensorSelectionText);
        Assert.Equal(new[] { "Zone", "Mainboard", "CPU", "Ungruppiert", "-", "+ Neue Gruppe …" },
                     ctrl.BulkMoveMenu.Select(m => m.Header));
        Assert.DoesNotContain(ctrl.BulkMoveMenu, m => m.IsCurrent);
        Assert.DoesNotContain(ctrl.BulkMoveMenu, m => m.IsValue); // no current value: plain actions, no radio marks

        ctrl.BulkMoveMenu.Single(m => m.Header == "Mainboard").Command!.Execute(null);

        Assert.Equal("Mainboard", Sensor(ctrl, Gpu).Group);
        Assert.Equal("Mainboard", Sensor(ctrl, Nvme).Group);
        Assert.Equal("2 Sensoren nach „Mainboard“ verschoben.", ctrl.Status.Text);
        Assert.False(ctrl.HasSensorSelection);

        ctrl.UndoCommand.Execute(null);
        Assert.Equal("", Sensor(ctrl, Gpu).Group);
        Assert.Equal("", Sensor(ctrl, Nvme).Group);
    }

    [Fact]
    public void Selection_IsViewStateOnly_NeverDirty_NeverSaved_NeverUndone()
    {
        var (ctrl, _, saved) = Editor();
        SensorGroupSection ungrouped = Section(ctrl, "Ungruppiert");

        ungrouped.ToggleSelectAllCommand.Execute(null);
        Assert.True(ungrouped.AllSelected);
        Sensor(ctrl, Gpu).IsSelected = false;
        Assert.Null(ungrouped.AllSelected);       // some
        ungrouped.ToggleSelectAllCommand.Execute(null);
        Assert.True(ungrouped.AllSelected);       // a partial header selects all
        ungrouped.ToggleSelectAllCommand.Execute(null);
        Assert.False(ungrouped.AllSelected);      // a full one clears
        Sensor(ctrl, Cpu).IsSelected = true;

        Assert.False(ctrl.HasUnsavedChanges);
        Assert.False(ctrl.UndoCommand.CanExecute(null));
        ctrl.Fans[0].Name = "x";
        ctrl.SaveCommand.Execute(null);
        Assert.True(Sensor(ctrl, Cpu).IsSelected); // saving keeps it
        Assert.DoesNotContain("IsSelected", System.Text.Json.JsonSerializer.Serialize(saved.Single()));

        ctrl.ClearSensorSelectionCommand.Execute(null);
        Assert.False(ctrl.HasSensorSelection);
    }

    [Fact]
    public void Drop_DraggingASelectedRow_MovesTheWholeSelection_InOneStep()
    {
        var (ctrl, _, _) = Editor();
        Sensor(ctrl, Gpu).IsSelected = true;
        Sensor(ctrl, Acpi).IsSelected = true;

        IReadOnlyList<string> ids = ctrl.BeginSensorDrag(Sensor(ctrl, Acpi));
        Assert.Equal(new[] { Gpu, Acpi }, ids);
        Assert.True(Sensor(ctrl, Gpu).IsDragged);
        Assert.False(Sensor(ctrl, Nvme).IsDragged);

        ctrl.MoveSensors(ids, "CPU"); // what the view's drop handler calls
        ctrl.EndSensorDrag();

        Assert.Equal(new[] { Cpu, Gpu, Acpi }, Section(ctrl, "CPU").Rows.Select(r => r.Id));
        Assert.True(Sensor(ctrl, Gpu).IsSelected); // a drop keeps the selection
        ctrl.UndoCommand.Execute(null);
        Assert.Equal(new[] { Cpu }, Section(ctrl, "CPU").Rows.Select(r => r.Id));
    }

    [Fact]
    public void Drop_IntoTheGroupTheyAreIn_ChangesNothing()
    {
        var (ctrl, _, _) = Editor();

        ctrl.MoveSensors(new[] { Cpu }, "cpu");
        ctrl.MoveSensors(new[] { Gpu }, (string?)null);

        Assert.False(ctrl.HasUnsavedChanges);
        Assert.False(ctrl.UndoCommand.CanExecute(null));
        Assert.Equal("CPU", Sensor(ctrl, Cpu).Group);
    }

    // --- Fans: sections by position ---------------------------------------------------------------------

    private static FanAssignRow Fan(CurveEditorController ctrl, string id) => ctrl.Fans.Single(f => f.FanId == id);

    [Fact]
    public void FanSections_OnlyPositionsInUse_ByRank_NoPositionLast()
    {
        var (ctrl, _, _) = Editor();

        Assert.Equal(new[] { FanLocation.CpuCooler, FanLocation.CaseFrontIntake, FanLocation.CaseRearExhaust, FanLocation.Unspecified },
                     ctrl.FanLocationSections.Select(s => s.Location));
        Assert.Equal(new[] { "CPU-Kühler", "Front · Einlass", "Hinten · Auslass", "Ohne Position" },
                     ctrl.FanLocationSections.Select(s => s.Name));
        FanLocationSection front = ctrl.FanLocationSections[1];
        Assert.True(front.IsIntake);
        Assert.True(ctrl.FanLocationSections[2].IsExhaust);
        Assert.True(ctrl.FanLocationSections[0].IsInternal);
        Assert.True(ctrl.FanLocationSections[3].IsUnspecified);
        Assert.False(ctrl.FanLocationSections[3].IsInternal);
        Assert.Equal("1 Lüfter", front.CountText);
    }

    [Fact]
    public void PositionMenu_GroupsWithHeadings_MarksTheCurrent_AndAPickSetsTheLocation_InOneStep()
    {
        var (ctrl, _, _) = Editor();
        FanAssignRow front = Fan(ctrl, "nct6798/pwm2");

        Assert.Equal(new[] { "Kühler", "Gehäuse · Einlass", "Gehäuse · Auslass", "Sonstiges" },
                     front.PositionMenu.Where(m => !m.IsEnabled && m.Header != "-").Select(m => m.Header));
        Assert.Equal("Vorn", Assert.Single(front.PositionMenu, m => m.IsCurrent).Header);
        Assert.Equal("Im Gehäuse-Diagramm wählen …", front.PositionMenu.Last().Header);
        Assert.Equal("-", front.PositionMenu[^2].Header);
        Assert.False(front.PositionMenu.Last().IsValue); // an action: picking it must not leave a radio mark behind
        // The short names repeat under both directions - a screen reader hears the full one.
        Assert.Equal(new[] { "Gehäuse vorn · Einlass", "Gehäuse vorn · Auslass" },
                     front.PositionMenu.Where(m => m.Header == "Vorn").Select(m => m.AutomationName));

        // The second "Oben" is the exhaust one (intake heading first).
        front.PositionMenu.Where(m => m.Header == "Oben").Last().Command!.Execute(null);

        Assert.Equal(FanLocation.CaseTopExhaust, front.Location.Value);
        Assert.Equal("Gehäuse oben · Auslass", front.LocationLabel);
        Assert.Equal("Position: Gehäuse oben · Auslass.", ctrl.Status.Text);
        Assert.Contains(ctrl.FanLocationSections, s => s.Location == FanLocation.CaseTopExhaust && s.Rows.Contains(front));
        Assert.DoesNotContain(ctrl.FanLocationSections, s => s.Location == FanLocation.CaseFrontIntake); // emptied
        Assert.Equal("Oben", Assert.Single(front.PositionMenu, m => m.IsCurrent).Header);

        ctrl.UndoCommand.Execute(null);
        Assert.Equal(FanLocation.CaseFrontIntake, Fan(ctrl, "nct6798/pwm2").Location.Value);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void PositionMenu_DiagramEntry_AsksTheViewForTheDialog()
    {
        var (ctrl, _, _) = Editor();
        FanAssignRow rear = Fan(ctrl, "nct6798/pwm3");
        FanAssignRow? asked = null;
        ctrl.LocationDialogRequested += (_, row) => asked = row;

        rear.PositionMenu.Last().Command!.Execute(null);

        Assert.Same(rear, asked);
        Assert.False(ctrl.HasUnsavedChanges); // the dialog's answer is the edit, not the request
    }

    [Fact]
    public void Unplaced_Fan_ShowsNotSet()
    {
        var (ctrl, _, _) = Editor();
        FanAssignRow pump = Fan(ctrl, "nct6798/fan4");

        Assert.Equal("nicht gesetzt", pump.LocationLabel);
        Assert.Equal("Ohne Position", Assert.Single(pump.PositionMenu, m => m.IsCurrent).Header);
    }

    [Fact]
    public void BulkPosition_SetsTheSelection_AsOneStep_AndClearsIt()
    {
        var (ctrl, _, _) = Editor();
        Fan(ctrl, "nct6798/pwm2").IsSelected = true;
        Fan(ctrl, "nct6798/fan4").IsSelected = true;
        Assert.Equal("2 Lüfter ausgewählt", ctrl.FanSelectionText);
        Assert.True(ctrl.FanLocationSections.Single(s => s.Location == FanLocation.Unspecified).AllSelected); // its only fan
        Assert.DoesNotContain(ctrl.BulkPositionMenu, m => m.IsCurrent);
        Assert.DoesNotContain(ctrl.BulkPositionMenu, m => m.Header == "-"); // no diagram for several fans
        Assert.DoesNotContain(ctrl.BulkPositionMenu, m => m.IsValue);      // no current value: plain actions

        ctrl.BulkPositionMenu.Single(m => m.Header == "Netzteil").Command!.Execute(null);

        Assert.Equal(FanLocation.Psu, Fan(ctrl, "nct6798/pwm2").Location.Value);
        Assert.Equal(FanLocation.Psu, Fan(ctrl, "nct6798/fan4").Location.Value);
        Assert.Equal("2 Lüfter → Netzteil.", ctrl.Status.Text);
        Assert.False(ctrl.HasFanSelection);
        ctrl.UndoCommand.Execute(null);
        Assert.Equal(FanLocation.CaseFrontIntake, Fan(ctrl, "nct6798/pwm2").Location.Value);
        Assert.Equal(FanLocation.Unspecified, Fan(ctrl, "nct6798/fan4").Location.Value);
    }

    [Fact]
    public void BulkVisibility_HidesWhileAllAreVisible_OtherwiseShows_InOneStep()
    {
        var (ctrl, _, _) = Editor();
        FanAssignRow front = Fan(ctrl, "nct6798/pwm2");
        FanAssignRow rear = Fan(ctrl, "nct6798/pwm3");
        front.IsSelected = true;
        rear.IsSelected = true;
        Assert.Equal("Ausblenden", ctrl.FanBulkVisibilityText);

        ctrl.ToggleSelectedFansVisibleCommand.Execute(null);
        Assert.False(front.Visible);
        Assert.False(rear.Visible);
        Assert.Equal("Einblenden", ctrl.FanBulkVisibilityText);

        ctrl.UndoCommand.Execute(null);
        Assert.True(front.Visible);
        Assert.True(rear.Visible);
        Assert.Equal("Ausblenden", ctrl.FanBulkVisibilityText);
    }

    [Fact]
    public void FanSectionHeader_SelectsTheBlock_WithoutAnEdit()
    {
        var (ctrl, _, _) = Editor();
        FanLocationSection front = ctrl.FanLocationSections.Single(s => s.Location == FanLocation.CaseFrontIntake);

        front.ToggleSelectAllCommand.Execute(null);

        Assert.True(front.AllSelected);
        Assert.True(Fan(ctrl, "nct6798/pwm2").IsSelected);
        Assert.True(ctrl.HasFanSelection);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void FanDrop_SetsTheSectionsPosition_ForTheDraggedSelection()
    {
        var (ctrl, _, _) = Editor();
        Fan(ctrl, "nct6798/pwm2").IsSelected = true;
        Fan(ctrl, "nct6798/pwm3").IsSelected = true;

        IReadOnlyList<string> ids = ctrl.BeginFanDrag(Fan(ctrl, "nct6798/pwm3"));
        ctrl.SetFanLocations(ids, FanLocation.CpuCooler); // what the view's drop handler calls
        ctrl.EndFanDrag();

        Assert.Equal(new[] { "nct6798/pwm2", "nct6798/pwm3" }, ids);
        Assert.Equal(3, ctrl.FanLocationSections.Single(s => s.Location == FanLocation.CpuCooler).Rows.Count);
        Assert.False(Fan(ctrl, "nct6798/pwm3").IsDragged);
        ctrl.UndoCommand.Execute(null);
        Assert.Single(ctrl.FanLocationSections.Single(s => s.Location == FanLocation.CpuCooler).Rows);
    }

    // --- Device actions: calibrate, identify, couple ------------------------------------------

    [Fact]
    public void DeviceActions_WaitForTheServiceAndForRunningOnes_AndTheTooltipSaysWhy()
    {
        var (ctrl, _, _) = Editor();
        FanAssignRow front = Fan(ctrl, "nct6798/pwm2");
        Assert.True(front.CalibrateCommand.CanExecute(null));
        Assert.True(front.IdentifyCommand.CanExecute(null));
        Assert.True(front.CoupleSensorCommand.CanExecute(null));
        Assert.Equal("Dreht den Lüfter kurz auf 100 % hoch (andere werden gedrosselt), um ihn physisch zu identifizieren.",
                     front.IdentifyTooltip);

        ctrl.IsConnected = false;
        Assert.False(front.CalibrateCommand.CanExecute(null));
        Assert.False(front.IdentifyCommand.CanExecute(null));
        Assert.False(front.CoupleSensorCommand.CanExecute(null));
        Assert.Equal("Gesperrt - der Hintergrunddienst ist nicht erreichbar.", front.IdentifyTooltip);
        Assert.Equal(front.IdentifyTooltip, front.CalibrateTooltip);
        Assert.Equal(front.IdentifyTooltip, front.CoupleSensorTooltip);
        ctrl.IsConnected = true;
        Assert.True(front.IdentifyCommand.CanExecute(null));

        // Another fan calibrates: the daemon would refuse a second action without a word.
        ctrl.UpdateLive(Snapshot(Config(), calibration: new CalibrationStatus(
            "nct6798/pwm1", CalibrationPhase.Measuring, 120, 900, Running: true, Done: false, StartPwm: null, FailReason: null)));
        Assert.False(front.CalibrateCommand.CanExecute(null));
        Assert.False(front.IdentifyCommand.CanExecute(null));
        Assert.False(front.CoupleSensorCommand.CanExecute(null));
        Assert.StartsWith("Gesperrt - gerade läuft", front.CalibrateTooltip);

        ctrl.UpdateLive(Snapshot(Config()));
        Assert.True(front.CalibrateCommand.CanExecute(null));
        Assert.True(front.CoupleSensorCommand.CanExecute(null));

        ctrl.UpdateLive(Snapshot(Config(), identify: new IdentifyStatus("nct6798/pwm3", Running: true, FailReason: null)));
        Assert.False(front.IdentifyCommand.CanExecute(null));
    }

    /// <summary>
    /// The GUI re-applies the last snapshot every second, also while the daemon ticks slower: only a new snapshot may
    /// count against a fan test the daemon has not confirmed yet - otherwise the switch drops back to "curve" while the
    /// override the daemon holds stays, and leaving the settings no longer ends it.
    /// </summary>
    [Fact]
    public void FanTest_HoldsWhileTheDaemonHasNotTicked_AndFollowsNewSnapshotsWithoutTheOverride()
    {
        var (ctrl, _, _) = Editor();
        FanAssignRow front = Fan(ctrl, "nct6798/pwm2");
        MonitorSnapshot last = Snapshot(Config());
        ctrl.UpdateLive(last);
        front.Manual.IsActive = true;

        for (int poll = 0; poll < 6; poll++)
            ctrl.UpdateLive(last);
        Assert.True(front.Manual.IsActive);

        for (int tick = 0; tick < 4; tick++)
            ctrl.UpdateLive(Snapshot(Config())); // the daemon ticks and still holds no override: refused
        Assert.False(front.Manual.IsActive);
    }

    /// <summary>A dry-run daemon refuses every device action (it writes no fan) - and the manual test with them.</summary>
    [Fact]
    public void DeviceActions_AreLockedInADryRun_AndTheTooltipSaysWhy()
    {
        var (ctrl, _, _) = Editor();
        FanAssignRow front = Fan(ctrl, "nct6798/pwm2");

        ctrl.UpdateLive(Snapshot(Config()) with { DryRun = true });

        Assert.False(front.CalibrateCommand.CanExecute(null));
        Assert.False(front.IdentifyCommand.CanExecute(null));
        Assert.False(front.CoupleSensorCommand.CanExecute(null));
        Assert.True(front.Manual.EngageBlocked);
        Assert.StartsWith("Im Dry-Run nicht möglich", front.CalibrateTooltip);

        ctrl.UpdateLive(Snapshot(Config()));
        Assert.True(front.CalibrateCommand.CanExecute(null));
    }

    // --- Review follow-ups -----------------------------------------------------------------------

    [Fact]
    public void Selection_OnlyCoversWhatTheListShows_SearchUnticksHiddenRows()
    {
        var (ctrl, _, _) = Editor();
        Sensor(ctrl, Cpu).IsSelected = true;
        Sensor(ctrl, Gpu).IsSelected = true;

        ctrl.SensorSearch = "Tctl";

        Assert.True(Sensor(ctrl, Cpu).IsSelected);
        Assert.False(Sensor(ctrl, Gpu).IsSelected); // out of view - a bulk move or drag must not take it along
        Assert.Equal("1 Sensor ausgewählt", ctrl.SensorSelectionText);
        Assert.Equal(new[] { Cpu }, ctrl.BeginSensorDrag(Sensor(ctrl, Cpu)));
        ctrl.EndSensorDrag();
    }

    [Fact]
    public void BulkHide_WithHideHereToo_LeavesNoInvisibleSelection()
    {
        var (ctrl, _, _) = Editor();
        ctrl.ShowHiddenFans = false;
        Fan(ctrl, "nct6798/pwm2").IsSelected = true;
        Fan(ctrl, "nct6798/pwm3").IsSelected = true;

        ctrl.ToggleSelectedFansVisibleCommand.Execute(null);

        Assert.False(Fan(ctrl, "nct6798/pwm2").Visible);
        Assert.DoesNotContain(ctrl.Fans, f => f.IsSelected);
        Assert.False(ctrl.HasFanSelection);
    }

    [Fact]
    public void SectionHeader_OfAnEmptyGroup_HasNothingToSelect()
    {
        var (ctrl, _, _) = Editor();
        SensorGroupSection zone = Section(ctrl, "Zone");

        Assert.False(zone.HasRows);
        Assert.False(zone.ToggleSelectAllCommand.CanExecute(null));
        Assert.True(Section(ctrl, "CPU").ToggleSelectAllCommand.CanExecute(null));
    }

    [Fact]
    public void NewGroup_WhileSearching_EndsTheSearch_SoTheGroupShows()
    {
        var (ctrl, _, _) = Editor();
        ctrl.SensorSearch = "Tctl";

        ctrl.AddSensorGroupCommand.Execute(null);

        Assert.Equal("", ctrl.SensorSearch);
        Assert.True(Section(ctrl, "Neue Gruppe").IsHighlighted);
    }

    [Fact]
    public void Drop_OnASection_MovesIntoItsGroup_OrOutOfAllGroupsOnUngrouped()
    {
        var (ctrl, _, _) = Editor();

        ctrl.MoveSensors(new[] { Gpu }, Section(ctrl, "Zone"));
        Assert.Equal("Zone", Sensor(ctrl, Gpu).Group);

        ctrl.MoveSensors(new[] { Cpu }, Section(ctrl, "Ungruppiert"));
        Assert.Equal("", Sensor(ctrl, Cpu).Group);

        ctrl.SetFanLocations(new[] { "nct6798/fan4" }, ctrl.FanLocationSections.Single(s => s.Location == FanLocation.CpuCooler));
        Assert.Equal(FanLocation.CpuCooler, Fan(ctrl, "nct6798/fan4").Location.Value);
    }

    [Fact]
    public void AccessibleNames_SayWhatTheChecksAndChipsAreFor()
    {
        var (ctrl, _, _) = Editor();

        Assert.Equal("Tctl auswählen", Sensor(ctrl, Cpu).SelectName);
        Assert.Equal("Gruppe: CPU", Sensor(ctrl, Cpu).GroupChipName);
        Assert.Equal("Gruppe: Ungruppiert", Sensor(ctrl, Gpu).GroupChipName);
        Assert.Equal("Gruppe CPU: alle auswählen", Section(ctrl, "CPU").SelectAllName);
        Assert.Equal("Front auswählen", Fan(ctrl, "nct6798/pwm2").SelectName);
        Assert.Equal("Position: Gehäuse vorn · Einlass", Fan(ctrl, "nct6798/pwm2").PositionChipName);
        Assert.Equal("Front · Einlass: alle Lüfter auswählen",
                     ctrl.FanLocationSections.Single(s => s.Location == FanLocation.CaseFrontIntake).SelectAllName);

        Sensor(ctrl, Cpu).GroupMenu.Single(m => m.Header == "Zone").Command!.Execute(null);
        Assert.Equal("Gruppe: Zone", Sensor(ctrl, Cpu).GroupChipName);
    }

    [Fact]
    public void GroupLimit_TurnsOffNewGroup_AndItsMenuEntry()
    {
        string[] full = Enumerable.Range(1, ConfigLimits.MaxSensorGroups - 2).Select(i => $"G{i}").ToArray();
        var (ctrl, _, _) = Editor(Config() with { SensorGroups = full }); // + "CPU" and "Mainboard" used = the limit

        Assert.False(ctrl.CanAddGroup);
        Assert.False(ctrl.AddSensorGroupCommand.CanExecute(null));
        Assert.DoesNotContain(Sensor(ctrl, Gpu).GroupMenu, m => m.Header == "+ Neue Gruppe …");
        Assert.DoesNotContain(ctrl.BulkMoveMenu, m => m.Header == "-");

        Section(ctrl, "G1").DeleteCommand.Execute(null);
        Assert.True(ctrl.CanAddGroup);
        Assert.Contains(Sensor(ctrl, Gpu).GroupMenu, m => m.Header == "+ Neue Gruppe …");
    }

    [Fact]
    public void GroupOperations_Redo_AndDiscardAll()
    {
        var (ctrl, _, _) = Editor();
        Section(ctrl, "CPU").MoveUpCommand.Execute(null);
        ctrl.MoveSensors(new[] { Gpu }, "Zone");
        Section(ctrl, "Mainboard").DeleteCommand.Execute(null);
        Assert.Equal(new[] { "Zone", "CPU" }, ctrl.SensorGroups);

        ctrl.UndoCommand.Execute(null);
        ctrl.UndoCommand.Execute(null);
        Assert.Equal("", Sensor(ctrl, Gpu).Group);
        Assert.Equal("Mainboard", Sensor(ctrl, Board).Group);

        ctrl.RedoCommand.Execute(null);
        ctrl.RedoCommand.Execute(null);
        Assert.Equal("Zone", Sensor(ctrl, Gpu).Group);
        Assert.Equal("", Sensor(ctrl, Board).Group);
        Assert.DoesNotContain("Mainboard", ctrl.SensorGroups);

        ctrl.RevertCommand.Execute(null); // „Alles verwerfen"
        Assert.Equal(new[] { "Zone", "Mainboard" }, ctrl.SensorGroups);
        Assert.Equal(new[] { "Zone", "Mainboard", "CPU", "Ungruppiert" }, SectionNames(ctrl));
        Assert.Equal("Mainboard", Sensor(ctrl, Board).Group);
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.UndoCommand.Execute(null); // discarding is a step of its own
        Assert.Equal("Zone", Sensor(ctrl, Gpu).Group);
    }
}
