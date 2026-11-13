// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// <see cref="OverviewHints"/> - every rule of the overview's hint list, its order and edge cases - and the status tile
/// built from it (<see cref="OverviewStatus"/>).
/// </summary>
public sealed class OverviewHintsTests
{
    private static readonly CurveConfig Quiet = new()
    {
        Id = "c1",
        Name = "Quiet",
        SourceSensorIds = new[] { "t1" },
        Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
    };

    private static readonly FanCalibration Calibrated = new() { StartPwm = 51, MinRpm = 300, MaxRpm = 1800 }; // 20 %

    private static MonitorSnapshot Snap(
        SensorReading[]? sensors = null, FanReading[]? fans = null,
        SensorConfig[]? sensorConfigs = null, FanConfig[]? fanConfigs = null, CurveConfig[]? curves = null,
        bool dryRun = false, FailSafeState? failSafe = null, CalibrationStatus? calibration = null) =>
        new("ok",
            sensors ?? Array.Empty<SensorReading>(),
            fans ?? Array.Empty<FanReading>(),
            new AppConfig
            {
                Sensors = sensorConfigs ?? Array.Empty<SensorConfig>(),
                Fans = fanConfigs ?? Array.Empty<FanConfig>(),
                Curves = curves ?? Array.Empty<CurveConfig>(),
            },
            Connected: true, Calibration: calibration, DryRun: dryRun, FailSafe: failSafe);

    private static SensorReading Temp(string id, double value, string? name = null) =>
        new(id, name ?? id, SensorKind.Temperature, "°C", value);

    private static FanReading Fan(string id, double? rpm = 1200, byte pwm = 128, bool canControl = true,
                                  bool manual = false, FanControlState? state = null, string? curveId = null) =>
        new(id, id, rpm, pwm, canControl, manual, state, curveId);

    private static FanConfig Cfg(string id, string? curve = null, bool hidden = false, FanCalibration? calibration = null) =>
        new() { FanId = id, AssignedCurveId = curve, Hidden = hidden, Calibration = calibration ?? Calibrated };

    private static IReadOnlyList<OverviewHint> Build(MonitorSnapshot snapshot, ConnectionProblem problem = ConnectionProblem.None) =>
        OverviewHints.Build(snapshot, problem);

    [Fact]
    public void NothingToSay_NoHints()
    {
        Assert.Empty(Build(Snap(new[] { Temp("t1", 45) }, new[] { Fan("f1") }, fanConfigs: new[] { Cfg("f1") })));
    }

    // --- connection, dry run, fail-safe -----------------------------------------------------------

    [Theory]
    [InlineData(ConnectionProblem.Unreachable)]
    [InlineData(ConnectionProblem.AccessDenied)]
    public void Offline_IsCritical(ConnectionProblem problem)
    {
        OverviewHint hint = Assert.Single(Build(Snap(), problem));

        Assert.Equal(HintSeverity.Critical, hint.Severity);
        Assert.Equal("Hintergrunddienst nicht erreichbar - Steuerung und Speichern sind gesperrt.", hint.Text);
        Assert.False(hint.HasAction);
    }

    [Fact]
    public void Connecting_IsNoHint() => Assert.Empty(Build(Snap(), ConnectionProblem.Connecting));

    [Fact]
    public void Offline_ShowsOnlyTheOfflineHint_TheFrozenStateCouldBeStale()
    {
        MonitorSnapshot frozen = Snap(new[] { Temp("t1", 90) }, new[] { Fan("f1", rpm: 0, pwm: 200, manual: true) },
            fanConfigs: new[] { new FanConfig { FanId = "f1" } }, failSafe: FailSafeState.OverTemperature);

        OverviewHint hint = Assert.Single(Build(frozen, ConnectionProblem.Unreachable));

        Assert.Equal("offline", hint.Key);
    }

    [Fact]
    public void UnreadableData_ShowsOnlyItsOwnHint_TheFrozenStateCouldBeStale()
    {
        MonitorSnapshot frozen = Snap(new[] { Temp("t1", 90) }, new[] { Fan("f1", rpm: 0, pwm: 200, manual: true) },
            fanConfigs: new[] { new FanConfig { FanId = "f1" } }, failSafe: FailSafeState.OverTemperature, dryRun: true);

        OverviewHint hint = Assert.Single(Build(frozen, ConnectionProblem.UnreadableData));

        Assert.Equal("unreadable", hint.Key);
        Assert.Equal(HintSeverity.Critical, hint.Severity);
        Assert.Equal("Daten des Hintergrunddienstes nicht lesbar - Steuerung und Speichern sind gesperrt.", hint.Text);
        Assert.False(hint.HasAction);
    }

    [Fact]
    public void DryRun_IsANotice_OnlyWhileConnected()
    {
        OverviewHint hint = Assert.Single(Build(Snap(dryRun: true)));
        Assert.Equal(HintSeverity.Notice, hint.Severity);
        Assert.Equal("Dry-Run - die Regelung schreibt keine Werte an die Lüfter.", hint.Text);

        // Offline the banner says the more important thing; the dry run of the frozen state does not count.
        Assert.DoesNotContain(Build(Snap(dryRun: true), ConnectionProblem.Unreachable), h => h.Text.StartsWith("Dry-Run"));
    }

