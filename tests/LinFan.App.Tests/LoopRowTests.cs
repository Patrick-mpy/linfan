// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// <see cref="LoopRow"/> - one control loop of the overview: source summary, input and output from the live curve,
/// the mini curve's samples, and the switch/edit callbacks.
/// </summary>
public sealed class LoopRowTests
{
    private static CurveEditRow Curve(bool enabled = true, InterpolationMode mode = InterpolationMode.Linear,
                                      SensorAggregation aggregation = SensorAggregation.Max, params string[] sourceIds)
    {
        var sensors = new ObservableCollection<SensorOption>();
        foreach (string id in sourceIds)
            sensors.Add(new SensorOption(id, id, visible: true, group: null, unit: "°C"));
        var curve = new CurveEditRow("c1", "Quiet", sourceIds, aggregation, 2m, sensors, mode) { Enabled = enabled };
        curve.AddPointRow(30, 20);
        curve.AddPointRow(70, 100);
        return curve;
    }

    private static LoopRow Row(CurveEditRow curve, Action<bool>? toggle = null, Action? edit = null) =>
        new(curve, Array.Empty<FanRow>(), new ObservableCollection<double>(), toggle ?? (_ => { }), edit ?? (() => { }));

    [Fact]
    public void Toggling_Enabled_InvokesCallback()
    {
        bool? toggled = null;
        LoopRow row = Row(Curve(sourceIds: "t1"), e => toggled = e);

        Assert.True(row.Enabled);
        row.Enabled = false;

        Assert.False(toggled);
    }

    [Fact]
    public void EditCommand_InvokesCallback()
    {
        bool edited = false;
        LoopRow row = Row(Curve(sourceIds: "t1"), edit: () => edited = true);

        row.EditCommand.Execute(null);

        Assert.True(edited);
    }

    [Fact]
    public void Summary_NamesTheSensor_OrCountsThemWithTheMix()
    {
        Assert.Equal("t1", Row(Curve(sourceIds: "t1")).Summary);
        Assert.Equal("2 Sensoren · Maximum", Row(Curve(sourceIds: new[] { "t1", "t2" })).Summary);
        Assert.Equal("2 Sensoren · Mittelwert",
            Row(Curve(aggregation: SensorAggregation.Avg, sourceIds: new[] { "t1", "t2" })).Summary);
    }

    [Fact]
    public void Refresh_EvaluatesTheCurveAtTheLiveInput()
    {
        CurveEditRow curve = Curve(sourceIds: "t1");
        LoopRow row = Row(curve);
        curve.LiveTemperature = 50; // halfway between 30 → 20 % and 70 → 100 %

        row.Refresh(canToggle: true, daemonEnabled: true);

        Assert.Equal(50, row.Temperature);
        Assert.Equal(60, row.OutputPercent, 3);
        Assert.Equal("≈ 60 %", row.OutputText);
        Assert.Equal("50 °C", row.TemperatureText);
        Assert.True(row.CanToggle);
    }

    [Fact]
    public void NoReadableInput_ShowsADash_AndNoDot()
    {
        CurveEditRow curve = Curve(sourceIds: "t1");
        LoopRow row = Row(curve);
        curve.LiveTemperature = double.NaN;

        row.Refresh(canToggle: false, daemonEnabled: true);

        Assert.Equal("-", row.TemperatureText);
        Assert.Equal("-", row.OutputText);
        Assert.True(double.IsNaN(row.DotTemperature));
        Assert.False(row.CanToggle);
    }

    [Fact]
    public void Disabled_SaysOff()
    {
        CurveEditRow curve = Curve(enabled: false, sourceIds: "t1");
        LoopRow row = Row(curve);
        curve.LiveTemperature = 50;

        row.Refresh(canToggle: true, daemonEnabled: false);

        Assert.Equal("aus", row.OutputText);
    }

    /// <summary>
    /// The row's texts are computed getters - a language switch reaches the view only through the change notification
    /// the next tick's <see cref="LoopRow.Refresh"/> raises (the overview ticks offline too).
    /// </summary>
    [Fact]
    public void ALanguageSwitch_ReachesTheTexts_WithTheNextTick()
    {
        CurveEditRow on = Curve(aggregation: SensorAggregation.Avg, sourceIds: new[] { "t1", "t2" });
        CurveEditRow off = Curve(enabled: false, sourceIds: "t1");
        LoopRow onRow = Row(on);
        LoopRow offRow = Row(off);
        on.LiveTemperature = 50;
        var raised = new List<string?>();
        onRow.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        offRow.PropertyChanged += (_, e) => raised.Add("off." + e.PropertyName);

        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);
            onRow.Refresh(canToggle: true, daemonEnabled: true);
            offRow.Refresh(canToggle: true, daemonEnabled: false);

