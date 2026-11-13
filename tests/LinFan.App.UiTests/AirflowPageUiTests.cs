// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.UiTests;

/// <summary>
/// Settings › Airflow in the real window: every fan sits in its zone of the case schema and opens „Position wählen",
/// the pressure balance shows its basis and verdict, a notice's action works, and the proposal can go into another
/// profile than the running one.
/// </summary>
public class AirflowPageUiTests
{
    private const int TabSettings = 2;

    private static MonitorSnapshot Snapshot()
    {
        var curve = new CurveConfig
        {
            Id = "c1",
            Name = "Handarbeit",
            SourceSensorIds = new[] { "hwmon0/temp1" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "Tctl" } },
            Fans = new[]
            {
                new FanConfig { FanId = "cpu", Name = "CPU", Location = FanLocation.CpuCooler, AssignedCurveId = "c1" },
                new FanConfig { FanId = "front", Name = "Front", Location = FanLocation.CaseFrontIntake, AssignedCurveId = "c1" },
                new FanConfig { FanId = "rear", Name = "Heck", Location = FanLocation.CaseRearExhaust, AssignedCurveId = "c1" },
                new FanConfig { FanId = "loose", Name = "Lose", Location = FanLocation.Unspecified },
            },
            Curves = new[] { curve },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p1", Name = "Standard", Curves = new[] { curve },
                    Assignments = new[] { new ProfileAssignment("cpu", "c1"), new ProfileAssignment("front", "c1"), new ProfileAssignment("rear", "c1") },
                },
                new Profile { Id = "p2", Name = "Leise", Curves = new[] { curve } },
            },
            ActiveProfileId = "p1",
        };
        return new MonitorSnapshot("Verbunden",
            new[] { new SensorReading("hwmon0/temp1", "Tctl", SensorKind.Temperature, "°C", 50) },
            new[]
            {
                new FanReading("cpu", "CPU", 1200, 128, CanControl: true, ControlState: FanControlState.Curve, ControlCurveId: "c1"),
                new FanReading("front", "Front", 900, 100, CanControl: true, ControlState: FanControlState.Curve, ControlCurveId: "c1"),
                new FanReading("rear", "Heck", 1000, 110, CanControl: true, ControlState: FanControlState.Curve, ControlCurveId: "c1"),
                new FanReading("loose", "Lose", 700, 0, CanControl: false),
            },
            config, Connected: true);
    }

    private static (MainController Ctrl, MainWindow Window) Show()
    {
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(Snapshot()));
        var window = new MainWindow { DataContext = ctrl, Height = 1800 };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        Assert.Single(window.Find<TabControl>()).SelectedIndex = TabSettings;
        ctrl.Settings.OpenSection(SettingsSection.Airflow);
        Dispatcher.UIThread.RunJobs();
        return (ctrl, window);
    }

    [AvaloniaFact]
    public void EveryFanSitsInItsZone_AndClickingItOpensTheDialog()
    {
        var (_, window) = Show();
        CaseSchemaView schema = window.Find<CaseSchemaView>().Single(v => v.IsEffectivelyVisible);
        List<Button> fans = schema.Find<Button>().Where(b => b.Classes.Contains("caseFan")).ToList();
        Assert.Equal(4, fans.Count);
        Button front = fans.Single(b => b.DataContext is CaseFan { FanId: "front" });
        CaseZone zone = front.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<CaseZone>().First();
        Assert.Equal(FanLocation.CaseFrontIntake, zone.Key);

        front.Command!.Execute(null);
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count > 0);

        FanLocationDialog dialog = Assert.IsType<FanLocationDialog>(Assert.Single(window.OwnedWindows));
        Assert.Equal("Position von „Front“", ((LocationPickController)dialog.DataContext!).Heading);
        dialog.Close();
    }

    [AvaloniaFact]
    public void Balance_ShowsItsBasisAndVerdict()
    {
        var (ctrl, window) = Show();

        Assert.Equal("nach Anzahl", ctrl.Editor.AirflowBalance.BasisText);
        Assert.Equal("Ausgeglichen.", ctrl.Editor.AirflowBalance.VerdictText);
        Assert.Contains(window.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Einlass · 1 Lüfter");
        Assert.Contains(window.Find<PressureBar>(), b => b.IsEffectivelyVisible && b.IntakePercent == 50);
    }

    [AvaloniaFact]
    public void ANoticeWithoutPosition_OpensTheDialogForThatFan()
    {
        var (_, window) = Show();
        Button action = window.Find<Button>().Single(b => b.IsEffectivelyVisible && b.DataContext is AirflowHintRow h
            && h.Text.Contains("„Lose“") && UiTestHelpers.ButtonLabel(b) is null or "Position setzen");

        action.Command!.Execute(null);
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count > 0);

        var dialog = Assert.IsType<FanLocationDialog>(Assert.Single(window.OwnedWindows));
        Assert.Equal("Position von „Lose“", ((LocationPickController)dialog.DataContext!).Heading);
        dialog.Close();
    }

    [AvaloniaFact]
    public void TheProposal_CanGoIntoAProfileThatIsNotRunning()
    {
        var (ctrl, window) = Show();
        Assert.Equal("Standard", ctrl.Editor.AirflowTargetName);

        ctrl.Editor.AirflowTargetMenu.Single(m => m.Header == "Leise").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(ctrl.Editor.AirflowTargetIsRunning);
        Button apply = window.Find<Button>().Single(b => b.Name == "AirflowApplyButton");
        Assert.Equal("In „Leise“ übernehmen", apply.Content);
        apply.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        ProfileRow quiet = ctrl.Editor.Profiles.Single(p => p.Id == "p2");
        Assert.Equal("airflow-intake", quiet.Assignments.Single(a => a.FanId == "front").CurveId);
        Assert.Equal("c1", ctrl.Editor.Profiles.Single(p => p.Id == "p1").Assignments.Single(a => a.FanId == "front").CurveId);
    }

    [AvaloniaFact]
    public void RoleTiles_ShowWhatTheProfileHas_AndWhatWouldComeFromTheTemplate()
    {
        var (ctrl, _) = Show();

        Assert.Equal(5, ctrl.Editor.AirflowRoles.Count);
        Assert.All(ctrl.Editor.AirflowRoles, r => Assert.False(r.InProfile));
        AirflowRoleTile intake = ctrl.Editor.AirflowRoles.Single(r => r.Title == "Einlass");
        Assert.Equal("Front", intake.FansText);
        Assert.Equal("Tctl", intake.Detail);
        Assert.True(ctrl.Editor.AirflowRoles.Single(r => r.Title == "GPU").IsUnused);
    }

    /// <summary>
    /// A resync (after an import or reset) rebuilds the fan rows under an open „Position wählen": the dialog closes as a
    /// cancel - its nudge ends, the position stays.
    /// </summary>
    [AvaloniaFact]
    public void AResync_ClosesAnOpenPositionDialog_AsACancel()
    {
        var (ctrl, window) = Show();
        Button front = window.Find<CaseSchemaView>().Single(v => v.IsEffectivelyVisible)
            .Find<Button>().Single(b => b.Classes.Contains("caseFan") && b.DataContext is CaseFan { FanId: "front" });
        front.Command!.Execute(null);
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count > 0);
        var dialog = Assert.IsType<FanLocationDialog>(Assert.Single(window.OwnedWindows));
        var pick = (LocationPickController)dialog.DataContext!;
        pick.Zones.Single(z => z.Key == FanLocation.CaseTopExhaust).Pick!.Execute(null); // picked, not applied
        pick.NudgeCommand.Execute(null);
        Assert.True(pick.Manual.IsActive);

        ctrl.Editor.Resync(Snapshot());
        UiTestHelpers.PumpUntil(() => window.OwnedWindows.Count == 0);

        Assert.Empty(window.OwnedWindows);
        Assert.False(pick.Manual.IsActive);
        Assert.Equal(FanLocation.CaseFrontIntake, ctrl.Editor.Fans.Single(f => f.FanId == "front").Location.Value);
    }

    [AvaloniaFact]
    public void TheCaseChip_SwitchesTheSchemaToTheTopViewOfAServer()
    {
        var (ctrl, window) = Show();
        Button chip = Assert.Single(window.Find<Button>(), b => b.Name == "CaseTypeChip");
        Assert.Contains(chip.Find<TextBlock>(), t => t.Text == "Tower");
        CaseSchemaView schema = Assert.Single(window.Find<CaseSchemaView>());
        Assert.Contains(schema.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Seitenansicht · vorn links, hinten rechts");

        var menu = (Avalonia.Controls.Primitives.PopupFlyoutBase)chip.Flyout!;
        menu.ShowAt(chip);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);
        ctrl.Editor.CaseTypeMenu.Single(m => m.Header == "Server / Rack").Command!.Execute(null);
        menu.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(chip.Find<TextBlock>(), t => t.Text == "Server / Rack");
        Grid grid = schema.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 4 && g.RowDefinitions.Count == 4);
        Assert.Equal(19, grid.ColumnDefinitions[0].Width.Value); // the server's top view: front | fan wall | parts | rear
        Assert.True(ShowsZoneLabel(schema, "Lüfterwand"));
        Assert.True(ctrl.Editor.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void ALaptop_ShowsTheTopViewOfItsBase()
    {
        var (ctrl, window) = Show();

        ctrl.Editor.SetCaseType(CaseType.Laptop);
        Dispatcher.UIThread.RunJobs();

        CaseSchemaView schema = Assert.Single(window.Find<CaseSchemaView>());
        Grid grid = schema.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 3 && g.RowDefinitions.Count == 4);
        Assert.Equal(16, grid.ColumnDefinitions[0].Width.Value); // side vent | the two blowers
        Assert.True(ShowsZoneLabel(schema, "Tastatur · Einlass"));
        Assert.True(ShowsZoneLabel(schema, "Unterseite · Einlass"));
        Assert.True(ShowsZoneLabel(schema, "Nicht im Schema (Laptop)")); // the tower's case fans
    }

    [AvaloniaFact]
    public void AFanAtAPositionTheTypeLacks_ShowsInNotInTheSchema()
    {
        var (ctrl, window) = Show();

        ctrl.Editor.SetCaseType(CaseType.Open);
        Dispatcher.UIThread.RunJobs();

        CaseSchemaView schema = Assert.Single(window.Find<CaseSchemaView>());
        Assert.True(ShowsZoneLabel(schema, "Nicht im Schema (Offener Aufbau)"));
        Assert.Contains(schema.Find<Button>(), b => b.IsEffectivelyVisible && b.Find<TextBlock>().Any(t => t.Text == "Heck"));
    }

    // A zone's label is a Run (the arrow is the second one).
    private static bool ShowsZoneLabel(CaseSchemaView schema, string label) => schema.Find<TextBlock>()
        .Any(t => t.IsEffectivelyVisible && t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == label) == true);
}