    [Theory]
    [InlineData(FailSafeState.OverTemperature, "Fail-Safe aktiv - Übertemperatur: alle Lüfter auf Hardware-Auto.")]
    [InlineData(FailSafeState.Blind, "Fail-Safe aktiv - Temperaturen nicht überwachbar (ein Sensor liefert keine Werte mehr): alle Lüfter auf Hardware-Auto.")]
    [InlineData(FailSafeState.Error,
        "Fail-Safe aktiv - der Regelkreis des Dienstes ist fehlgeschlagen: alle Lüfter auf Hardware-Auto. Details im Dienst-Log.")]
    public void FailSafe_IsCritical(FailSafeState state, string text)
    {
        OverviewHint hint = Assert.Single(Build(Snap(failSafe: state)));

        Assert.Equal(HintSeverity.Critical, hint.Severity);
        Assert.Equal(text, hint.Text);
    }

    /// <summary>A dry run reports the watchdog's verdict but writes nothing - the hint must not claim hardware auto.</summary>
    [Theory]
    [InlineData(FailSafeState.OverTemperature, "Übertemperatur - im Dry-Run greift der Fail-Safe nicht ein.")]
    [InlineData(FailSafeState.Blind,
        "Temperaturen nicht überwachbar (ein Sensor liefert keine Werte mehr) - im Dry-Run greift der Fail-Safe nicht ein.")]
    [InlineData(FailSafeState.Error,
        "Der Regelkreis des Dienstes ist fehlgeschlagen - im Dry-Run greift der Fail-Safe nicht ein. Details im Dienst-Log.")]
    public void FailSafe_InADryRun_IsCritical_ButSaysNothingIsWritten(FailSafeState state, string text)
    {
        IReadOnlyList<OverviewHint> hints = Build(Snap(dryRun: true, failSafe: state));

        Assert.Equal(2, hints.Count);
        Assert.Equal("failSafe", hints[0].Key);
        Assert.Equal(HintSeverity.Critical, hints[0].Severity);
        Assert.Equal(text, hints[0].Text);
        Assert.Equal("dryRun", hints[1].Key);
    }

    [Theory]
    [InlineData(FailSafeState.None)]
    [InlineData(null)]
    public void NoFailSafe_OrNoReport_IsNoHint(FailSafeState? state) => Assert.Empty(Build(Snap(failSafe: state)));

    [Fact]
    public void DriverLock_IsANotice_ThatNamesTheFix_OnceForAllLockedFans()
    {
        FanReading Locked(string id) => new(id, id, 2100, 0, CanControl: false,
            ReadOnlyReason: FanReadOnlyReason.DriverLocked);

        OverviewHint hint = Assert.Single(Build(Snap(fans: new[] { Locked("a"), Locked("b") })));

        Assert.Equal("driverLocked", hint.Key);
        Assert.Equal(HintSeverity.Notice, hint.Severity);
        Assert.Contains("fan_control=1", hint.Text);
    }

    [Fact]
    public void ConfigSetAside_IsANotice_ThatSaysWhereTheFileWent()
    {
        OverviewHint hint = Assert.Single(Build(Snap() with
        {
            ConfigProblem = ConfigLoadProblem.SetAside,
            ConfigSetAsidePath = "/etc/linfan/config.json.invalid",
        }));

        Assert.Equal(("configProblem", HintSeverity.Notice), (hint.Key, hint.Severity));
        Assert.Equal("Die gespeicherte Konfiguration war unbrauchbar und liegt jetzt unter /etc/linfan/config.json.invalid "
                     + "- der Dienst läuft ohne Kurven, bis du LinFan neu einrichtest.", hint.Text);
    }

    [Fact]
    public void ConfigNotLoaded_WarnsThatTheNextChangeOverwritesTheFile()
    {
        OverviewHint hint = Assert.Single(Build(Snap() with { ConfigProblem = ConfigLoadProblem.NotLoaded }));

        Assert.Equal("configProblem", hint.Key);
        Assert.Contains("überschreibt die Datei", hint.Text);
    }

    [Fact]
    public void ReadOnly_ForLackOfRights_OrHardware_IsNoHint()
    {
        FanReading ReadOnly(string id, FanReadOnlyReason reason) => new(id, id, 2100, 0, CanControl: false,
            ReadOnlyReason: reason);

        Assert.Empty(Build(Snap(fans: new[]
        {
            ReadOnly("a", FanReadOnlyReason.NoWriteAccess), ReadOnly("b", FanReadOnlyReason.NoControlInterface),
        })));
    }

    // --- sensors ----------------------------------------------------------------------------------

