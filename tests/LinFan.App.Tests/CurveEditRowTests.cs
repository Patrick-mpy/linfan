// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Tests für die Warnung „Leistung sinkt bei steigender Temperatur" (<see cref="CurveEditRow.HasDecreasingPercent"/>):
/// reagiert auf Add/Remove von Punkten und auf Wertänderungen einzelner Punkte; gleiche Prozente (flach) sind ok.
/// Sowie <see cref="CurveEditRow.HasNoSource"/> (steuert den „Kein Quell-Sensor"-Hinweis im Kurven-Tab).
/// </summary>
public sealed class CurveEditRowTests
{
    private static CurveEditRow MakeRow()
    {
        var sensors = new ObservableCollection<SensorOption>();
        return new CurveEditRow("c1", "Kurve", Array.Empty<string>(), SensorAggregation.Max, 0m, sensors);
    }

    /// <summary>Kurve mit zwei verfügbaren Sensoren; <paramref name="selectedIds"/> sind anfangs als Quelle gesetzt.</summary>
    private static CurveEditRow MakeRowWithSensors(params string[] selectedIds)
    {
        var sensors = new ObservableCollection<SensorOption>
        {
            new("s1", "CPU"),
            new("s2", "GPU"),
        };
        return new CurveEditRow("c1", "Kurve", selectedIds, SensorAggregation.Max, 0m, sensors);
    }

    [Fact]
    public void NewRow_WithoutPoints_NoWarning() =>
        Assert.False(MakeRow().HasDecreasingPercent);

    [Fact]
    public void MonotonicRisingPoints_NoWarning()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        row.AddPointRow(50, 50);
        row.AddPointRow(80, 100);

