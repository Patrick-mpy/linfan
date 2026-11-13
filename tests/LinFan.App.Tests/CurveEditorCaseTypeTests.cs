// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// The case type in the editor: loaded and saved with the config, one history step per change, part of the dirty summary,
/// and it shapes the position menus, the airflow schema and its notices. „Position wählen" applies position and type
/// together.
/// </summary>
public sealed class CurveEditorCaseTypeTests
{
    private static MonitorSnapshot Snapshot(AppConfig config) => new(
        "test",
        new[] { new SensorReading("k10temp/temp1", "Tctl", SensorKind.Temperature, "°C", 50) },
        new[]
        {
            new FanReading("nct/pwm1", "Front", 900, 120, true),
            new FanReading("nct/pwm2", "Rear", 900, 120, true),
            new FanReading("nct/pwm3", "Top", 900, 120, true),
        },
        config);

    private static AppConfig Tower() => new()
    {
        Fans = new[]
        {
            new FanConfig { FanId = "nct/pwm1", Name = "Front", Location = FanLocation.CaseFrontIntake },
            new FanConfig { FanId = "nct/pwm2", Name = "Rear", Location = FanLocation.CaseRearExhaust },
            new FanConfig { FanId = "nct/pwm3", Name = "Top", Location = FanLocation.CaseTopExhaust },
        },
    };

    private static (CurveEditorController Ctrl, List<AppConfig> Saved) Editor(AppConfig config)
    {
        var saved = new List<AppConfig>();
        var ctrl = new CurveEditorController((c, _) => { saved.Add(c); return Task.FromResult(true); }) { IsConnected = true };
        ctrl.Initialize(Snapshot(config));
        return (ctrl, saved);
    }

    [Fact]
    public void LoadsTheCaseType_AndSavesIt()
    {
        var (ctrl, saved) = Editor(Tower() with { CaseType = CaseType.Server });
        Assert.Equal(CaseType.Server, ctrl.CaseType);
        Assert.Equal("Server / Rack", ctrl.CaseTypeLabel);

        ctrl.SetCaseType(CaseType.Open);
        ctrl.SaveCommand.Execute(null);

        Assert.Equal(CaseType.Open, Assert.Single(saved).CaseType);
    }