    [Fact]
    public void HotSensor_IsCritical_AndOpensItsGroup()
    {
        IReadOnlyList<OverviewHint> hints = Build(Snap(
            new[] { Temp("t1", 85.25, "Tctl"), Temp("t2", 90, "Edge") },
            sensorConfigs: new[] { new SensorConfig { SensorId = "t1", Group = " CPU " } }));

        Assert.Equal(2, hints.Count);
        Assert.Equal("Tctl ist heiß (85.3 °C).", hints[0].Text);
        Assert.Equal(HintSeverity.Critical, hints[0].Severity);
        Assert.Equal(HintAction.ShowTemperatureGroup, hints[0].Action);
        Assert.Equal("CPU", hints[0].Target);       // trimmed group key
        Assert.Equal("", hints[1].Target);          // ungrouped
        Assert.Equal("Anzeigen", hints[0].ActionLabel);
        Assert.False(hints[0].NeedsDaemon);
    }

    [Fact]
    public void BelowTheMark_Hidden_OrUnreadable_IsNotHot()
    {
        Assert.Empty(Build(Snap(
            new[] { Temp("t1", 79.9), Temp("t2", 95), Temp("t3", double.NaN) },
            sensorConfigs: new[] { new SensorConfig { SensorId = "t2", Hidden = true } })));
    }

    // --- fans -------------------------------------------------------------------------------------

    [Fact]
    public void Standstill_AboveTheThreshold_IsCritical()
    {
        OverviewHint hint = Assert.Single(Build(Snap(fans: new[] { Fan("f1", rpm: 0, pwm: 128) }, fanConfigs: new[] { Cfg("f1") })));

        Assert.Equal(HintSeverity.Critical, hint.Severity);
        Assert.Equal("f1 meldet 0 RPM bei 50 % - steht der Lüfter?", hint.Text);
    }

    [Theory]
    [InlineData(null, (byte)128)]  // no tachometer
    [InlineData(0.0, (byte)51)]    // 20 % - not above the threshold
    [InlineData(400.0, (byte)128)] // turning
    public void NoStandstill(double? rpm, byte pwm) =>
        Assert.Empty(Build(Snap(fans: new[] { Fan("f1", rpm, pwm) }, fanConfigs: new[] { Cfg("f1") })));

    [Fact]
    public void Standstill_UsesTheCalibratedStartPoint_WhenHigher()
    {
        var late = new FanCalibration { StartPwm = 128, MinRpm = 300, MaxRpm = 1800 }; // starts at 50 %
        Assert.Empty(Build(Snap(fans: new[] { Fan("f1", 0, pwm: 102) }, fanConfigs: new[] { Cfg("f1", calibration: late) }))); // 40 %
        Assert.Single(Build(Snap(fans: new[] { Fan("f1", 0, pwm: 153) }, fanConfigs: new[] { Cfg("f1", calibration: late) }))); // 60 %
        Assert.Equal(50, OverviewHints.StandstillThreshold(Cfg("f1", calibration: late)));
        Assert.Equal(20, OverviewHints.StandstillThreshold(null));
    }

    [Fact]
    public void Standstill_IsSilent_WhileADeviceActionRuns_OrForReadOnlyFans()
    {
        var running = new CalibrationStatus("f2", CalibrationPhase.Measuring, 0, 0, Running: true, Done: false, StartPwm: null, FailReason: null);
        Assert.Empty(Build(Snap(fans: new[] { Fan("f1", 0, 200) }, fanConfigs: new[] { Cfg("f1") }, calibration: running)));
        Assert.Empty(Build(Snap(fans: new[] { Fan("f1", 0, 200, state: FanControlState.IdentifyThrottled) }, fanConfigs: new[] { Cfg("f1") })));
        Assert.Empty(Build(Snap(fans: new[] { Fan("f1", 0, 200, canControl: false) }, fanConfigs: new[] { Cfg("f1") })));
    }

    [Fact]
    public void Failed_IsCritical_ForHiddenFansToo()
    {
        OverviewHint hint = Assert.Single(Build(Snap(
            fans: new[] { Fan("f1", state: FanControlState.Failed) }, fanConfigs: new[] { Cfg("f1", hidden: true) })));

        Assert.Equal(HintSeverity.Critical, hint.Severity);
        Assert.StartsWith("f1: Die Steuerung ließ sich zuletzt nicht ausführen", hint.Text);
    }

    [Fact]
    public void Manual_OverridingACurve_OffersBackToTheCurve()
    {
        OverviewHint hint = Assert.Single(Build(Snap(
            new[] { Temp("t1", 40) }, new[] { Fan("f1", manual: true) },
            fanConfigs: new[] { Cfg("f1", "c1") }, curves: new[] { Quiet })));

        Assert.Equal(HintSeverity.Notice, hint.Severity);
        Assert.Equal("f1 wird manuell gesteuert und übersteuert „Quiet“.", hint.Text);
        Assert.Equal(HintAction.BackToCurve, hint.Action);
        Assert.Equal("f1", hint.Target);
        Assert.Equal("Zurück zur Kurve", hint.ActionLabel);
        Assert.True(hint.NeedsDaemon);
    }

    [Fact]
    public void Manual_WithoutACurve_OffersBackToAuto()
    {
        OverviewHint hint = Assert.Single(Build(Snap(fans: new[] { Fan("f1", manual: true) }, fanConfigs: new[] { Cfg("f1") })));

        Assert.Equal("f1 wird manuell gesteuert.", hint.Text);
        Assert.Equal("Zurück auf Auto", hint.ActionLabel);
    }

