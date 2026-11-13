// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;

namespace LinFan.App.Tests;

/// <summary>
/// Settings › Fans › Advanced as the row presents it: the power range with the calibrated start point, the state and
/// actions of calibration and tach coupling, the „assign by hand" list (a sensor another fan holds is locked) and the
/// test switch between curve and manual.
/// </summary>
public sealed class FanAssignRowAdvancedTests
{
    private static FanAssignRow Row(
        byte? startPwm = null, byte minPwm = 0, bool canControl = true, string? rpmSource = null,
        ObservableCollection<TachSensorOption>? tachs = null, Func<Task>? cancelCalibration = null,
        List<(string FanId, string? SensorId)>? sets = null, Func<string, byte, Task>? sendManual = null,
        Func<FanAssignRow, string, string>? tachOwner = null, Func<FanAssignRow, string, string>? tachAutoOwner = null) =>
        new(new FanConfig
        {
            FanId = "hwmon7/pwm1",
            Name = "CPU",
            MinPwm = minPwm,
            RpmSource = rpmSource,
            Calibration = startPwm is { } s ? new FanCalibration { StartPwm = s } : null,
        },
            selected: null, new ObservableCollection<CurveEditRow>(), canControl,
            sendManual: sendManual,
            sendSetTach: sets is null ? null : (id, sensor) => { sets.Add((id, sensor)); return Task.CompletedTask; },
            availableTachSensors: tachs, cancelCalibration: cancelCalibration)
        {
            TachOwner = tachOwner,
            TachAutoOwner = tachAutoOwner,
        };

    private static ObservableCollection<TachSensorOption> Tachs() => new()
    {
        new TachSensorOption(null, "Automatisch (Hardware)"),
        new TachSensorOption("hwmon7/fan1", "Fan 1"),
        new TachSensorOption("hwmon7/fan2", "Fan 2"),
    };

    // --- Power range and start point --------------------------------------------------------------

    [Fact]
    public void LoadedCalibration_ShowsTheStartPoint_AndMarksItOnTheRange()
    {
        FanAssignRow row = Row(startPwm: 46);

        Assert.Equal(18, row.StartPercent); // 46/255 ≈ 18 %
        Assert.Equal(18, row.StartMarker);
        Assert.Equal("Anlauf 18 %", row.StartMarkerLabel);
        Assert.Equal("18 % · pwm 46", row.StartText);
        Assert.Equal("kalibriert", row.CalibrationChipText);
        Assert.Equal(StatusTone.Ok, row.CalibrationChipTone);
        Assert.Equal("Neu kalibrieren", row.CalibrateLabel);
    }

    [Fact]
    public void WithoutCalibration_NoMarker_AndTheButtonSaysCalibrate()
    {
        FanAssignRow row = Row();

        Assert.Null(row.StartMarker);
        Assert.Equal("-", row.StartText);
        Assert.Equal("nicht kalibriert", row.CalibrationChipText);
        Assert.Equal(StatusTone.Muted, row.CalibrationChipTone);
        Assert.Equal("Kalibrieren", row.CalibrateLabel);
    }

    [Fact]
    public void NoSafeStartPoint_IsAWarning_WithoutMarkerOrHint()
    {
        FanAssignRow row = Row(startPwm: 255, minPwm: 10);

        Assert.Null(row.StartMarker);
        Assert.Equal("keiner gefunden", row.StartText);
        Assert.Equal("kein sicherer Anlaufpunkt", row.CalibrationChipText);
        Assert.Equal(StatusTone.Warn, row.CalibrationChipTone);
        Assert.False(row.HasStartHint);
        Assert.False(row.RaiseToStartCommand.CanExecute(null));
    }

    [Fact]
    public void MinBelowTheStartPoint_IsFlagged_AndCanBeRaisedToIt()
    {
        FanAssignRow row = Row(startPwm: 46, minPwm: 30);

        Assert.True(row.HasStartHint);
        Assert.Contains("Min liegt unter dem Anlaufpunkt (18 %)", row.StartHintText);
        Assert.Equal("Min auf 18 % setzen", row.StartHintAction);

        row.RaiseToStartCommand.Execute(null);

        Assert.Equal(46, row.MinPwm);
        Assert.False(row.HasStartHint);
    }