            Assert.Equal("2 sensors · Average", onRow.Summary);
            Assert.Equal("Curve “Quiet” on/off", onRow.SwitchName);
            Assert.Equal("≈ 60 %", onRow.OutputText);
            Assert.Equal("off", offRow.OutputText);
            Assert.Contains(nameof(LoopRow.Summary), raised);
            Assert.Contains(nameof(LoopRow.SwitchName), raised);
            Assert.Contains(nameof(LoopRow.OutputText), raised);
            Assert.Contains("off." + nameof(LoopRow.OutputText), raised);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // restore the pinned test culture
        }

        onRow.Refresh(canToggle: true, daemonEnabled: true);
        Assert.Equal("2 Sensoren · Mittelwert", onRow.Summary);
        Assert.Equal("Kurve „Quiet“ an/aus", onRow.SwitchName);
    }

    [Fact]
    public void Hot_FromTheHotMark()
    {
        CurveEditRow curve = Curve(sourceIds: "t1");
        LoopRow row = Row(curve);

        curve.LiveTemperature = 79.9;
        row.Refresh(canToggle: true, daemonEnabled: true);
        Assert.False(row.IsHot);

        curve.LiveTemperature = 80;
        row.Refresh(canToggle: true, daemonEnabled: true);
        Assert.True(row.IsHot);
    }

    [Fact]
    public void Dot_StaysInsideTheDrawnRange()
    {
        CurveEditRow curve = Curve(sourceIds: "t1");
        LoopRow row = Row(curve);
        curve.LiveTemperature = 120;

        row.Refresh(canToggle: true, daemonEnabled: true);

        Assert.Equal(100, row.DotTemperature);
        Assert.Equal(100, row.OutputPercent, 3); // above the last point: its value
    }

    [Fact]
    public void Samples_FollowTheCurveEngine_SoStepsStaySteps()
    {
        LoopRow row = Row(Curve(mode: InterpolationMode.Step, sourceIds: "t1"));

        Assert.Equal(LoopRow.SampleCount, row.CurveSamples.Count);
        Assert.Equal(20, row.CurveSamples[30], 3);
        Assert.Equal(20, row.CurveSamples[69], 3); // a step holds until the next point - linear would be ~98 here
        Assert.Equal(100, row.CurveSamples[70], 3);
    }

    [Fact]
    public void TheDaemonsState_SetsTheSwitch_WithoutSendingAnything()
    {
        var toggles = new List<bool>();
        LoopRow row = Row(Curve(sourceIds: "t1"), toggles.Add);

        row.Refresh(canToggle: true, daemonEnabled: false); // switched off elsewhere (another window, the daemon)

        Assert.False(row.Enabled);
        Assert.Empty(toggles);
    }

    [Fact]
    public void AToggle_IsHeldBriefly_ThenTheDaemonWins_IfItNeverConfirms()
    {
        var toggles = new List<bool>();
        LoopRow row = Row(Curve(sourceIds: "t1"), toggles.Add);

        row.Enabled = false; // the user switches off; the command gets lost
        Assert.Equal(new[] { false }, toggles);
        for (int tick = 0; tick < 3; tick++)
        {
            row.Refresh(canToggle: true, daemonEnabled: true); // the daemon's config may lag a tick or two
            Assert.False(row.Enabled);
        }

        row.Refresh(canToggle: true, daemonEnabled: true);

        Assert.True(row.Enabled);            // the lost command shows instead of hiding
        Assert.Equal(new[] { false }, toggles); // and correcting the switch sends nothing
    }

    [Fact]
    public void AConfirmedToggle_Stays()
    {
        LoopRow row = Row(Curve(sourceIds: "t1"));

        row.Enabled = false;
        row.Refresh(canToggle: true, daemonEnabled: false);
        row.Refresh(canToggle: true, daemonEnabled: false);

        Assert.False(row.Enabled);
    }

    [Fact]
    public void Rebind_KeepsTheRow_AndTakesTheRebuiltCurve()
    {
        var history = new ObservableCollection<double> { 40, 41 };
        CurveEditRow before = Curve(sourceIds: "t1");
        var row = new LoopRow(before, Array.Empty<FanRow>(), history, _ => { }, () => { });
        CurveEditRow after = Curve(mode: InterpolationMode.Step, sourceIds: "t1");
        after.LiveTemperature = 69;

        row.Rebind(after, Array.Empty<FanRow>());

        Assert.Same(after, row.Curve);
        Assert.Same(history, row.History);
        Assert.Equal(20, row.CurveSamples[69], 3); // the new curve's shape
        Assert.Equal(69, row.Temperature);
    }
}