    [Fact]
    public void Uncalibrated_CountsVisibleControllableFans()
    {
        OverviewHint hint = Assert.Single(Build(Snap(
            fans: new[] { Fan("f1"), Fan("f2"), Fan("f3"), Fan("ro", canControl: false) },
            fanConfigs: new[]
            {
                new FanConfig { FanId = "f1" },
                new FanConfig { FanId = "f3", Hidden = true },
                new FanConfig { FanId = "ro" },
            })));

        Assert.Equal(HintSeverity.Info, hint.Severity);
        Assert.Equal("2 Lüfter sind noch nicht kalibriert.", hint.Text); // f1 and f2 (no config at all)
        Assert.Equal(HintAction.ShowFans, hint.Action); // calibrating lives in the fan table's rows
        Assert.Equal("Zu den Lüftern", hint.ActionLabel);
    }

    /// <summary>A dry run refuses to calibrate - the hint would lead to a locked button.</summary>
    [Fact]
    public void Uncalibrated_IsNoHint_InADryRun()
    {
        IReadOnlyList<OverviewHint> hints = Build(Snap(fans: new[] { Fan("f1") }, fanConfigs: new[] { new FanConfig { FanId = "f1" } },
            dryRun: true));

        Assert.DoesNotContain(hints, h => h.Key == "uncalibrated");
    }

    [Fact]
    public void Uncalibrated_One()
    {
        Assert.Equal("1 Lüfter ist noch nicht kalibriert.",
            Assert.Single(Build(Snap(fans: new[] { Fan("f1") }, fanConfigs: new[] { new FanConfig { FanId = "f1" } }))).Text);
    }

    // --- curves -----------------------------------------------------------------------------------

    [Fact]
    public void CurveOff_WithFans_OffersToSwitchItOn()
    {
        CurveConfig off = Quiet with { Enabled = false };
        OverviewHint one = Assert.Single(Build(Snap(new[] { Temp("t1", 40) }, new[] { Fan("f1") },
            fanConfigs: new[] { Cfg("f1", "c1") }, curves: new[] { off })));
        Assert.Equal(HintSeverity.Info, one.Severity);
        Assert.Equal("Kurve „Quiet“ ist aus - 1 Lüfter läuft auf Hardware-Auto.", one.Text);
        Assert.Equal(HintAction.EnableCurve, one.Action);
        Assert.Equal("c1", one.Target);
        Assert.True(one.NeedsDaemon);

        OverviewHint two = Assert.Single(Build(Snap(new[] { Temp("t1", 40) }, new[] { Fan("f1"), Fan("f2") },
            fanConfigs: new[] { Cfg("f1", "c1"), Cfg("f2", "c1", hidden: true) }, curves: new[] { off })));
        Assert.Equal("Kurve „Quiet“ ist aus - 2 Lüfter laufen auf Hardware-Auto.", two.Text);

        Assert.Empty(Build(Snap(new[] { Temp("t1", 40) }, curves: new[] { off }))); // off without fans: nothing runs differently
    }

    [Fact]
    public void CurveWithoutSensor_IsANotice()
    {
        CurveConfig bare = Quiet with { SourceSensorIds = Array.Empty<string>() };
        OverviewHint hint = Assert.Single(Build(Snap(fans: new[] { Fan("f1", state: FanControlState.CurveHolding, curveId: "c1") },
            fanConfigs: new[] { Cfg("f1", "c1") }, curves: new[] { bare })));

        // One hint per curve: the missing sensor explains the held value already.
        Assert.Equal(HintSeverity.Notice, hint.Severity);
        Assert.Equal("Kurve „Quiet“ hat keinen Sensor - ihre Lüfter halten den letzten Wert.", hint.Text);
        Assert.Equal(HintAction.OpenCurve, hint.Action);
        Assert.Equal("Zur Kurve", hint.ActionLabel);
    }

    [Fact]
    public void CurveHolding_AsReported_IsCritical()
    {
        OverviewHint hint = Assert.Single(Build(Snap(new[] { Temp("t1", double.NaN) },
            new[] { Fan("f1", state: FanControlState.CurveHolding, curveId: "c1") },
            fanConfigs: new[] { Cfg("f1", "c1") }, curves: new[] { Quiet })));

        Assert.Equal(HintSeverity.Critical, hint.Severity);
        Assert.Equal("Kurve „Quiet“: kein lesbarer Sensor - Lüfter halten den letzten Wert.", hint.Text);
        Assert.Equal("c1", hint.Target);
    }

    [Fact]
    public void UnreadableInput_WithoutTheDaemonsReport_IsNoHint()
    {
        // The GUI does not guess: without the daemon saying "holding" there is no such hint.
        Assert.Empty(Build(Snap(new[] { Temp("t1", double.NaN) }, new[] { Fan("f1", state: FanControlState.Curve, curveId: "c1") },
            fanConfigs: new[] { Cfg("f1", "c1") }, curves: new[] { Quiet })));
    }