    /// <summary>
    /// Max under the start point would clamp every curve value below it - the fan never starts. The daemon raises such a
    /// Max (ConfigSanitizer), so the row does too: the slider snaps back, the stepper starts there.
    /// </summary>
    [Fact]
    public void MaxCannotGoBelowTheStartPoint()
    {
        FanAssignRow row = Row(startPwm: 46, minPwm: 0);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.MaxPercent = 5;

        Assert.Equal(46, row.MaxPwm);
        Assert.Equal(18, row.MaxPercent);
        Assert.Contains(nameof(FanAssignRow.MaxPercent), changed); // the slider reads the floor back
        Assert.Equal(18, row.MaxFloorPercent);
        Assert.False(row.HasStartHint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((byte)0)] // a start point of 0 is none to the daemon either
    public void MaxCannotGoToZero_WithoutAStartPoint(byte? startPwm)
    {
        // The daemon reads Max 0 as „no cap" - a GUI showing 0 % would show what the daemon does not do.
        FanAssignRow row = Row(startPwm: startPwm, minPwm: 0);

        row.MaxPercent = 0;

        Assert.Equal(1, row.MaxPercent);
        Assert.True(row.MaxPwm > 0);
        Assert.Equal(1, row.MaxFloorPercent);
    }

    [Fact]
    public void TheMaxFloor_FollowsMin_WhenMinIsAboveTheStartPoint()
    {
        FanAssignRow row = Row(startPwm: 46, minPwm: 128);

        Assert.Equal(row.MinPercent, row.MaxFloorPercent);
    }

    [Theory]
    [InlineData(0, true)]   // Min 0 is a deliberate „may stop"
    [InlineData(46, true)]  // at the start point
    [InlineData(30, false)] // read-only fans have no range to set
    public void BelowStart_IsNotFlagged(byte minPwm, bool canControl)
    {
        Assert.False(Row(startPwm: 46, minPwm: minPwm, canControl: canControl).HasStartHint);
    }

    [Fact]
    public void StoredCalibration_FromTheDaemon_UpdatesTheStartPoint_AndANullLeavesIt()
    {
        FanAssignRow row = Row();

        row.ApplyStoredCalibration(new FanCalibration { StartPwm = 51 });
        Assert.Equal((byte)51, row.StartPwm);
        Assert.True(row.IsCalibrated);

        row.ApplyStoredCalibration(null); // a snapshot from before the daemon stored it
        Assert.Equal((byte)51, row.StartPwm);
    }

    [Theory]
    [InlineData(CalibrationMismatch.Stalls, "Dreht kaum, obwohl die Kalibrierung hier Drehung erwarten lässt - neu kalibrieren empfohlen.")]
    [InlineData(CalibrationMismatch.Slower, "Dreht deutlich langsamer, als die Kalibrierung erwarten lässt - wurde der Lüfter getauscht? Neu kalibrieren empfohlen.")]
    [InlineData(CalibrationMismatch.Faster, "Dreht deutlich schneller, als die Kalibrierung erwarten lässt - wurde der Lüfter getauscht? Neu kalibrieren empfohlen.")]
    [InlineData(CalibrationMismatch.OtherSensor, "Die Kalibrierung wurde mit einem anderen Drehzahl-Sensor gemessen als dem aktuellen - neu kalibrieren empfohlen.")]
    public void CalibrationMismatch_TurnsTheChipIntoAWarning_AndSaysWhatTheDaemonFound(CalibrationMismatch mismatch, string text)
    {
        FanAssignRow row = Row(startPwm: 46);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.ApplyCalibrationMismatch(mismatch);

        Assert.True(row.HasCalibrationMismatch);
        Assert.Equal(text, row.CalibrationMismatchText);
        Assert.Equal("passt nicht mehr", row.CalibrationChipText);
        Assert.Equal(StatusTone.Warn, row.CalibrationChipTone);
        Assert.False(row.ShowsCalibratedBadge); // the warning takes the badge's place
        Assert.True(row.IsCalibrated);
        Assert.Subset(changed.ToHashSet(), new HashSet<string?>
        {
            nameof(FanAssignRow.HasCalibrationMismatch), nameof(FanAssignRow.CalibrationMismatchText),
            nameof(FanAssignRow.CalibrationChipText), nameof(FanAssignRow.CalibrationChipTone),
            nameof(FanAssignRow.ShowsCalibratedBadge),
        });

        row.ApplyCalibrationMismatch(null); // recalibrated, or the fan fits again
        Assert.False(row.HasCalibrationMismatch);
        Assert.Equal("kalibriert", row.CalibrationChipText);
        Assert.Equal(StatusTone.Ok, row.CalibrationChipTone);
        Assert.True(row.ShowsCalibratedBadge);
    }

    /// <summary>Without a speed sensor only calibrate is locked: identify still works, and coupling is how it gets one.</summary>
    [Fact]
    public void NoSpeedSensor_LocksCalibrate_WithTheReason_ButNotIdentifyOrCoupling()
    {
        FanAssignRow row = Row(startPwm: 46);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        int canExecuteChanged = 0;
        row.CalibrateCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        row.ApplyLacksSpeedSensor(true);

        Assert.False(row.CanCalibrate);
        Assert.False(row.CalibrateCommand.CanExecute(null));
        Assert.Equal("Gesperrt - dieser Lüfter hat keinen eigenen, erkannten Drehzahl-Sensor zum Messen. Zuerst einen Tacho koppeln.",
            row.CalibrateTooltip);
        Assert.True(row.CanIdentify);
        Assert.True(row.CanCoupleSensor);
        Assert.Contains(nameof(FanAssignRow.CanCalibrate), changed);
        Assert.Contains(nameof(FanAssignRow.CalibrateTooltip), changed);
        Assert.Equal(1, canExecuteChanged);

        row.BlockDeviceActions("Gesperrt - der Hintergrunddienst ist nicht erreichbar.");
        Assert.Equal("Gesperrt - der Hintergrunddienst ist nicht erreichbar.", row.CalibrateTooltip); // the shared lock first

        row.BlockDeviceActions("");
        row.ApplyLacksSpeedSensor(false); // coupled
        Assert.True(row.CanCalibrate);
        Assert.True(row.CalibrateCommand.CanExecute(null));
        Assert.Equal("Ermittelt den minimalen PWM-Wert, bei dem dieser Lüfter sicher anläuft.", row.CalibrateTooltip);
    }

    [Fact]
    public void CalibrationMismatch_IsNotShown_WhileACalibrationRuns_OrForAFanThatCannotBeCalibrated()
    {
        FanAssignRow row = Row(startPwm: 46);
        row.ApplyCalibrationMismatch(CalibrationMismatch.OtherSensor);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 120, 1500,
            Running: true, Done: false, StartPwm: null, FailReason: null));
        Assert.False(row.HasCalibrationMismatch); // the run replaces the calibration the finding is about
        Assert.Contains(nameof(FanAssignRow.HasCalibrationMismatch), changed);
        Assert.StartsWith("läuft", row.CalibrationChipText);

