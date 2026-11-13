// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// <see cref="FanControlResolver"/>: the overview's "Controlled by" follows the state the daemon reported per fan - one
/// test per state, plus the cases where nothing is known (offline, no report).
/// </summary>
public sealed class FanControlResolverTests
{
    private static readonly CurveConfig Quiet = new() { Id = "c1", Name = "Quiet" };

    private static MonitorSnapshot Snapshot(bool dryRun = false) => new(
        "ok", Array.Empty<SensorReading>(), Array.Empty<FanReading>(),
        new AppConfig { Curves = new[] { Quiet } }, Connected: true, DryRun: dryRun);

    private static FanReading Fan(FanControlState? state, string? curveId = null) =>
        new("pwm1", "CPU Fan", 1200, 128, CanControl: true, ControlState: state, ControlCurveId: curveId);

    private static FanControl Resolve(FanControlState? state, string? curveId = null, bool connected = true, bool dryRun = false) =>
        FanControlResolver.Resolve(Fan(state, curveId), Snapshot(dryRun), connected);

    [Fact]
    public void NoReport_IsUnknown()
    {
        FanControl control = Resolve(null);

        Assert.Null(control.State);
        Assert.Equal("unbekannt", control.Label);
        Assert.False(control.OpensCurve);
    }

    [Fact]
    public void Offline_IsUnknown_WhateverTheLastReportSaid()
    {
        FanControl control = Resolve(FanControlState.Curve, "c1", connected: false);

        Assert.Null(control.State);
        Assert.Equal("unbekannt", control.Label);
        Assert.Equal(ControlTone.Muted, control.Tone);
    }

    [Fact]
    public void Curve_IsAChipThatOpensTheCurve()
    {
        FanControl control = Resolve(FanControlState.Curve, "c1");

        Assert.Equal("Quiet", control.Label);
        Assert.Equal("c1", control.CurveId);
        Assert.True(control.OpensCurve);
        Assert.Equal(ControlTone.Normal, control.Tone);
    }

    [Fact]
    public void Curve_InADryRun_SaysSo()
    {
        FanControl control = Resolve(FanControlState.Curve, "c1", dryRun: true);

        Assert.Equal("Quiet (Dry-Run)", control.Label);
        Assert.True(control.OpensCurve);
        Assert.Contains("nicht geschrieben", control.Tooltip);
    }

    [Fact]
    public void NotControllable_SaysWhy_WhereTheDaemonKnowsIt()
    {
        var locked = new FanReading("pwm1", "Fan", 2100, 0, CanControl: false,
            ControlState: FanControlState.NotControllable, ReadOnlyReason: FanReadOnlyReason.DriverLocked);

        FanControl control = FanControlResolver.Resolve(locked, Snapshot(), connected: true);

        Assert.Equal("Nicht steuerbar", control.Label);
        Assert.Equal(ReadOnlyReasonText.Of(FanReadOnlyReason.DriverLocked), control.Tooltip);
        Assert.Equal("Dieser Lüfter liefert nur Messwerte.", Resolve(FanControlState.NotControllable).Tooltip); // unknown
    }

    [Fact]
    public void FailSafe_InADryRun_SaysNothingIsWritten()
    {
        FanControl control = Resolve(FanControlState.FailSafe, dryRun: true);

        Assert.Equal(FanControlState.FailSafe, control.State);
        Assert.Equal("Fail-Safe (Dry-Run)", control.Label);
        Assert.Equal(ControlTone.Danger, control.Tone);
        Assert.Contains("nichts geschrieben", control.Tooltip);
        Assert.DoesNotContain("Hardware-Auto", control.Tooltip);
    }

    [Fact]
    public void Curve_TheConfigDoesNotKnow_ShowsItsId()
    {
        Assert.Equal("gone", Resolve(FanControlState.Curve, "gone").Label);
    }

    [Fact]
    public void CurveHolding_IsAWarningChip_ThatSaysTheValueIsHeld()
    {
        FanControl control = Resolve(FanControlState.CurveHolding, "c1");

        Assert.Equal("Quiet (hält letzten Wert)", control.Label);
        Assert.Equal(ControlTone.Warning, control.Tone);
        Assert.True(control.OpensCurve);
        Assert.DoesNotContain("Hardware-Auto", control.Tooltip);
    }

    [Theory]
    [InlineData(FanControlState.Curve)]
    [InlineData(FanControlState.CurveHolding)]
    public void CurveStates_WithoutTheirCurve_OpenNothing(FanControlState state)
    {
        FanControl control = Resolve(state);

        Assert.False(control.OpensCurve);
        Assert.Equal(state, control.State);
    }

    [Fact]
    public void Manual()
    {
        FanControl control = Resolve(FanControlState.Manual);

        Assert.Equal("Manuell", control.Label);
        Assert.Equal(ControlTone.Warning, control.Tone);
        Assert.False(control.OpensCurve);
    }

    [Fact]
    public void HardwareAuto_WithoutACurve_SaysThereIsNone()
    {
        FanControl control = Resolve(FanControlState.HardwareAuto);

        Assert.Equal("Hardware-Auto", control.Label);
        Assert.Equal("Keine aktive Kurve - das Mainboard regelt selbst.", control.Tooltip);
        Assert.False(control.OpensCurve);
    }

    [Fact]
    public void HardwareAuto_WithACurve_NamesTheSwitchedOffCurve()
    {
        FanControl control = Resolve(FanControlState.HardwareAuto, "c1");

        Assert.Equal("Hardware-Auto", control.Label);
        Assert.Equal("Kurve „Quiet“ ist aus - das Mainboard regelt selbst.", control.Tooltip);
        Assert.False(control.OpensCurve); // the curve is off - it controls nothing
    }

    [Theory]
    [InlineData(FanControlState.Calibrating, "Kalibrierung", ControlTone.Accent)]
    [InlineData(FanControlState.Identifying, "Identifizieren (100 %)", ControlTone.Accent)]
    [InlineData(FanControlState.IdentifyThrottled, "Gedrosselt (Identifizieren)", ControlTone.Accent)]
    [InlineData(FanControlState.Coupling, "Tacho-Kopplung", ControlTone.Accent)]
    [InlineData(FanControlState.CouplingThrottled, "Gedrosselt (Tacho-Kopplung)", ControlTone.Accent)]
    [InlineData(FanControlState.FailSafe, "Fail-Safe", ControlTone.Danger)]
    [InlineData(FanControlState.Failed, "Fehler", ControlTone.Danger)]
    [InlineData(FanControlState.NotControllable, "Nicht steuerbar", ControlTone.Muted)]
    public void OtherStates_HaveTheirLabelAndTone(FanControlState state, string label, ControlTone tone)
    {
        FanControl control = Resolve(state);

        Assert.Equal(state, control.State);
        Assert.Equal(label, control.Label);
        Assert.Equal(tone, control.Tone);
        Assert.False(control.OpensCurve);
    }

    [Fact]
    public void EveryState_HasATextOfItsOwn()
    {
        // A state added to the daemon without a text here would show its resource key (or "unknown").
        foreach (FanControlState state in Enum.GetValues<FanControlState>())
        {
            FanControl control = Resolve(state, "c1");
            Assert.Equal(state, control.State);
            Assert.DoesNotContain("Overview.", control.Label);
            Assert.DoesNotContain("Overview.", control.Tooltip);
        }
    }
}