    [Fact]
    public void CurveWithoutFans_IsInfo()
    {
        OverviewHint hint = Assert.Single(Build(Snap(new[] { Temp("t1", 40) }, curves: new[] { Quiet })));

        Assert.Equal(HintSeverity.Info, hint.Severity);
        Assert.Equal("Kurve „Quiet“ steuert keinen Lüfter.", hint.Text);
    }

    // --- calibration check ------------------------------------------------------------------------

    private static FanReading Mismatched(string id, CalibrationMismatch mismatch, double? rpm = 1200, byte pwm = 128) =>
        Fan(id, rpm, pwm) with { CalibrationMismatch = mismatch };

    /// <summary>A fan that stands at its calibrated start point is no standstill by the threshold - only the daemon can tell.</summary>
    [Fact]
    public void Standstill_AtTheStartPoint_WhenTheDaemonSaysItStalls()
    {
        // 20 % = the calibrated start point: below it a fan is not meant to turn, at it the threshold says nothing.
        Assert.Empty(Build(Snap(fans: new[] { Fan("f1", rpm: 0, pwm: 51) }, fanConfigs: new[] { Cfg("f1") })));

        OverviewHint hint = Assert.Single(Build(Snap(fans: new[] { Mismatched("f1", CalibrationMismatch.Stalls, rpm: 0, pwm: 51) },
            fanConfigs: new[] { Cfg("f1") })));

        Assert.Equal(("standstill:f1", HintSeverity.Critical), (hint.Key, hint.Severity));
        Assert.Equal("f1 meldet 0 RPM bei 20 % - steht der Lüfter?", hint.Text);
    }

    [Fact]
    public void Standstill_OffersToRecalibrate_OrToCalibrateAnUncalibratedFan()
    {
        OverviewHint calibrated = Assert.Single(Build(Snap(fans: new[] { Fan("f1", rpm: 0, pwm: 128) }, fanConfigs: new[] { Cfg("f1") })));
        Assert.Equal(HintAction.Calibrate, calibrated.Action);
        Assert.Equal("f1", calibrated.Target);
        Assert.Equal("Neu kalibrieren", calibrated.ActionLabel);
        Assert.True(calibrated.NeedsDaemon);

        OverviewHint uncalibrated = Build(Snap(fans: new[] { Fan("f1", rpm: 0, pwm: 128) },
            fanConfigs: new[] { new FanConfig { FanId = "f1" } })).Single(h => h.Key == "standstill:f1");
        Assert.Equal("Kalibrieren", uncalibrated.ActionLabel);
    }

    [Theory]
    [InlineData(CalibrationMismatch.Slower,
        "f1 dreht deutlich langsamer, als die Kalibrierung erwarten lässt - wurde der Lüfter getauscht? Neu kalibrieren empfohlen.")]
    [InlineData(CalibrationMismatch.Faster,
        "f1 dreht deutlich schneller, als die Kalibrierung erwarten lässt - wurde der Lüfter getauscht? Neu kalibrieren empfohlen.")]
    [InlineData(CalibrationMismatch.OtherSensor,
        "f1: Die Kalibrierung wurde mit einem anderen Drehzahl-Sensor gemessen als dem aktuellen - neu kalibrieren empfohlen.")]
    public void CalibrationMismatch_IsANotice_ThatOffersToRecalibrate(CalibrationMismatch mismatch, string text)
    {
        OverviewHint hint = Assert.Single(Build(Snap(fans: new[] { Mismatched("f1", mismatch) }, fanConfigs: new[] { Cfg("f1") })));

        Assert.Equal(("calibration:f1", HintSeverity.Notice), (hint.Key, hint.Severity));
        Assert.Equal(text, hint.Text);
        Assert.Equal(HintAction.Calibrate, hint.Action);
        Assert.Equal("f1", hint.Target);
        Assert.Equal("Neu kalibrieren", hint.ActionLabel);
    }

    /// <summary>
    /// A fan whose current speed sensor is gone keeps the hint about its calibration, but no button the daemon would refuse -
    /// with a sensor it can measure with, the button is there.
    /// </summary>
    [Fact]
    public void CalibrationHint_OffersNoRecalibrate_ForAFanWithoutASpeedSensorToMeasureWith()
    {
        FanReading fan = Mismatched("f1", CalibrationMismatch.OtherSensor) with { Tachometer = "hwmon0/fan2" };
        MonitorSnapshot gone = Snap(fans: new[] { fan }, fanConfigs: new[] { Cfg("f1") }) with { DaemonVersion = "0.5.0+abc1234" };

        OverviewHint hint = Assert.Single(Build(gone));
        Assert.Equal("calibration:f1", hint.Key);
        Assert.Equal(HintAction.None, hint.Action);

        MonitorSnapshot detected = gone with
        {
            Sensors = new[] { new SensorReading("hwmon0/fan2", "Fan 2", SensorKind.FanRpm, "RPM", 1200) },
        };
        Assert.Equal(HintAction.Calibrate, Assert.Single(Build(detected)).Action);
    }

