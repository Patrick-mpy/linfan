// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// The curves tab's controller side: the side menu (one list, one selection, adjusted in place), activating the
/// profile shown, the source sections per sensor group, adding curves from templates and the fan blocks.
/// </summary>
public sealed class CurveTabControllerTests
{
    private const string CpuFan = "hwmon7/pwm1";
    private const string FrontFan = "hwmon7/pwm2";
    private const string PumpFan = "hwmon7/pwm3";

    private static readonly CurveConfig Quiet = new()
    {
        Id = "quiet",
        Name = "Quiet",
        SourceSensorIds = new[] { "hwmon6/temp1" },
        Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
    };

    private static readonly CurveConfig Case = new()
    {
        Id = "case",
        Name = "Case",
        SourceSensorIds = new[] { "hwmon8/temp1" },
        Points = new[] { new CurvePoint(30, 30), new CurvePoint(70, 100) },
    };

    private static readonly CurveConfig Loud = new()
    {
        Id = "loud",
        Name = "Loud",
        SourceSensorIds = new[] { "hwmon6/temp1" },
        Points = new[] { new CurvePoint(30, 60), new CurvePoint(80, 100) },
    };

    /// <summary>
    /// p-silent runs with two curves (CPU fan on Quiet, front fan on Case); p-loud has one. Sensors in the groups
    /// "Mainboard" (configured first) and "CPU", one ungrouped; a read-only pump. Every profile lists every fan, as the
    /// editor writes them.
    /// </summary>
    private static MonitorSnapshot Snapshot() => new(
        "test",
        new[]
        {
            new SensorReading("hwmon6/temp1", "Tctl", SensorKind.Temperature, "°C", 41),
            new SensorReading("hwmon6/temp2", "Tccd1", SensorKind.Temperature, "°C", 39),
            new SensorReading("hwmon8/temp1", "Board", SensorKind.Temperature, "°C", 33),
            new SensorReading("hwmon9/temp1", "NVMe", SensorKind.Temperature, "°C", 36),
        },
        new[]
        {
            new FanReading(CpuFan, "CPU", 1500, 128, true),
            new FanReading(FrontFan, "Front", 900, 100, true),
            new FanReading(PumpFan, "Pump", 2400, 255, false),
        },
        new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl", Group = "CPU" },
                new SensorConfig { SensorId = "hwmon6/temp2", Name = "Tccd1", Group = "CPU" },
                new SensorConfig { SensorId = "hwmon8/temp1", Name = "Board", Group = "Mainboard" },
            },
            SensorGroups = new[] { "Mainboard", "CPU" },
            Curves = new[] { Quiet, Case },
            Fans = new[]
            {
                new FanConfig { FanId = CpuFan, Name = "CPU", MinPwm = 64, AssignedCurveId = "quiet" },
                new FanConfig { FanId = FrontFan, Name = "Front", AssignedCurveId = "case" },
                new FanConfig { FanId = PumpFan, Name = "Pump" },
            },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p-silent", Name = "Silent", Curves = new[] { Quiet, Case },
                    Assignments = new[]
                    {
                        new ProfileAssignment(CpuFan, "quiet"), new ProfileAssignment(FrontFan, "case"),
                        new ProfileAssignment(PumpFan, null),
                    },
                },
                new Profile
                {
                    Id = "p-loud", Name = "Loud", Curves = new[] { Loud },
                    Assignments = new[]
                    {
                        new ProfileAssignment(CpuFan, "loud"), new ProfileAssignment(FrontFan, null),
                        new ProfileAssignment(PumpFan, null),
                    },
                },
            },
            ActiveProfileId = "p-silent",
        });

    private static CurveEditorController Editor(Func<string, Task>? activate = null)
    {
        var ctrl = new CurveEditorController((_, _) => Task.FromResult(true), activateProfile: activate);
        ctrl.Initialize(Snapshot());
        ctrl.IsConnected = true;
        return ctrl;
    }

    private static string[] NavLabels(CurveEditorController ctrl) => ctrl.NavItems.Select(i => i switch
    {
        ProfileNavItem p => (p.IsExpanded ? "v " : "> ") + p.Profile.Name,
        CurveNavItem c => "  " + c.Curve.Name,
        _ => "  +",
    }).ToArray();

    private static ProfileNavItem ProfileItem(CurveEditorController ctrl, string id) =>
        ctrl.NavItems.OfType<ProfileNavItem>().Single(i => i.Profile.Id == id);

    // --- Side menu --------------------------------------------------------------------------------

    [Fact]
    public void NavItems_ExpandTheSelectedProfile_WithItsCurvesAndTheAddRow()
    {
        CurveEditorController ctrl = Editor();

        Assert.Equal(new[] { "v Silent", "  Quiet", "  Case", "  +", "> Loud" }, NavLabels(ctrl));
        CurveNavItem selected = Assert.IsType<CurveNavItem>(ctrl.SelectedNavItem); // the curve pane is open
        Assert.Same(ctrl.SelectedCurve, selected.Curve);
    }

    [Fact]
    public void SelectingAProfile_OpensIt_AndAdjustsTheListInPlace()
    {
        CurveEditorController ctrl = Editor();
        var actions = new List<NotifyCollectionChangedAction>();
        ctrl.NavItems.CollectionChanged += (_, e) => actions.Add(e.Action);
        CurveTabNavItem silentItem = ProfileItem(ctrl, "p-silent");
        ProfileNavItem loudItem = ProfileItem(ctrl, "p-loud");

        ctrl.SelectedNavItem = loudItem;

        Assert.Equal("p-loud", ctrl.SelectedProfile!.Id);
        Assert.Equal(CurveTabPane.Profile, ctrl.Pane);
        Assert.Same(loudItem, ctrl.SelectedNavItem);
        Assert.Equal(new[] { "> Silent", "v Loud", "  Loud", "  +" }, NavLabels(ctrl));
        Assert.Same(silentItem, ctrl.NavItems[0]); // the rows survive, only curve rows come and go
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.False(ctrl.HasUnsavedChanges); // opening is no edit
    }

    [Fact]
    public void SelectingACurve_OpensTheCurveEditor()
    {
        CurveEditorController ctrl = Editor();
        ctrl.Pane = CurveTabPane.Profile;
        Assert.IsType<ProfileNavItem>(ctrl.SelectedNavItem);

        CurveNavItem caseItem = ctrl.NavItems.OfType<CurveNavItem>().Single(i => i.Curve.Id == "case");
        ctrl.SelectedNavItem = caseItem;

        Assert.Equal(CurveTabPane.Curve, ctrl.Pane);
        Assert.Equal("case", ctrl.SelectedCurve!.Id);
        Assert.Same(caseItem, ctrl.SelectedNavItem);
    }

    [Fact]
    public void SelectedNavItem_IgnoresNull_AndTheAddRow()
    {
        CurveEditorController ctrl = Editor();
        CurveTabNavItem before = ctrl.SelectedNavItem!;

        ctrl.SelectedNavItem = null;
        ctrl.SelectedNavItem = ctrl.NavItems.OfType<AddCurveNavItem>().Single();

        Assert.Same(before, ctrl.SelectedNavItem);
        Assert.Equal(CurveTabPane.Curve, ctrl.Pane);
    }

    [Fact]
    public void SelectedNavItem_IsNotified_OncePerSelection()
    {
        CurveEditorController ctrl = Editor();
        int notified = 0;
        ctrl.PropertyChanged += (_, e) => notified += e.PropertyName == nameof(CurveEditorController.SelectedNavItem) ? 1 : 0;

        ctrl.SelectedNavItem = ProfileItem(ctrl, "p-loud"); // loads the profile and selects its first curve on the way

        Assert.Equal(1, notified);
    }

    [Fact]
    public void NavItems_FollowAddedAndDeletedCurves()
    {
        CurveEditorController ctrl = Editor();

        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Silent);
        Assert.Equal(new[] { "v Silent", "  Quiet", "  Case", "  Neue Kurve · Leise", "  +", "> Loud" }, NavLabels(ctrl));
        Assert.Same(ctrl.SelectedCurve, Assert.IsType<CurveNavItem>(ctrl.SelectedNavItem).Curve);

        ctrl.DeleteCurveCommand.Execute(null);
        Assert.Equal(new[] { "v Silent", "  Quiet", "  Case", "  +", "> Loud" }, NavLabels(ctrl));
        Assert.Same(ctrl.SelectedCurve, Assert.IsType<CurveNavItem>(ctrl.SelectedNavItem).Curve);
    }

    [Fact]
    public void UndoingADelete_OpensTheRestoredCurveAgain()
    {
        CurveEditorController ctrl = Editor();
        ctrl.SelectedNavItem = ctrl.NavItems.OfType<CurveNavItem>().Single(i => i.Curve.Id == "case");
        ctrl.DeleteCurveCommand.Execute(null);
        Assert.Equal("quiet", ctrl.SelectedCurve!.Id);

        ctrl.UndoCommand.Execute(null);

        CurveNavItem restored = Assert.IsType<CurveNavItem>(ctrl.SelectedNavItem);
        Assert.Equal("case", restored.Curve.Id);
        Assert.Same(ctrl.SelectedCurve, restored.Curve);
        Assert.Contains(restored, ctrl.NavItems);
        Assert.Equal(CurveTabPane.Curve, ctrl.Pane);
    }

    [Fact]
    public void NavItems_FollowUndo()
    {
        CurveEditorController ctrl = Editor();
        ctrl.AddProfileCommand.Execute(null);
        Assert.Equal(3, ctrl.NavItems.OfType<ProfileNavItem>().Count());

        ctrl.UndoCommand.Execute(null);

        Assert.Equal(new[] { "v Silent", "  Quiet", "  Case", "  +", "> Loud" }, NavLabels(ctrl));
        Assert.NotNull(ctrl.SelectedNavItem);
    }

    // --- Activating the profile shown --------------------------------------------------------------

    [Fact]
    public void Activate_IsOffForTheRunningProfile_AndAsksWhileUnsaved()
    {
        var activated = new List<string>();
        CurveEditorController ctrl = Editor(id => { activated.Add(id); return Task.CompletedTask; });
        var asked = new List<ProfileSwitchRequest>();
        ctrl.ProfileSwitchConfirmationRequested += (_, r) => asked.Add(r);
        Assert.False(ctrl.ActivateSelectedProfileCommand.CanExecute(null)); // p-silent runs already
        Assert.Equal("", ctrl.EditingInactiveProfileText);

        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p-loud");
        Assert.True(ctrl.ActivateSelectedProfileCommand.CanExecute(null));
        Assert.Equal("Du bearbeitest „Loud“ - aktiv ist „Silent“", ctrl.EditingInactiveProfileText);
        Assert.Equal("Übergibt die Lüfter an dieses Profil.", ctrl.ActivateProfileHint);

        var notified = new List<string?>();
        ctrl.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        ctrl.Curves.Single().Name = "Entwurf";
        Assert.True(ctrl.ActivateSelectedProfileCommand.CanExecute(null)); // unsaved changes do not lock it …
        Assert.Contains(nameof(CurveEditorController.ActivateProfileHint), notified);
        Assert.EndsWith("ob sie vorher übernommen werden.", ctrl.ActivateProfileHint); // … it asks first
        ctrl.ActivateSelectedProfileCommand.Execute(null);
        Assert.Equal("p-loud", Assert.Single(asked).ProfileId);
        Assert.Empty(activated);

        ctrl.Curves.Single().Name = "Loud";
        ctrl.UpdateLive(Snapshot()); // the tick settles the edit back to the baseline
        Assert.True(ctrl.ActivateSelectedProfileCommand.CanExecute(null));

        ctrl.ActivateSelectedProfileCommand.Execute(null);
        Assert.Equal("p-loud", ctrl.ActiveProfile!.Id);
        Assert.Equal(new[] { "p-loud" }, activated);
        Assert.False(ctrl.HasUnsavedChanges); // the daemon persists the switch itself
        Assert.Equal("", ctrl.EditingInactiveProfileText);
    }

    /// <summary>
    /// Offline the activation would go nowhere, yet the editor would move the badge and rebase onto a profile the
    /// daemon never switched to - the banner button and the profile pane's switch share one rule.
    /// </summary>
    [Fact]
    public void Activate_IsOff_WhileTheServiceIsUnreachable()
    {
        var activated = new List<string>();
        CurveEditorController ctrl = Editor(id => { activated.Add(id); return Task.CompletedTask; });
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p-loud");
        int raised = 0;
        ctrl.ActivateSelectedProfileCommand.CanExecuteChanged += (_, _) => raised++;
        var notified = new List<string?>();
        ctrl.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        ctrl.IsConnected = false;

        Assert.True(raised > 0);
        Assert.Contains(nameof(CurveEditorController.ActivateProfileHint), notified);
        Assert.Contains(nameof(CurveEditorController.CanActivateSelectedProfile), notified);
        Assert.False(ctrl.ActivateSelectedProfileCommand.CanExecute(null));
        Assert.False(ctrl.CanActivateSelectedProfile);
        Assert.StartsWith("Nicht verbunden", ctrl.ActivateProfileHint);

        ctrl.ActivateSelectedProfileCommand.Execute(null);
        ctrl.SelectedProfileActive = true; // the profile pane's switch
        Assert.Empty(activated);
        Assert.Equal("p-silent", ctrl.ActiveProfile!.Id);
        Assert.True(ctrl.Profiles.Single(p => p.Id == "p-silent").IsActive); // the badge stays where the daemon is

        ctrl.IsConnected = true;
        Assert.True(ctrl.ActivateSelectedProfileCommand.CanExecute(null));
    }

    [Fact]
    public void EditingInactiveProfileText_FollowsARename()
    {
        CurveEditorController ctrl = Editor();
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p-loud");
        var notified = new List<string?>();
        ctrl.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        ctrl.SelectedProfile.Name = "Laut";

        Assert.Contains(nameof(CurveEditorController.EditingInactiveProfileText), notified);
        Assert.Equal("Du bearbeitest „Laut“ - aktiv ist „Silent“", ctrl.EditingInactiveProfileText);
    }

    // --- Source sections ---------------------------------------------------------------------------

    [Fact]
    public void SourceSections_FollowTheConfiguredGroupOrder_UngroupedLast()
    {
        CurveEditorController ctrl = Editor();

        Assert.Equal(new[] { "Mainboard", "CPU", "Ungruppiert" }, ctrl.SourceSensorSections.Select(s => s.Name));
        Assert.Equal(new[] { "Tccd1", "Tctl" }, ctrl.SourceSensorSections[1].Sensors.Select(s => s.Sensor.Name));
    }

    [Fact]
    public void SourceSections_OpenWhereASourceIs_AndRememberAToggle()
    {
        CurveEditorController ctrl = Editor(); // Quiet: source Tctl (CPU)
        SensorCheckGroup cpu = ctrl.SourceSensorSections.Single(s => s.Key == "CPU");
        Assert.True(cpu.IsOpen);
        Assert.False(ctrl.SourceSensorSections.Single(s => s.Key == "Mainboard").IsOpen);

        cpu.IsOpen = false;
        ctrl.SelectedCurve = ctrl.Curves.Single(c => c.Id == "case"); // Case: source Board (Mainboard)

        Assert.False(ctrl.SourceSensorSections.Single(s => s.Key == "CPU").IsOpen); // remembered per group, across curves
        Assert.True(ctrl.SourceSensorSections.Single(s => s.Key == "Mainboard").IsOpen);
    }

    [Fact]
    public void SourceSection_Header_IsTriState_AndSelectsOrClearsTheGroup()
    {
        CurveEditorController ctrl = Editor();
        CurveEditRow curve = ctrl.SelectedCurve!;
        SensorCheckGroup cpu = ctrl.SourceSensorSections.Single(s => s.Key == "CPU");
        Assert.Null(cpu.AllState); // 1 of 2
        Assert.Equal("1 von 2 gewählt", cpu.Summary);

        cpu.ToggleAllCommand.Execute(null); // partial → all
        Assert.True(cpu.AllState);
        Assert.Equal(2, curve.Sources.Count);
        Assert.True(curve.HasMultipleSources);

        cpu.IsOpen = false;
        cpu.ToggleAllCommand.Execute(null); // all → none, and the section opens to show it
        Assert.False(cpu.AllState);
        Assert.Equal("keiner gewählt", cpu.Summary);
        Assert.True(curve.HasNoSource);
        Assert.True(cpu.IsOpen);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    /// <summary>A group toggle right after another edit is an undo step of its own - undo takes back the toggle only.</summary>
    [Fact]
    public void SourceSection_Toggle_IsItsOwnUndoStep()
    {
        CurveEditorController ctrl = Editor();
        CurveEditRow curve = ctrl.SelectedCurve!;
        curve.Name = "Umbenannt"; // an edit burst still open
        ctrl.SourceSensorSections.Single(s => s.Key == "CPU").ToggleAllCommand.Execute(null);
        Assert.Equal(2, curve.Sources.Count);

        ctrl.UndoCommand.Execute(null);
        CurveEditRow afterOne = ctrl.SelectedCurve!;
        Assert.Equal("Umbenannt", afterOne.Name);
        Assert.Equal(new[] { "hwmon6/temp1" }, afterOne.Sources.Select(s => s.Id));

        ctrl.UndoCommand.Execute(null);
        Assert.Equal("Quiet", ctrl.SelectedCurve!.Name);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void SourceSections_FollowAGroupChange_AndDropTheOldSubscriptions()
    {
        CurveEditorController ctrl = Editor();
        SensorCheckGroup oldCpu = ctrl.SourceSensorSections.Single(s => s.Key == "CPU");
        int stale = 0;
        oldCpu.PropertyChanged += (_, _) => stale++;

        ctrl.Sensors.Single(s => s.Id == "hwmon9/temp1").Group = "CPU";

        SensorCheckGroup cpu = ctrl.SourceSensorSections.Single(s => s.Key == "CPU");
        Assert.Equal(3, cpu.Sensors.Count);
        Assert.DoesNotContain(ctrl.SourceSensorSections, s => s.Key == "");
        cpu.Sensors.First().Selected = !cpu.Sensors.First().Selected;
        Assert.Equal(0, stale); // the replaced section no longer listens to the checks
    }

    // --- Templates -----------------------------------------------------------------------------------

    [Fact]
    public void AddFromTemplate_TakesThePresetPoints_AndTheCurrentSources()
    {
        CurveEditorController ctrl = Editor();
        CurveEditRow current = ctrl.SelectedCurve!;
        current.SensorChecks.Single(c => c.Sensor.Id == "hwmon6/temp2").Selected = true;
        current.Aggregation = SensorAggregation.Avg;

        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Constant40);

        CurveEditRow added = ctrl.SelectedCurve!;
        Assert.NotSame(current, added);
        Assert.Equal("Neue Kurve · Konstant 40 %", added.Name);
        Assert.Equal(CurvePresets.Constant40.Select(p => ((decimal)p.TemperatureC, (decimal)p.Percent)),
                     added.Points.Select(p => (p.Temperature, p.Percent)));
        Assert.Equal(InterpolationMode.Linear, added.InterpolationMode);
        Assert.Equal(new[] { "hwmon6/temp1", "hwmon6/temp2" }, added.Sources.Select(s => s.Id));
        Assert.Equal(SensorAggregation.Max, added.Aggregation); // the sources, not the mix
        Assert.Empty(added.AssignedFans);
        Assert.Equal(CurveTabPane.Curve, ctrl.Pane);
    }

    [Fact]
    public void AddFromTemplate_IsOneUndoStep()
    {
        CurveEditorController ctrl = Editor();
        CurveEditRow before = ctrl.SelectedCurve!;

        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Performance);
        Assert.Equal(3, ctrl.Curves.Count);

        ctrl.UndoCommand.Execute(null);
        Assert.Equal(new[] { "quiet", "case" }, ctrl.Curves.Select(c => c.Id));
        Assert.Equal(before.Id, ctrl.SelectedCurve!.Id);
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.RedoCommand.Execute(null);
        Assert.Equal(3, ctrl.Curves.Count);
    }

    [Fact]
    public void AddFromTemplate_RespectsTheCurveLimit()
    {
        CurveEditorController ctrl = Editor();
        while (ctrl.Curves.Count < ConfigLimits.MaxCurvesPerSet)
            ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced);

        Assert.False(ctrl.CanAddCurve);
        Assert.False(ctrl.AddCurveFromTemplateCommand.CanExecute(CurveTemplate.Balanced));
        Assert.False(ctrl.DuplicateCurveCommand.CanExecute(null));
        AddCurveNavItem addRow = ctrl.NavItems.OfType<AddCurveNavItem>().Single();
        Assert.False(addRow.CanAdd);
        Assert.Contains("32", addRow.Tooltip);

        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced);
        Assert.Equal(ConfigLimits.MaxCurvesPerSet, ctrl.Curves.Count);

        ctrl.DeleteCurveCommand.Execute(null);
        Assert.True(ctrl.AddCurveFromTemplateCommand.CanExecute(CurveTemplate.Balanced));
    }

    [Fact]
    public void AddFromTemplate_InAnInactiveProfile_IsNoActiveCurve()
    {
        CurveEditorController ctrl = Editor();
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p-loud");

        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Silent);

        Assert.False(ctrl.SelectedCurve!.ProfileIsActive);
    }

    // --- Fan blocks ------------------------------------------------------------------------------------

    [Fact]
    public void FanBlocks_OfferControllableFansOnly()
    {
        CurveEditorController ctrl = Editor();

        Assert.Equal(new[] { CpuFan, FrontFan }, ctrl.SelectedCurveFans.Select(c => c.Fan.FanId).Order());
    }

    [Fact]
    public void FanBlocks_KeepAReadOnlyFan_ThatFollowsTheCurve()
    {
        CurveEditorController ctrl = Editor();
        ctrl.Fans.Single(f => f.FanId == PumpFan).Selected = ctrl.SelectedCurve; // e.g. from an older config

        ctrl.SelectedCurve = ctrl.Curves.Single(c => c.Id == "case");
        ctrl.SelectedCurve = ctrl.Curves.Single(c => c.Id == "quiet");

        Assert.Contains(ctrl.SelectedCurveFans, c => c.Fan.FanId == PumpFan);
    }

    [Fact]
    public void FanCheck_HintsAtTheCurveItWouldMoveFrom()
    {
        CurveEditorController ctrl = Editor(); // Quiet selected; the front fan follows Case
        FanCurveCheck front = ctrl.SelectedCurveFans.Single(c => c.Fan.FanId == FrontFan);
        FanCurveCheck cpu = ctrl.SelectedCurveFans.Single(c => c.Fan.FanId == CpuFan);

        Assert.True(front.ShowMoveHint);
        Assert.Equal("Steckt in „Case“ - Häkchen verschiebt ihn hierher", front.MoveHint);
        Assert.False(cpu.ShowMoveHint); // follows this curve already

        front.Assigned = true; // ticking moves it (one curve per fan)

        Assert.Same(ctrl.SelectedCurve, front.Fan.Selected);
        Assert.False(front.ShowMoveHint);
        Assert.Equal("2 Lüfter gewählt", ctrl.SelectedCurveFanCount);
    }

    [Fact]
    public void FanBlock_Header_IsTriState_AndAssignsOrRemovesTheBlock()
    {
        CurveEditorController ctrl = Editor();
        // One block: neither fan has a position.
        FanCheckGroup block = Assert.Single(ctrl.SelectedCurveFanGroups);
        Assert.Null(block.AllState);
        Assert.True(block.AnyAssigned);
        Assert.Equal("1 von 2", block.Summary);
        Assert.Equal("1 Lüfter gewählt", ctrl.SelectedCurveFanCount);

        block.ToggleAllCommand.Execute(null);
        Assert.True(block.AllState);
        Assert.All(block.Fans, f => Assert.True(f.Assigned));

        block.ToggleAllCommand.Execute(null);
        Assert.False(block.AllState);
        Assert.False(block.AnyAssigned);
        Assert.Equal("keiner", block.Summary);
        Assert.Equal("0 Lüfter gewählt", ctrl.SelectedCurveFanCount);
    }

    [Fact]
    public void FanBlock_Toggle_IsItsOwnUndoStep()
    {
        CurveEditorController ctrl = Editor();
        ctrl.SelectedCurve!.Hysteresis = 4;
        FanCheckGroup block = Assert.Single(ctrl.SelectedCurveFanGroups);
        block.ToggleAllCommand.Execute(null);
        Assert.True(block.AllState);

        ctrl.UndoCommand.Execute(null);

        Assert.Equal(4, ctrl.SelectedCurve!.Hysteresis);
        Assert.Equal("case", ctrl.Fans.Single(f => f.FanId == FrontFan).Selected!.Id);
        Assert.Null(Assert.Single(ctrl.SelectedCurveFanGroups).AllState);
    }

    [Fact]
    public void Limits_FollowAFansMinimum()
    {
        CurveEditorController ctrl = Editor();
        CurveEditRow quiet = ctrl.SelectedCurve!;
        Assert.Equal(25, quiet.LowerLimit); // the CPU fan's 64/255
        var notified = new List<string?>();
        quiet.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        ctrl.Fans.Single(f => f.FanId == CpuFan).MinPwm = 102;

        Assert.Equal(40, quiet.LowerLimit);
        Assert.Contains(nameof(CurveEditRow.LowerLimitLabel), notified);
    }
}
