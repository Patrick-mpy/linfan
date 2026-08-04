// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using LinFan.App.Controllers;
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

    // --- Sensor-Collapse: DisplayedSensorChecks (3 + „alle einblenden", aktive zuerst) ----------

    private static CurveEditRow MakeRowWithSensorCount(int n, params string[] selectedIds)
    {
        var sensors = new ObservableCollection<SensorOption>();
        for (int i = 0; i < n; i++)
            sensors.Add(new SensorOption($"s{i}", $"Sensor {i}"));
        return new CurveEditRow("c1", "Kurve", selectedIds, SensorAggregation.Max, 0m, sensors);
    }

    [Fact]
    public void DisplayedSensors_CollapsedToThree_WhenManySensors()
    {
        CurveEditRow row = MakeRowWithSensorCount(6);
        Assert.True(row.HasCollapsibleSensors);
        Assert.Equal(3, row.DisplayedSensorChecks.Count);
    }

    [Fact]
    public void DisplayedSensors_ShowsAll_WhenFewSensors()
    {
        CurveEditRow row = MakeRowWithSensorCount(2);
        Assert.False(row.HasCollapsibleSensors);
        Assert.Equal(2, row.DisplayedSensorChecks.Count);
    }

    [Fact]
    public void DisplayedSensors_ActiveFirst_WhenCollapsed()
    {
        // s4 ist als Quelle ausgewählt → muss in der eingeklappten Ansicht ganz vorn stehen.
        CurveEditRow row = MakeRowWithSensorCount(6, "s4");
        Assert.Equal(3, row.DisplayedSensorChecks.Count);
        Assert.Equal("s4", row.DisplayedSensorChecks[0].Sensor.Id);
    }

    [Fact]
    public void ToggleShowAll_RevealsAndCollapses_WithLabel()
    {
        CurveEditRow row = MakeRowWithSensorCount(6);
        Assert.Equal("Alle 6 einblenden", row.ToggleSensorsLabel);

        row.ToggleShowAllSensorsCommand.Execute(null);
        Assert.True(row.ShowAllSensors);
        Assert.Equal(6, row.DisplayedSensorChecks.Count);
        Assert.Equal("Weniger anzeigen", row.ToggleSensorsLabel);

        row.ToggleShowAllSensorsCommand.Execute(null);
        Assert.Equal(3, row.DisplayedSensorChecks.Count);
        Assert.Equal("Alle 6 einblenden", row.ToggleSensorsLabel);
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
    public void RebuildSensorChecks_KeepsHiddenSource_AndNotifiesCollapseGetters()
    {
        CurveEditRow row = MakeRowWithSensorCount(4, "s0");
        Assert.True(row.HasCollapsibleSensors);

        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        // Hide the selected source plus one more sensor: the source survives, the other drops out,
        // and the count crosses the collapse threshold (4 → 3).
        row.Sensors.First(s => s.Id == "s0").Visible = false;
        row.Sensors.First(s => s.Id == "s1").Visible = false;
        row.RebuildSensorChecks();

        Assert.Equal(3, row.SensorChecks.Count);
        Assert.True(row.SensorChecks.Single(c => c.Sensor.Id == "s0").Selected);
        Assert.DoesNotContain(row.SensorChecks, c => c.Sensor.Id == "s1");
        Assert.False(row.HasCollapsibleSensors);
        Assert.Contains(nameof(CurveEditRow.HasCollapsibleSensors), notified);
        Assert.Contains(nameof(CurveEditRow.ToggleSensorsLabel), notified);
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