    /// <summary>Reading exactly 0 is the standstill hint's; a fan that still turns a little would otherwise go unmentioned.</summary>
    [Fact]
    public void Stalls_WhileTurningALittle_IsANotice()
    {
        OverviewHint hint = Assert.Single(Build(Snap(fans: new[] { Mismatched("f1", CalibrationMismatch.Stalls, rpm: 60, pwm: 51) },
            fanConfigs: new[] { Cfg("f1") })));

        Assert.Equal(("calibration:f1", HintSeverity.Notice), (hint.Key, hint.Severity));
        Assert.Equal("f1 dreht bei 20 % kaum, obwohl die Kalibrierung hier Drehung erwarten lässt - neu kalibrieren empfohlen.", hint.Text);
    }

    [Fact]
    public void Stalls_IsSilent_WhileADeviceActionRuns_LikeTheStandstill()
    {
        var running = new CalibrationStatus("f2", CalibrationPhase.Measuring, 0, 0, Running: true, Done: false, StartPwm: null, FailReason: null);

        Assert.Empty(Build(Snap(fans: new[] { Mismatched("f1", CalibrationMismatch.Stalls, rpm: 0, pwm: 51) },
            fanConfigs: new[] { Cfg("f1") }, calibration: running)));
    }

    /// <summary>
    /// The calibration lives in the fan's row; where it cannot start - a dry run, another device action - the hint stays
    /// but offers no button that would only be refused.
    /// </summary>
    [Fact]
    public void CalibrateAction_IsNotOffered_InADryRun_OrWhileADeviceActionRuns()
    {
        var identifying = new IdentifyStatus("f2", Running: true, FailReason: null);
        MonitorSnapshot slower = Snap(fans: new[] { Mismatched("f1", CalibrationMismatch.Slower) }, fanConfigs: new[] { Cfg("f1") });
        MonitorSnapshot busy = slower with { Identify = identifying };
        MonitorSnapshot dry = Snap(fans: new[] { Mismatched("f1", CalibrationMismatch.Faster) }, fanConfigs: new[] { Cfg("f1") },
            dryRun: true);

        OverviewHint whileBusy = Assert.Single(Build(busy));
        OverviewHint inADryRun = Build(dry).Single(h => h.Key == "calibration:f1");

        Assert.False(whileBusy.HasAction);
        Assert.Equal("", whileBusy.ActionLabel);
        Assert.False(inADryRun.HasAction);
    }

    [Fact]
    public void CalibrationMismatch_IsNoHint_ForHiddenFans_OrFromAnOlderDaemon()
    {
        Assert.Empty(Build(Snap(fans: new[] { Mismatched("f1", CalibrationMismatch.Slower) },
            fanConfigs: new[] { Cfg("f1", hidden: true) })));
        Assert.Empty(Build(Snap(fans: new[] { Fan("f1") }, fanConfigs: new[] { Cfg("f1") }))); // no finding sent
    }

    // --- hardware changes -------------------------------------------------------------------------

    private static HardwareChangeReading Replaced(string key, string[]? fans = null, string[]? sensors = null,
                                                  string name = "amdgpu") =>
        new(key, HardwareChangeKind.Replaced, name, "pci:1002:73bf", name, "pci:1002:744c",
            fans ?? Array.Empty<string>(), sensors ?? Array.Empty<string>());

    private static HardwareChangeReading Missing(string key, string[]? fans = null, string[]? sensors = null) =>
        new(key, HardwareChangeKind.Missing, "nvme", "nvme:Samsung SSD 980", null, null,
            fans ?? Array.Empty<string>(), sensors ?? Array.Empty<string>());

    private static MonitorSnapshot WithChanges(params HardwareChangeReading[] changes) => Snap() with { HardwareChanges = changes };

    /// <summary>A replaced device's fans keep the old calibration - their start point is unverified (F4).</summary>
    [Fact]
    public void HardwareReplaced_WithFans_IsANotice_ThatLeadsToTheDialog()
    {
        OverviewHint hint = Assert.Single(Build(WithChanges(
            Replaced("r1", fans: new[] { "amdgpu/pwm1" }, sensors: new[] { "amdgpu/temp1" }))));

        Assert.Equal(("hardwareReplaced", HintSeverity.Notice), (hint.Key, hint.Severity));
        Assert.Equal("„amdgpu“ wurde gegen ein anderes Gerät getauscht - Anlaufpunkt ungeprüft, neu kalibrieren empfohlen.", hint.Text);
        Assert.Equal(HintAction.ReviewHardware, hint.Action);
        Assert.Equal("Ansehen …", hint.ActionLabel);
        Assert.False(hint.NeedsDaemon); // opening the dialog sends nothing
    }

    [Fact]
    public void HardwareReplaced_SensorsOnly_IsInfo()
    {
        OverviewHint hint = Assert.Single(Build(WithChanges(Replaced("r1", sensors: new[] { "nvme/temp1" }, name: "nvme"))));

        Assert.Equal(HintSeverity.Info, hint.Severity);
        Assert.Equal("„nvme“ wurde gegen ein anderes Gerät getauscht - gespeicherte Sensornamen prüfen.", hint.Text);
    }