        Assert.False(row.HasDecreasingPercent);
    }

    [Fact]
    public void FlatPoints_SamePercent_NoWarning()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 50);
        row.AddPointRow(50, 50);
        row.AddPointRow(80, 50);

        Assert.False(row.HasDecreasingPercent); // gleich bleibend ist kein Sinken
    }

    [Fact]
    public void DecreasingPercentOverTemperature_Warns()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 60);
        row.AddPointRow(80, 40); // bei höherer Temperatur weniger Leistung

        Assert.True(row.HasDecreasingPercent);
    }

    [Fact]
    public void DecreasingDetected_AfterSortByTemperature_NotInsertOrder()
    {
        // In Einfüge-Reihenfolge stiege es (40 → 60); nach Temperatur-Sortierung sinkt es (80°C=40 < 30°C=60).
        CurveEditRow row = MakeRow();
        row.AddPointRow(80, 40);
        row.AddPointRow(30, 60);

        Assert.True(row.HasDecreasingPercent);
    }

    [Fact]
    public void Warning_ReactsTo_PointValueChange()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        row.AddPointRow(80, 100);
        Assert.False(row.HasDecreasingPercent);

        // Den hohen Punkt unter den niedrigen drücken → jetzt sinkt es.
        PointRow high = row.Points.Single(p => p.Temperature == 80);
        high.Percent = 10;
        Assert.True(row.HasDecreasingPercent);

        // wieder anheben → Warnung verschwindet
        high.Percent = 100;
        Assert.False(row.HasDecreasingPercent);
    }

    [Fact]
    public void Warning_ReactsTo_TemperatureChange()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        row.AddPointRow(80, 100);
        Assert.False(row.HasDecreasingPercent);

        // Temperaturen tauschen die Reihenfolge: der 100%-Punkt wird der kältere → danach sinkt es.
        row.Points.Single(p => p.Percent == 100).Temperature = 10;
        Assert.True(row.HasDecreasingPercent);
    }

    [Fact]
    public void Warning_ReactsTo_PointRemoval()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 60);
        row.AddPointRow(50, 30); // verursacht Sinken
        row.AddPointRow(80, 100);
        Assert.True(row.HasDecreasingPercent);

        PointRow dip = row.Points.Single(p => p.Percent == 30);
        row.Points.Remove(dip);

        Assert.False(row.HasDecreasingPercent); // verbleibend 60 → 100 steigt
    }

    [Fact]
    public void RemovedPoint_NoLongerAffectsWarning()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        PointRow dip = row.Points.Single();
        row.AddPointRow(80, 100);

        row.Points.Remove(dip);
        // Wäre der Punkt noch abonniert, würde dieser Setter ein Recompute mit einem Geist-Punkt auslösen.
        dip.Percent = 5;

        Assert.False(row.HasDecreasingPercent);
    }

    // --- HasNoSource: treibt den „Kein Quell-Sensor"-Hinweis nahe dem Graphen --------------------

    [Fact]
    public void HasNoSource_TrueWhenNothingSelected()
    {
        Assert.True(MakeRowWithSensors().HasNoSource); // keine Quelle angekreuzt
        Assert.Empty(MakeRowWithSensors().Sources);
    }

    [Fact]
    public void HasNoSource_FalseWhenSourceSelected()
    {
        CurveEditRow row = MakeRowWithSensors("s1");
        Assert.False(row.HasNoSource);
    }

    [Fact]
    public void HasNoSource_TogglesWithSelection_AndNotifies()
    {
        CurveEditRow row = MakeRowWithSensors(); // anfangs ohne Quelle → true
        Assert.True(row.HasNoSource);

        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // Quelle ankreuzen → HasNoSource muss false werden UND change-notifizieren.
        SensorCheck cpu = row.SensorChecks.Single(c => c.Sensor.Id == "s1");
        cpu.Selected = true;
        Assert.False(row.HasNoSource);
        Assert.Contains(nameof(CurveEditRow.HasNoSource), notified);

        // Wieder abwählen → zurück auf true.
        notified.Clear();
        cpu.Selected = false;
        Assert.True(row.HasNoSource);
        Assert.Contains(nameof(CurveEditRow.HasNoSource), notified);
    }

    private static CurveEditRow MakeRowWithSensorCount(int n, params string[] selectedIds)
    {
        var sensors = new ObservableCollection<SensorOption>();
        for (int i = 0; i < n; i++)
            sensors.Add(new SensorOption($"s{i}", $"Sensor {i}"));
        return new CurveEditRow("c1", "Kurve", selectedIds, SensorAggregation.Max, 0m, sensors);
    }

    // --- Mix: only with two or more sources ------------------------------------------------------

    [Fact]
    public void HasMultipleSources_FollowsTheSelection_AndNotifies()
    {
        CurveEditRow row = MakeRowWithSensors("s1");
        Assert.False(row.HasMultipleSources);
        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.SensorChecks.Single(c => c.Sensor.Id == "s2").Selected = true;

        Assert.True(row.HasMultipleSources);
        Assert.Contains(nameof(CurveEditRow.HasMultipleSources), notified);
    }

    // --- Segment switches: a list box writes null when it loses its selection ---------------------

    [Fact]
    public void SegmentSetters_IgnoreNull()
    {
        CurveEditRow row = MakeRow();
        row.Aggregation = SensorAggregation.Avg;
        row.InterpolationMode = InterpolationMode.Spline;

        row.SelectedAggregation = null!;
        row.SelectedInterpolation = null!;

        Assert.Equal(SensorAggregation.Avg, row.Aggregation);
        Assert.Equal(InterpolationMode.Spline, row.InterpolationMode);
    }

    [Fact]
    public void InterpolationOptions_RunFromStraightToStepped()
    {
        Assert.Equal(new[] { InterpolationMode.Linear, InterpolationMode.Spline, InterpolationMode.Step },
            MakeRow().Interpolations.Select(o => o.Value));
        Assert.All(InterpolationOption.All, o => Assert.False(string.IsNullOrEmpty(o.Summary)));
        Assert.Equal("Stufen", InterpolationOption.For(InterpolationMode.Step).Display);
    }

    // --- Hints next to the steppers --------------------------------------------------------------

    [Fact]
    public void SmoothingHint_SaysOffAtZero_AndFollowsTheValue()
    {
        CurveEditRow row = MakeRow();
        row.SmoothingSeconds = 0;
        Assert.StartsWith("Aus", row.SmoothingHint);

        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        row.SmoothingSeconds = 4;

        Assert.Contains("4 s", row.SmoothingHint);
        Assert.Contains(nameof(CurveEditRow.SmoothingHint), notified);
    }

    [Fact]
    public void HysteresisBand_NeedsAValueAndALiveTemperature()
    {
        CurveEditRow row = MakeRow();
        row.Hysteresis = 2.5m;
        Assert.Equal("±2.5°", row.HysteresisLabel);
        Assert.False(row.HasHysteresisBand); // no live value yet: there is nothing to draw it around

        row.LiveTemperature = 50;
        Assert.True(row.HasHysteresisBand);

        row.Hysteresis = 0;
        Assert.False(row.HasHysteresisBand);
    }

    // --- Points: at least one stays, adding follows the curve ------------------------------------

    [Fact]
    public void RemoveCommand_KeepsTheLastPoint()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        row.AddPointRow(80, 100);
        PointRow first = row.Points[0];
        Assert.True(first.RemoveCommand!.CanExecute(null));

        first.RemoveCommand.Execute(null);

        PointRow last = Assert.Single(row.Points);
        Assert.False(last.RemoveCommand!.CanExecute(null));
        last.RemoveCommand.Execute(null); // a stale button press changes nothing
        Assert.Single(row.Points);
    }

    [Fact]
    public void Points_AreNumbered_InListOrder()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        row.AddPointRow(80, 100);
        row.AddPointRow(50, 40);

        Assert.Equal(new[] { 1, 2, 3 }, row.Points.Select(p => p.Number));
        row.Points.RemoveAt(0);
        Assert.Equal(new[] { 1, 2 }, row.Points.Select(p => p.Number));
        Assert.Equal("Punkt 2 entfernen", row.Points[1].RemoveName);
    }

    [Fact]
    public void AddPoint_GoesTenDegreesAboveTheHottestPoint()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        row.AddPointRow(70, 60);

        row.AddPointCommand.Execute(null);

        Assert.Equal((80m, 60m), (row.Points[^1].Temperature, row.Points[^1].Percent));
    }

    [Fact]
    public void AddPoint_AboveNinety_GoesHalfwayBetweenTheTwoHottest()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(70, 50);
        row.AddPointRow(95, 100);

        row.AddPointCommand.Execute(null);

        // Inserted before the hottest point, so the list stays in temperature order.
        Assert.Equal(new[] { (70m, 50m), (83m, 75m), (95m, 100m) }, row.Points.Select(p => (p.Temperature, p.Percent)));
    }

    [Fact]
    public void AddPoint_OnAnEmptyCurve_StartsInTheMiddle()
    {
        CurveEditRow row = MakeRow();
        row.AddPointCommand.Execute(null);
        Assert.Equal((50m, 50m), (row.Points.Single().Temperature, row.Points.Single().Percent));
    }

    [Fact]
    public void AddPoint_IsOff_WhenItsTemperatureIsTaken_OrTheCurveIsFull()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(94, 90);
        row.AddPointRow(95, 100); // halfway rounds to 95, which is taken
        Assert.False(row.AddPointCommand.CanExecute(null));

        row.Points[0].Temperature = 60; // halfway is now 78 - free again
        Assert.True(row.AddPointCommand.CanExecute(null));

        CurveEditRow full = MakeRow();
        for (int t = 0; t < ConfigLimits.MaxPointsPerCurve; t++)
            full.AddPointRow(t / 2m, 50);
        Assert.False(full.AddPointCommand.CanExecute(null));
    }

    [Fact]
    public void AddPointAt_RefusesATakenTemperature_AndAFullCurve()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        row.AddPointRow(80, 100);

        Assert.False(row.AddPointAtCommand.CanExecute(new ChartPoint(30, 50)));
        Assert.True(row.AddPointAtCommand.CanExecute(new ChartPoint(55, 50)));

        row.AddPointAtCommand.Execute(new ChartPoint(55, 50));
        Assert.Equal(new[] { 30m, 55m, 80m }, row.Points.Select(p => p.Temperature)); // inserted in order

        CurveEditRow full = MakeRow();
        for (int t = 0; t < ConfigLimits.MaxPointsPerCurve; t++)
            full.AddPointRow(t, 50);
        Assert.False(full.AddPointAtCommand.CanExecute(new ChartPoint(500, 50)));
    }

    [Fact]
    public void AddPointAt_CanExecute_FollowsADraggedPoint()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(30, 20);
        int raised = 0;
        row.AddPointAtCommand.CanExecuteChanged += (_, _) => raised++;

        row.Points[0].Temperature = 40;

        Assert.True(raised > 0);
        Assert.True(row.AddPointAtCommand.CanExecute(new ChartPoint(30, 50)));
    }

    // --- Power limits of the assigned fans (grey bands) ------------------------------------------

    private static FanAssignRow Fan(string id, string name, int minPwm, int maxPwm, bool canControl,
                                    CurveEditRow? curve, ObservableCollection<FanAssignRow> fans)
    {
        var fan = new FanAssignRow(new FanConfig { FanId = id, Name = name, MinPwm = (byte)minPwm, MaxPwm = (byte)maxPwm },
                                   selected: curve, availableCurves: new ObservableCollection<CurveEditRow>(),
                                   canControl: canControl);
        fans.Add(fan);
        return fan;
    }

    [Fact]
    public void Limits_AreTheNarrowestOfTheAssignedControllableFans()
    {
        (CurveEditRow row, ObservableCollection<FanAssignRow> fans) = MakeRowWithFans("s1");
        Fan("a", "CPU", minPwm: 64, maxPwm: 255, canControl: true, row, fans);    // 25 %
        Fan("b", "Front", minPwm: 38, maxPwm: 204, canControl: true, row, fans);  // 15 %, max 80 %
        Fan("c", "Pump", minPwm: 128, maxPwm: 153, canControl: false, row, fans); // read-only: ignored
        Fan("d", "Rear", minPwm: 200, maxPwm: 255, canControl: true, null, fans);  // other curve: ignored

        Assert.Equal(25, row.LowerLimit);
        Assert.Equal("Min 25 % · CPU", row.LowerLimitLabel);
        Assert.Equal(80, row.UpperLimit);
        Assert.Equal("Max 80 % · Front", row.UpperLimitLabel);
        Assert.True(row.HasLimits);
    }

    [Fact]
    public void Limits_AreAbsent_WithoutAFloorOrCeiling()
    {
        (CurveEditRow row, ObservableCollection<FanAssignRow> fans) = MakeRowWithFans("s1");
        Fan("a", "CPU", minPwm: 0, maxPwm: 255, canControl: true, row, fans);

        Assert.Null(row.LowerLimit);
        Assert.Null(row.UpperLimit);
        Assert.Null(row.LowerLimitLabel);
        Assert.False(row.HasLimits);
    }

    [Fact]
    public void Limits_FollowAnAssignment_WithANotification()
    {
        (CurveEditRow row, ObservableCollection<FanAssignRow> fans) = MakeRowWithFans("s1");
        FanAssignRow fan = Fan("a", "CPU", minPwm: 64, maxPwm: 255, canControl: true, null, fans);
        Assert.Null(row.LowerLimit);
        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        fan.Selected = row;
        row.NotifyActiveChanged(); // what the controller does on an assignment change

        Assert.Equal(25, row.LowerLimit);
        Assert.Contains(nameof(CurveEditRow.LowerLimit), notified);
        Assert.Contains(nameof(CurveEditRow.HasLimits), notified);
    }

    // --- RebuildSensorChecks: live visibility filter (mirror of the fan assignment list) ---------

    [Fact]
    public void RebuildSensorChecks_PreservesSelection_AndRewiresSelectionChanged()
    {
        CurveEditRow row = MakeRowWithSensors("s1");
        row.Sensors.First(s => s.Id == "s2").Visible = false;

        row.RebuildSensorChecks();

        SensorCheck s1 = Assert.Single(row.SensorChecks);
        Assert.Equal("s1", s1.Sensor.Id);
        Assert.True(s1.Selected);

        // The rebuilt check must still notify the row on selection changes.
        s1.Selected = false;
        Assert.True(row.HasNoSource);
    }

    [Fact]
    public void RebuildSensorChecks_KeepsHiddenSource()
    {
        CurveEditRow row = MakeRowWithSensorCount(4, "s0");

        // Hide the selected source plus one more sensor: the source survives, the other drops out.
        row.Sensors.First(s => s.Id == "s0").Visible = false;
        row.Sensors.First(s => s.Id == "s1").Visible = false;
        row.RebuildSensorChecks();

        Assert.Equal(3, row.SensorChecks.Count);
        Assert.True(row.SensorChecks.Single(c => c.Sensor.Id == "s0").Selected);
        Assert.DoesNotContain(row.SensorChecks, c => c.Sensor.Id == "s1");
    }

    // --- Punkte-Collapse: ShowPoints + PointsLabel („X Punkte", eingeklappt) ---------------------

    [Fact]
    public void Points_StartCollapsed() =>
        Assert.False(MakeRow().ShowPoints);

    [Fact]
    public void PointsLabel_Singular_AndPlural()
    {
        CurveEditRow row = MakeRow();
        Assert.Equal("0 Punkte", row.PointsLabel);

        row.AddPointRow(40, 30);
        Assert.Equal("1 Punkt", row.PointsLabel);

        row.AddPointRow(60, 60);
        Assert.Equal("2 Punkte", row.PointsLabel);
    }

    [Fact]
    public void PointsLabel_NotifiesAndUpdates_OnRemove()
    {
        CurveEditRow row = MakeRow();
        row.AddPointRow(40, 30);
        row.AddPointRow(60, 60);

        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.Points.RemoveAt(0);

        Assert.Equal("1 Punkt", row.PointsLabel);
        Assert.Contains(nameof(CurveEditRow.PointsLabel), notified);
    }

    /// <summary>Were hardcoded German and showed up that way in the English UI too.</summary>
    [Fact]
    public void PointsLabel_AndSourceLabel_FollowLanguage()
    {
        CurveEditRow row = MakeRowWithSensors("s1", "s2");
        row.AddPointRow(40, 30);
        row.AddPointRow(60, 60);

        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);
            Assert.Equal("2 points", row.PointsLabel);
            Assert.EndsWith("2 sensors", row.Label);

            Localizer.Instance.SetLanguage(LanguageChoice.German);
            Assert.Equal("2 Punkte", row.PointsLabel);
            Assert.EndsWith("2 Sensoren", row.Label);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // restore the pinned test culture
        }
    }

    // --- IsActive: grün/grau-Badge (Quelle UND zugeordneter Lüfter) -----------------------------

    private static (CurveEditRow row, ObservableCollection<FanAssignRow> fans) MakeRowWithFans(params string[] selectedSensorIds)
    {
        var sensors = new ObservableCollection<SensorOption> { new("s1", "CPU"), new("s2", "GPU") };
        var fans = new ObservableCollection<FanAssignRow>();
        var row = new CurveEditRow("c1", "Kurve", selectedSensorIds, SensorAggregation.Max, 0m,
                                   sensors, InterpolationMode.Linear, fans);
        return (row, fans);
    }

    [Fact]
    public void From_RelayedValuesOutOfRange_AreClamped_InsteadOfThrowing()
    {
        // Another client's curve reaches this GUI through the daemon; a bare (decimal) cast of 1e30 threw an
        // OverflowException on the UI thread and ended the process, on every relaunch too.
        var curve = new CurveConfig
        {
            Id = "x",
            Name = "x",
            HysteresisC = 1e30,
            SmoothingSeconds = 1e30,
            Points = new[] { new CurvePoint(1e30, 50), new CurvePoint(40, 1e30), new CurvePoint(double.NaN, 10) },
        };

        CurveEditRow row = CurveEditRow.From(curve, new ObservableCollection<SensorOption>());

        Assert.Equal((decimal)ConfigSanitizer.MaxHysteresisC, row.Hysteresis);
        Assert.Equal((decimal)ConfigSanitizer.MaxSmoothingSeconds, row.SmoothingSeconds);
        Assert.Equal(
            new[] { (40m, 100m), ((decimal)ConfigSanitizer.MaxPointTempC, 50m) },
            row.Points.Select(p => (p.Temperature, p.Percent)));
    }

    [Fact]
    public void IsActive_RequiresSourceAndAssignedFan()
    {
        (CurveEditRow row, ObservableCollection<FanAssignRow> fans) = MakeRowWithFans("s1");
        Assert.False(row.IsActive); // Quelle vorhanden, aber kein Lüfter zugeordnet

        var fan = new FanAssignRow(new FanConfig { FanId = "f1", Name = "Fan" }, selected: row,
                                   availableCurves: new ObservableCollection<CurveEditRow> { row });
        fans.Add(fan);
        Assert.True(row.IsActive); // Quelle + zugeordneter Lüfter

        fan.Selected = null;
        Assert.False(row.IsActive); // Lüfter wieder gelöst
    }

    [Fact]
    public void IsActive_FalseWithoutSource_EvenWhenFanAssigned()
    {
        (CurveEditRow row, ObservableCollection<FanAssignRow> fans) = MakeRowWithFans(); // keine Quelle
        var fan = new FanAssignRow(new FanConfig { FanId = "f1", Name = "Fan" }, selected: row,
                                   availableCurves: new ObservableCollection<CurveEditRow> { row });
        fans.Add(fan);
        Assert.False(row.IsActive);
    }
}