        FanAssignRow readOnly = Row(startPwm: 46, canControl: false);
        readOnly.ApplyCalibrationMismatch(CalibrationMismatch.OtherSensor);
        Assert.False(readOnly.HasCalibrationMismatch);
        Assert.True(readOnly.ShowsCalibratedBadge);
    }

    // --- Calibration ------------------------------------------------------------------------------

    [Fact]
    public void RunningCalibration_TurnsTheButtonIntoCancelWithProgress()
    {
        FanAssignRow row = Row();

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 120, 1500,
            Running: true, Done: false, StartPwm: null, FailReason: null));

        Assert.Equal("läuft … 47 %", row.CalibrationChipText); // 120/255 ≈ 47 %
        Assert.Equal(StatusTone.Busy, row.CalibrationChipTone);
        Assert.Equal("Abbrechen · 47 %", row.CancelCalibrationLabel);
        Assert.False(row.HasCalibrationResult);
        Assert.Contains("pwm 120", row.CalibrationProgress);
    }

    [Theory]
    [InlineData(null, StatusTone.Ok)]
    [InlineData(CalibrationFailReason.Canceled, StatusTone.Muted)]
    [InlineData(CalibrationFailReason.NoTacho, StatusTone.Warn)]
    public void FinishedCalibration_IsHeldAsAResult_InItsTone(CalibrationFailReason? reason, StatusTone tone)
    {
        FanAssignRow row = Row();

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", reason is null ? CalibrationPhase.Done : CalibrationPhase.Failed,
            0, 0, Running: false, Done: reason is null, StartPwm: reason is null ? 46 : null, FailReason: reason));

        Assert.True(row.HasCalibrationResult);
        Assert.Equal(tone, row.CalibrationResultTone);
        Assert.Equal(reason is null, row.StartPwm == 46);
    }

    /// <summary>A run can end „done" without a start point (the fan never spun) - nothing to show in green.</summary>
    [Fact]
    public void FinishedCalibration_WithoutAStartPoint_IsAWarning()
    {
        FanAssignRow row = Row();

        row.ApplyCalibration(new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Done, 0, 0,
            Running: false, Done: true, StartPwm: 255, FailReason: null));

        Assert.Equal(StatusTone.Warn, row.CalibrationResultTone);
        Assert.Equal("Fertig - kein sicherer Anlaufpunkt gefunden", row.CalibrationProgress);
        Assert.True(row.IsCalibrated);
    }

    [Fact]
    public async Task CancelCalibration_FlowsThroughTheDelegate()
    {
        int cancels = 0;
        FanAssignRow row = Row(cancelCalibration: () => { cancels++; return Task.CompletedTask; });

        await row.CancelCalibrationCommand.ExecuteAsync(null);

        Assert.Equal(1, cancels);
    }

    // --- Speed sensor -------------------------------------------------------------------------------

    [Fact]
    public void WithoutAnOwnCoupling_ItIsAutomatic_AndOkWhileSpeedComes()
    {
        FanAssignRow row = Row(tachs: Tachs());
        row.SetLive(1180, 110);

        Assert.Equal("Automatisch (Hardware)", row.TachName);
        Assert.Equal("liefert Drehzahl", row.TachChipText);
        Assert.Equal(StatusTone.Ok, row.TachChipTone);
        Assert.Equal("Tacho koppeln", row.CoupleLabel);

        row.SetLive(null, 110);
        Assert.Equal("kein Signal", row.TachChipText);
        Assert.Equal(StatusTone.Warn, row.TachChipTone);
    }

    [Fact]
    public void TheHardwaresPairing_IsNamed_OnceTheDaemonReportsIt()
    {
        FanAssignRow row = Row(tachs: Tachs());

        Assert.True(row.ApplyDaemonTachometer("hwmon7/fan2"));
        Assert.Equal("Automatisch: Fan 2", row.TachName);
        Assert.Equal("hwmon7/fan2", row.AutoTachId);
        Assert.False(row.ApplyDaemonTachometer("hwmon7/fan2")); // unchanged: nothing to refresh

        row.ApplyDaemonTachometer(null); // none (another fan is coupled to it) - or an older daemon
        Assert.Equal("Automatisch (Hardware)", row.TachName);
    }

    [Fact]
    public void AnOwnCoupling_IsNoHardwarePairing()
    {
        FanAssignRow row = Row(tachs: Tachs(), rpmSource: "hwmon7/fan1");

        row.ApplyDaemonTachometer("hwmon7/fan1"); // the daemon reads the coupled sensor

        Assert.Equal("Fan 1", row.TachName);
        Assert.Null(row.AutoTachId);
    }

    [Fact]
    public void AnOwnCoupling_IsNamed_AndSharedOneIsAWarning()
    {
        string owner = "";
        FanAssignRow row = Row(tachs: Tachs(), rpmSource: "hwmon7/fan1", tachOwner: (_, id) => id == "hwmon7/fan1" ? owner : "");
        row.SetLive(1180, 110);

        Assert.Equal("Fan 1", row.TachName);
        Assert.Equal("gekoppelt", row.TachChipText);
        Assert.Equal("Tacho neu koppeln", row.CoupleLabel);

        owner = "Front"; // another fan picked the same sensor
        row.RefreshTach();
        Assert.Equal("mehrfach belegt", row.TachChipText);
        Assert.Equal(StatusTone.Warn, row.TachChipTone);
    }

    /// <summary>
    /// A coupling to a sensor the backend no longer reports (a swapped card) still holds in the daemon - the row must not
    /// read „automatic", and no entry of the list stands for it.
    /// </summary>
    [Fact]
    public void ACouplingToAMissingSensor_IsAWarning_NotAutomatic()
    {
        var sets = new List<(string FanId, string? SensorId)>();
        FanAssignRow row = Row(tachs: Tachs(), rpmSource: "amdgpu/fan1", sets: sets);
        row.SetLive(null, 110);

        Assert.Equal("Nicht erkannt: amdgpu/fan1", row.TachName);
        Assert.Equal("Sensor fehlt", row.TachChipText);
        Assert.Equal(StatusTone.Warn, row.TachChipTone);
        Assert.Null(row.SelectedTachChoice);

        row.ApplyRpmSource("amdgpu/fan1", "GPU-Lüfter"); // the config has an own name for it
        Assert.Equal("Nicht erkannt: GPU-Lüfter", row.TachName);

        row.ApplyRpmSource(null); // cleared (here or elsewhere): the hardware's own pairing applies again
        Assert.Equal("Automatisch (Hardware)", row.TachName);
        Assert.Empty(sets);       // following the daemon sends nothing back
    }

    [Fact]
    public void RunningCoupling_IsBusy()
    {
        FanAssignRow row = Row(tachs: Tachs());

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", TachMappingPhase.Running, Running: true));

        Assert.Equal("koppeln …", row.TachChipText);
        Assert.Equal(StatusTone.Busy, row.TachChipTone);
        Assert.False(row.HasTachMappingResult);
    }

    [Theory]
    [InlineData(TachMappingPhase.Matched, null, StatusTone.Ok)]
    [InlineData(TachMappingPhase.NoResponse, null, StatusTone.Warn)]
    [InlineData(TachMappingPhase.Ambiguous, null, StatusTone.Warn)]
    [InlineData(TachMappingPhase.Failed, TachMappingFailReason.Canceled, StatusTone.Muted)]
    [InlineData(TachMappingPhase.Failed, TachMappingFailReason.Busy, StatusTone.Warn)]
    public void FinishedCoupling_IsHeldAsAResult_InItsTone(TachMappingPhase phase, TachMappingFailReason? reason, StatusTone tone)
    {
        FanAssignRow row = Row(tachs: Tachs());

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", phase, Running: false, FailReason: reason));

        Assert.True(row.HasTachMappingResult);
        Assert.Equal(tone, row.TachMappingResultTone);
    }

    [Fact]
    public void AssignByHand_ListsEverySensor_AndLocksTheOnesAnotherFanHolds()
    {
        var sets = new List<(string, string?)>();
        ObservableCollection<TachSensorOption> tachs = Tachs();
        FanAssignRow row = Row(tachs: tachs, sets: sets, tachOwner: (_, id) => id == "hwmon7/fan2" ? "Front" : "");
        Assert.Empty(row.TachChoices); // built only while open

        row.ShowTachChoices = true;

        Assert.Equal(3, row.TachChoices.Count);
        TachChoice taken = row.TachChoices[2];
        Assert.False(taken.IsEnabled);
        Assert.Equal("belegt: Front", taken.TakenText);
        Assert.Same(row.TachChoices[0], row.SelectedTachChoice); // automatic

        row.SelectedTachChoice = taken; // refused
        Assert.Same(tachs[0], row.SelectedTach);
        Assert.Empty(sets);

        row.SelectedTachChoice = row.TachChoices[1];
        Assert.Same(tachs[1], row.SelectedTach);
        Assert.Equal(new[] { ("hwmon7/pwm1", (string?)"hwmon7/fan1") }, sets);
    }

    [Fact]
    public void AssignByHand_NamesTheFanTheHardwarePairsASensorWith_ButOffersIt()
    {
        var sets = new List<(string, string?)>();
        string autoOwner = "Heck";
        FanAssignRow row = Row(tachs: Tachs(), sets: sets,
            tachOwner: (_, id) => id == "hwmon7/fan2" ? "Front" : "",
            tachAutoOwner: (_, id) => id is "hwmon7/fan1" or "hwmon7/fan2" ? autoOwner : "");

        row.ShowTachChoices = true;

        TachChoice paired = row.TachChoices[1];
        Assert.True(paired.IsEnabled);                               // an own coupling outranks the pairing
        Assert.True(paired.HasAutoAt);
        Assert.Equal("automatisch bei Heck", paired.AutoAtText);
        Assert.False(row.TachChoices[2].HasAutoAt);                   // taken explicitly: that note says more
        Assert.False(row.TachChoices[0].HasAutoAt);                   // "automatic" itself

        autoOwner = "";
        row.RefreshTach();
        Assert.False(paired.HasAutoAt);

        row.SelectedTachChoice = paired;
        Assert.Equal(new[] { ("hwmon7/pwm1", (string?)"hwmon7/fan1") }, sets);
    }

    // --- Test ---------------------------------------------------------------------------------------

    [Fact]
    public void ManualCannotStart_WhileTheFanIsBusy_OrDeviceActionsAreLocked()
    {
        FanAssignRow row = Row();

        row.ApplyTachMapping(new TachMappingStatus("hwmon7/pwm1", TachMappingPhase.Running, Running: true));
        row.Manual.ModeIndex = 1;
        Assert.False(row.Manual.IsActive);

        row.ApplyTachMapping(null);
        row.BlockDeviceActions("Gesperrt - der Hintergrunddienst ist nicht erreichbar.");
        row.Manual.ModeIndex = 1;
        Assert.False(row.Manual.IsActive);

        row.BlockDeviceActions("");
        row.Manual.ModeIndex = 1;
        Assert.True(row.Manual.IsActive);
    }

    [Theory]
    [InlineData("calibrate")]
    [InlineData("identify")]
    [InlineData("couple")]
    public void DeviceAction_LetsGoOfARunningManualValueFirst(string action)
    {
        // The daemon keeps a manual override through a device action - without letting go first, the fan would return
        // to the test value afterwards (the action can start from the airflow page, where that value is not visible).
        var calls = new List<string>();
        Task Record(string what) { calls.Add(what); return Task.CompletedTask; }
        var row = new FanAssignRow(new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU" },
            selected: null, new ObservableCollection<CurveEditRow>(), canControl: true,
            sendCalibrate: _ => Record("calibrate"), sendIdentify: _ => Record("identify"),
            sendManual: (_, _) => Task.CompletedTask, sendAuto: _ => Record("auto"),
            sendTachMapping: _ => Record("couple"));
        row.Manual.ModeIndex = 1;
        Assert.True(row.Manual.IsActive);

        (action switch
        {
            "calibrate" => row.CalibrateCommand,
            "identify" => row.IdentifyCommand,
            _ => row.CoupleSensorCommand,
        }).Execute(null);

        Assert.False(row.Manual.IsActive);
        Assert.Equal(["auto", action], calls);
    }

    [Fact]
    public async Task SwitchingToManual_StartsAtTheLivePower_AndCollapsingReturnsToTheCurve()
    {
        var sent = new List<byte>();
        FanAssignRow row = Row(sendManual: (_, pwm) => { sent.Add(pwm); return Task.CompletedTask; });
        row.Manual.Throttle = TimeSpan.Zero;
        row.SetLive(1180, 128); // 50 %
        Assert.Equal(50, row.Manual.ShownPercent);

        row.ShowAdvanced = true;
        row.Manual.ModeIndex = 1;
        await row.Manual.PumpCompletion;

        Assert.True(row.Manual.IsActive);
        Assert.Equal(50, row.Manual.Percent);
        Assert.Equal(new byte[] { 128 }, sent.Distinct());

        row.ShowAdvanced = false;
        Assert.False(row.Manual.IsActive);
        Assert.Equal(0, row.Manual.ModeIndex);
    }
}