    [Fact]
    public void HardwareReplaced_IsOneHintForAllDevices_ANoticeIfAnyHasFans()
    {
        OverviewHint hint = Assert.Single(Build(WithChanges(
            Replaced("r1", sensors: new[] { "nvme/temp1" }, name: "nvme"),
            Replaced("r2", fans: new[] { "amdgpu/pwm1" }))));

        Assert.Equal(HintSeverity.Notice, hint.Severity);
        Assert.Equal("2 Geräte wurden getauscht - Anlaufpunkte ungeprüft, neu kalibrieren empfohlen.", hint.Text);
    }

    [Fact]
    public void HardwareMissing_IsOneInfo_CountingTheEntriesOfAllDevices()
    {
        IReadOnlyList<OverviewHint> hints = Build(WithChanges(
            Missing("m1", sensors: new[] { "nvme/temp1", "nvme/temp2" }),
            Missing("m2", fans: new[] { "it87/pwm3" })));

        OverviewHint hint = Assert.Single(hints);
        Assert.Equal(("hardwareMissing", HintSeverity.Info), (hint.Key, hint.Severity));
        Assert.Equal("3 gespeicherte Einträge gehören zu Hardware, die gerade nicht erkannt wird.", hint.Text);
        Assert.Equal(HintAction.ReviewHardware, hint.Action);

        Assert.Equal("1 gespeicherter Eintrag gehört zu Hardware, die gerade nicht erkannt wird.",
            Assert.Single(Build(WithChanges(Missing("m1", sensors: new[] { "nvme/temp1" })))).Text);
    }

    [Fact]
    public void HardwareChanges_FromAnOlderDaemon_OrOfAnUnknownKind_AreNoHint()
    {
        Assert.Empty(Build(Snap() with { HardwareChanges = null }));
        Assert.Empty(Build(WithChanges(Replaced("x", fans: new[] { "f" }) with { Kind = null })));
    }

    [Fact]
    public void HardwareChanges_Offline_ShowOnlyTheOfflineHint()
    {
        OverviewHint hint = Assert.Single(Build(WithChanges(Replaced("r1", fans: new[] { "f" })), ConnectionProblem.Unreachable));

        Assert.Equal("offline", hint.Key);
    }

    /// <summary>
    /// A hint keeps its instance under its key (the row, its focus) - when its action is no longer offered, or its weight
    /// changes, the kept instance follows instead of showing a stale button.
    /// </summary>
    [Fact]
    public void Sync_KeepsTheInstance_AndFollowsActionAndSeverity()
    {
        var list = new System.Collections.ObjectModel.ObservableCollection<OverviewHint>();
        MonitorSnapshot sensorsOnly = WithChanges(Replaced("r1", sensors: new[] { "nvme/temp1" }, name: "nvme"));
        OverviewHints.Sync(list, Build(sensorsOnly));
        OverviewHint kept = Assert.Single(list);
        Assert.Equal(HintSeverity.Info, kept.Severity);

        OverviewHints.Sync(list, Build(WithChanges(Replaced("r1", fans: new[] { "f" }))));
        Assert.Same(kept, Assert.Single(list));
        Assert.Equal(HintSeverity.Notice, kept.Severity);
        Assert.True(kept.IsNotice);

        MonitorSnapshot slower = Snap(fans: new[] { Mismatched("f1", CalibrationMismatch.Slower) }, fanConfigs: new[] { Cfg("f1") });
        OverviewHints.Sync(list, Build(slower));
        OverviewHint fan = Assert.Single(list);
        Assert.True(fan.HasAction);

        OverviewHints.Sync(list, Build(slower with { DryRun = true }).Where(h => h.Key != "dryRun").ToList());
        Assert.Same(fan, Assert.Single(list));
        Assert.False(fan.HasAction);
        Assert.False(fan.NeedsDaemon);
    }

    // --- order ------------------------------------------------------------------------------------

    [Fact]
    public void HeaviestFirst_RuleOrderWithin()
    {
        IReadOnlyList<OverviewHint> hints = Build(Snap(
            new[] { Temp("t1", 85, "Tctl") },
            new[] { Fan("f1", manual: true), Fan("f2", rpm: 0, pwm: 200) },
            fanConfigs: new[] { new FanConfig { FanId = "f1" }, Cfg("f2") },
            curves: new[] { Quiet }, dryRun: true), ConnectionProblem.None);

        Assert.Equal(new[]
        {
            HintSeverity.Critical, HintSeverity.Critical, // hot, then the standstill
            HintSeverity.Notice, HintSeverity.Notice,     // dry run, then manual
            HintSeverity.Info,                            // curve without fans (a dry run refuses to calibrate)
        }, hints.Select(h => h.Severity));
        Assert.StartsWith("Tctl ist heiß", hints[0].Text);
        Assert.StartsWith("f2 meldet 0 RPM", hints[1].Text);
        Assert.StartsWith("Dry-Run", hints[2].Text);
        Assert.StartsWith("f1 wird manuell", hints[3].Text);
    }

    // --- status tile ------------------------------------------------------------------------------

    private static OverviewHint H(HintSeverity severity, string text) => new(text, severity, text);