    [Fact]
    public void SetCaseType_IsOneStep_ThatTheSummaryNames_AndUndoTakesBack()
    {
        var (ctrl, _) = Editor(Tower());

        ctrl.CaseTypeMenu.Single(m => m.Header == "Server / Rack").Command!.Execute(null);

        Assert.Equal(CaseType.Server, ctrl.CaseType);
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.Contains("Gehäuse-Typ", ctrl.DirtySummary);
        Assert.Equal("", ctrl.Status.Text); // the chip and the schema show it, no toast
        Assert.True(ctrl.CaseTypeMenu.Single(m => m.Header == "Server / Rack").IsCurrent);

        ctrl.UndoCommand.Execute(null);

        Assert.Equal(CaseType.Tower, ctrl.CaseType);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void TheAirflowPage_FollowsTheType_WithItsSchemaAndNotices()
    {
        var (ctrl, _) = Editor(Tower());
        Assert.DoesNotContain(ctrl.AirflowTray, z => z.IsOutside);

        ctrl.SetCaseType(CaseType.Server);

        Assert.Contains(ctrl.AirflowZones, z => z.Key == FanLocation.FanWall);
        Assert.Equal("Top", ctrl.AirflowTray.Single(z => z.IsOutside).Fans.Single().Name);
        AirflowHintRow notice = ctrl.AirflowHints.Single(h => h.Text.Contains("„Server / Rack“"));
        Assert.True(notice.IsWarn);
        Assert.Equal("Position setzen", notice.ActionLabel);
        Assert.Equal("Draufsicht · Luft strömt von vorn (links) nach hinten (rechts)", ctrl.CaseCaption);
    }

    [Fact]
    public void AnOpenBench_HasNoBalance_AndNoNoticeAboutMissingCaseFans()
    {
        var (ctrl, _) = Editor(Tower());

        ctrl.SetCaseType(CaseType.Open);

        Assert.Equal("Offener Aufbau - ohne Gehäuse gibt es keine Druckbilanz.", ctrl.AirflowBalance.VerdictText);
        Assert.False(ctrl.AirflowBalance.HasCaseFans);
        Assert.DoesNotContain(ctrl.AirflowHints, h => h.Text.StartsWith("Noch kein Gehäuselüfter", StringComparison.Ordinal));
        Assert.Contains(ctrl.AirflowHints, h => h.Text.Contains("im offenen Aufbau zählen nur Bauteil-Lüfter"));
    }

    [Fact]
    public void ALaptop_HasNoBalance_AndNamesWhatItLacks()
    {
        var (ctrl, _) = Editor(Tower());
        Assert.Equal(new[] { "Tower", "Server / Rack", "Laptop", "Offener Aufbau" }, ctrl.CaseTypeMenu.Select(m => m.Header));

        ctrl.CaseTypeMenu.Single(m => m.Header == "Laptop").Command!.Execute(null);

        Assert.Equal(CaseType.Laptop, ctrl.CaseType);
        Assert.Equal("Laptop - die Lüfter saugen direkt an und blasen direkt aus; eine Druckbilanz gibt es hier nicht.",
            ctrl.AirflowBalance.VerdictText);
        Assert.False(ctrl.AirflowBalance.HasCaseFans);
        Assert.DoesNotContain(ctrl.AirflowHints, h => h.Text.StartsWith("Noch kein Gehäuselüfter", StringComparison.Ordinal));
        Assert.Contains(ctrl.AirflowHints, h => h.Text.EndsWith("die es bei „Laptop“ nicht gibt.", StringComparison.Ordinal));
        Assert.Equal("Draufsicht · Scharnier oben · ⊙ von unten, ⊗ durch die Tastatur, ↑ ← hinaus", ctrl.CaseCaption);
    }

    [Fact]
    public void AMixedSide_GetsANotice_ThatOpensTheFewerFan()
    {
        AppConfig config = Tower();
        var (ctrl, _) = Editor(config with
        {
            Fans = [config.Fans[0], config.Fans[1] with { Location = FanLocation.CaseFrontExhaust }, config.Fans[2]],
        });
        FanAssignRow? opened = null;
        ctrl.LocationDialogRequested += (_, row) => opened = row;

        AirflowHintRow notice = ctrl.AirflowHints.Single(h => h.Text.StartsWith("Vorn: 1 × Einlass und 1 × Auslass", StringComparison.Ordinal));
        notice.Action!.Execute(null);

        Assert.False(notice.IsWarn);
        Assert.Equal("Front", opened?.Name); // a tie: the intake is the one asked about
        Assert.True(ctrl.AirflowZones.Single(z => z.Key == FanLocation.CaseFrontIntake).IsMixed);
    }

    [Fact]
    public void ThePositionMenus_OfferOnlyThePositionsOfTheType()
    {
        var (ctrl, _) = Editor(Tower());
        FanAssignRow fan = ctrl.Fans.First();
        Assert.Contains(fan.PositionMenu, m => m.AutomationName == "Gehäuse oben · Auslass");
        Assert.DoesNotContain(fan.PositionMenu, m => m.Header == "Lüfterwand");

        ctrl.SetCaseType(CaseType.Server);

        Assert.DoesNotContain(fan.PositionMenu, m => m.AutomationName == "Gehäuse oben · Auslass");
        Assert.Contains(fan.PositionMenu, m => m.Header == "Lüfterwand");
        Assert.Contains(fan.PositionMenu, m => m.Header == "Server · von vorn nach hinten");
    }

    [Fact]
    public void ALocationPick_AppliesPositionAndType_AsOneStep()
    {
        var (ctrl, saved) = Editor(Tower());
        LocationPickController pick = ctrl.CreateLocationPick(ctrl.Fans.Single(f => f.FanId == "nct/pwm3"));
        Assert.Equal(CaseType.Tower, pick.CaseType);

        pick.CaseTypeMenu.Single(m => m.Header == "Server / Rack").Command!.Execute(null);
        Assert.Equal("Gehäuse wird „Server / Rack“", pick.FromText);
        pick.Selected = FanLocation.FanWall;
        ctrl.ApplyLocationPick("nct/pwm3", pick.Result!);

        Assert.Equal(CaseType.Server, ctrl.CaseType);
        Assert.Equal(FanLocation.FanWall, ctrl.Fans.Single(f => f.FanId == "nct/pwm3").Location.Value);
        ctrl.UndoCommand.Execute(null); // one step takes both back
        Assert.Equal(CaseType.Tower, ctrl.CaseType);
        Assert.Equal(FanLocation.CaseTopExhaust, ctrl.Fans.Single(f => f.FanId == "nct/pwm3").Location.Value);
        Assert.Empty(saved);
    }

    [Fact]
    public void ACancelledTypeChange_InTheDialog_LeavesTheEditorAlone()
    {
        var (ctrl, _) = Editor(Tower());
        LocationPickController pick = ctrl.CreateLocationPick(ctrl.Fans.First());

        pick.CaseType = CaseType.Open;
        pick.CaseType = CaseType.Tower;

        Assert.False(pick.Changed);
        Assert.Null(pick.Result);
        Assert.Equal(CaseType.Tower, ctrl.CaseType);
    }

    [Fact]
    public void AServerWithOnlyItsFanWall_SaysSo_InsteadOfAskingForCaseFans()
    {
        AppConfig config = Tower();
        var (ctrl, _) = Editor(config with
        {
            CaseType = CaseType.Server,
            Fans = [config.Fans[0] with { Location = FanLocation.FanWall }, config.Fans[1] with { Location = FanLocation.CpuCooler },
                config.Fans[2] with { Location = FanLocation.Psu }],
        });

        Assert.StartsWith("Nur die Lüfterwand", ctrl.AirflowBalance.VerdictText, StringComparison.Ordinal);
        Assert.DoesNotContain(ctrl.AirflowHints, h => h.Text.StartsWith("Noch kein Gehäuselüfter", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNoIntakeNotice_OpensAFanTheBalanceCounts()
    {
        AppConfig config = Tower();
        // The top fan comes first, but a server has no top - the notice must ask about the rear fan.
        var (ctrl, _) = Editor(config with
        {
            CaseType = CaseType.Server,
            Fans = [config.Fans[2], config.Fans[1], config.Fans[0] with { Location = FanLocation.CpuCooler }],
        });
        FanAssignRow? opened = null;
        ctrl.LocationDialogRequested += (_, row) => opened = row;

        ctrl.AirflowHints.Single(h => h.ActionLabel == "Richtung prüfen").Action!.Execute(null);

        Assert.Equal("Rear", opened?.Name);
    }
}