    [Fact]
    public void Status_NoHints_IsOk_WithinRange()
    {
        OverviewStatus status = OverviewStatus.From(Array.Empty<OverviewHint>());

        Assert.Equal(OverviewLevel.Ok, status.Level);
        Assert.Equal("OK", status.Headline);
        Assert.Equal("Temperaturen und Lüfter im Rahmen", status.Detail);
        Assert.Null(status.Tooltip); // nothing to open
    }

    [Fact]
    public void Status_OnlyInfo_StaysOk_AndNamesTheFirst()
    {
        OverviewStatus status = OverviewStatus.From(new[] { H(HintSeverity.Info, "A."), H(HintSeverity.Info, "B.") });

        Assert.Equal(OverviewLevel.Ok, status.Level);
        Assert.Equal("OK", status.Headline);
        Assert.Equal("A. · +1 weitere", status.Detail);
        Assert.Equal("Alle Hinweise ein-/ausblenden", status.Tooltip);
        Assert.Equal(0, status.AttentionCount); // no badge in the header
    }

    /// <summary>The headline counts what needs a look (notices, critical ones) - the same number as the header's badge.</summary>
    [Fact]
    public void Status_Notices_CountTheHintsThatNeedALook()
    {
        OverviewStatus one = OverviewStatus.From(new[] { H(HintSeverity.Notice, "A.") });
        Assert.Equal(OverviewLevel.Notice, one.Level);
        Assert.Equal("1 Hinweis", one.Headline);
        Assert.Equal("A.", one.Detail);
        Assert.Equal(1, one.AttentionCount);

        OverviewStatus withInfos = OverviewStatus.From(new[]
        {
            H(HintSeverity.Notice, "A."), H(HintSeverity.Info, "B."), H(HintSeverity.Info, "C."),
        });
        Assert.Equal("1 Hinweis", withInfos.Headline);       // the infos are not counted …
        Assert.Equal("A. · +2 weitere", withInfos.Detail);   // … but the second line still mentions them
        Assert.Equal(1, withInfos.AttentionCount);

        OverviewStatus two = OverviewStatus.From(new[] { H(HintSeverity.Notice, "A."), H(HintSeverity.Notice, "B.") });
        Assert.Equal("2 Hinweise", two.Headline);
        Assert.Equal(2, two.AttentionCount);
    }

    [Fact]
    public void Status_Critical()
    {
        OverviewStatus status = OverviewStatus.From(new[] { H(HintSeverity.Critical, "Heiß."), H(HintSeverity.Notice, "B.") });

        Assert.Equal(OverviewLevel.Critical, status.Level);
        Assert.Equal("Kritisch", status.Headline);
        Assert.Equal(2, status.AttentionCount);
        Assert.Equal("Heiß. · +1 weitere", status.Detail);
        Assert.Equal("Status: Kritisch, Heiß. · +1 weitere", status.Name);
    }

    // --- stability over ticks ---------------------------------------------------------------------

    [Fact]
    public void Sync_KeepsTheInstance_WhenOnlyTheValueChanges()
    {
        var list = new System.Collections.ObjectModel.ObservableCollection<OverviewHint>();
        OverviewHints.Sync(list, Build(Snap(new[] { Temp("t1", 86.0, "Tctl") })));
        OverviewHint first = Assert.Single(list);
        var changes = new List<string?>();
        first.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        int collectionChanges = 0;
        list.CollectionChanged += (_, _) => collectionChanges++;

        bool moved = OverviewHints.Sync(list, Build(Snap(new[] { Temp("t1", 86.4, "Tctl") })));

        Assert.False(moved);
        Assert.Same(first, Assert.Single(list));        // the row and its focused button stay
        Assert.Equal("Tctl ist heiß (86.4 °C).", first.Text);
        Assert.Contains(nameof(OverviewHint.Text), changes);
        Assert.Equal(0, collectionChanges);             // nothing re-added, nothing re-announced
    }

    [Fact]
    public void Sync_AddsAndRemoves_InPlace_InTheNewOrder()
    {
        var list = new System.Collections.ObjectModel.ObservableCollection<OverviewHint>();
        OverviewHints.Sync(list, Build(Snap(new[] { Temp("t1", 86.0, "A") }, curves: new[] { Quiet with { SourceSensorIds = new[] { "x" } } })));
        OverviewHint hot = list.Single(h => h.Key == "hot:t1");

        bool moved = OverviewHints.Sync(list, Build(Snap(new[] { Temp("t1", 87.0, "A"), Temp("t2", 90.0, "B") })));

        Assert.True(moved);
        Assert.Equal(new[] { "hot:t1", "hot:t2" }, list.Select(h => h.Key)); // the curve hint is gone, B is new
        Assert.Same(hot, list[0]);
    }

    [Fact]
    public void AccessibleText_SaysTheSeverity()
    {
        Assert.Equal("Kritisch: Heiß.", H(HintSeverity.Critical, "Heiß.").AccessibleText);
        Assert.Equal("Hinweis: B.", H(HintSeverity.Notice, "B.").AccessibleText);
        Assert.Equal("Info: C.", H(HintSeverity.Info, "C.").AccessibleText);
    }
}
