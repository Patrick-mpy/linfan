// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert das optimistische Modus-Umschalten (Auto↔Manual) in <see cref="FanRow"/>: nach einem
/// Nutzer-Toggle darf ein veralteter Snapshot (noch alte <c>ManualOverride</c>-Bestätigung) den
/// Schalter nicht zurückspringen lassen - bleibt die Bestätigung dauerhaft aus, wird die Realität
/// wieder akzeptiert.
/// </summary>
public sealed class FanRowTests
{
    private static FanRow MakeRow()
    {
        // ManualThrottle=0 → die Coalescing-Pumpe läuft inline durch (kein Hintergrund-Task, kein Task.Delay),
        // damit die synchronen Modus-Tests deterministisch bleiben.
        var row = new FanRow("hwmon1/pwm1", "CPU Fan") { ManualThrottle = TimeSpan.Zero };
        row.BindCommands(sendManual: (_, _) => Task.CompletedTask, sendAuto: _ => Task.CompletedTask, sendCalibrate: null);
        return row;
    }

    private static FanReading Reading(bool manualOverride) =>
        new("hwmon1/pwm1", "CPU Fan", Rpm: 1200, Pwm: 128,
            CanControl: true, ManualOverride: manualOverride);

    [Fact]
    public void Update_WithoutPendingAction_FollowsSnapshot()
    {
        var row = MakeRow();

        row.Update(Reading(manualOverride: true));
        Assert.True(row.IsManual);

        row.Update(Reading(manualOverride: false));
        Assert.False(row.IsManual);
    }

    [Fact]
    public void SwitchToManual_ThenStaleAutoSnapshot_HoldsManual()
    {
        var row = MakeRow();

        row.IsManual = true; // Nutzer schaltet auf Manual (optimistisch)
        row.Update(Reading(manualOverride: false)); // noch unterwegs befindlicher Snapshot, alte Bestätigung

        Assert.True(row.IsManual); // Schalter bleibt - kein Zurückspringen
    }

    [Fact]
    public void SwitchToManual_AfterConfirmation_ResumesFollowingSnapshots()
    {
        var row = MakeRow();

        row.IsManual = true;
        row.Update(Reading(manualOverride: true)); // Daemon bestätigt → Pending aufgelöst

        // Danach gilt wieder die Snapshot-Wahrheit (z. B. extern auf Auto zurückgestellt).
        row.Update(Reading(manualOverride: false));
        Assert.False(row.IsManual);
    }

    /// <summary>The same snapshot applied again (the daemon has not ticked yet) does not count against the switch.</summary>
    [Fact]
    public void SwitchToManual_RepeatedOldSnapshot_DoesNotCountAgainstIt()
    {
        var row = MakeRow();
        row.Update(Reading(manualOverride: false));
        row.IsManual = true;

        for (int poll = 0; poll < 6; poll++)
            row.Update(Reading(manualOverride: false), fresh: false);

        Assert.True(row.IsManual);
    }

    [Fact]
    public void DryRun_LocksTheSwitchAndSlider()
    {
        var row = MakeRow();
        row.Update(Reading(manualOverride: false));
        Assert.True(row.CanOperate);

        row.IsDryRun = true;

        Assert.False(row.CanOperate);
        Assert.False(row.CanMoveSlider);
    }

    [Fact]
    public void SwitchToManual_ConfirmationNeverArrives_AcceptsRealityAfterThreshold()
    {
        var row = MakeRow();

        row.IsManual = true; // Befehl wird (simuliert) nie bestätigt
        for (int i = 0; i < 3; i++)
        {
            row.Update(Reading(manualOverride: false));
            Assert.True(row.IsManual); // innerhalb der Toleranz weiter optimistisch gehalten
        }

        row.Update(Reading(manualOverride: false)); // Schwelle überschritten
        Assert.False(row.IsManual); // Realität akzeptiert - Fehlschlag wird sichtbar
    }

    // --- „Bereits kalibriert"-Badge (Dashboard) -------------------------------------------------

    [Fact]
    public void SetCalibration_WithResult_ShowsBadgeWithStartPwmHint()
    {
        var row = MakeRow();

        row.SetCalibration(new FanCalibration { StartPwm = 128, MinRpm = 400, MaxRpm = 1800 }); // 128/255 ≈ 50 %

        Assert.True(row.IsCalibrated);
        Assert.Contains("kalibriert", row.CalibrationBadgeHint);
        Assert.Contains("50 %", row.CalibrationBadgeHint);
    }

    [Fact]
    public void SetCalibration_Null_HidesBadge()
    {
        var row = MakeRow();
        row.SetCalibration(new FanCalibration { StartPwm = 96, MinRpm = 400, MaxRpm = 1800 });

        row.SetCalibration(null);

        Assert.False(row.IsCalibrated);
        Assert.Equal("", row.CalibrationBadgeHint);
    }

    [Fact]
    public void SetCalibration_NoSafeStart_ShowsFailSafeHint()
    {
        var row = MakeRow();

        row.SetCalibration(new FanCalibration { StartPwm = 255, MinRpm = 0, MaxRpm = 0 }); // nicht angelaufen

        Assert.True(row.IsCalibrated);
        Assert.Contains("kein sicherer Anlaufpunkt", row.CalibrationBadgeHint);
    }

    [Fact]
    public void IsCalibrating_DisablesCalibrateButton()
    {
        var row = MakeRow();
        Assert.True(row.CanCalibrate);

        row.IsCalibrating = true;
        Assert.False(row.CanCalibrate); // Dashboard-Kalibrier-Button gesperrt, solange der Lauf läuft

        row.IsCalibrating = false;
        Assert.True(row.CanCalibrate);
    }

    // --- Manuell-Throttle: Coalescing der Slider-Flut ------------------------------------------

    [Fact]
    public async Task SliderDrag_WhileSendInFlight_CoalescesToLatestValue()
    {
        var sent = new List<byte>();
        var firstInFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Func<string, byte, Task> sink = async (_, pwm) =>
        {
            sent.Add(pwm);
            if (sent.Count == 1)
            {
                firstInFlight.SetResult();
                await release.Task; // den ersten Send in der Luft halten
            }
        };

        var row = new FanRow("f", "F") { ManualThrottle = TimeSpan.Zero };
        row.BindCommands(sink, sendAuto: _ => Task.CompletedTask, sendCalibrate: null);

        row.IsManual = true; // erster Manual-Send (Slider 0 → pwm 0), hängt am Gate
        await firstInFlight.Task;

        for (int p = 1; p <= 100; p++) // viele schnelle Slider-Änderungen, während der erste Send hängt
            row.SliderPercent = p;

        release.SetResult(); // ersten Send abschließen → die Pumpe sendet nur noch den letzten Wert
        await row.ManualPumpCompletion;

        Assert.Equal((byte)0, sent[0]);              // erster Stellwert
        Assert.Equal(PwmScale.ToPwm(100), sent[^1]); // Endposition garantiert gesendet
        Assert.True(sent.Count <= 2, $"Zwischenwerte müssen coalescen; gesendet: {sent.Count}");
    }

    [Fact]
    public void SwitchToAuto_ThenStaleManualSnapshot_HoldsAuto()
    {
        var row = MakeRow();
        row.IsManual = true;
        row.Update(Reading(manualOverride: true)); // erst sauber in Manual versetzen

        row.IsManual = false; // Nutzer schaltet zurück auf Auto
        row.Update(Reading(manualOverride: true)); // veralteter Snapshot trägt noch Manual

        Assert.False(row.IsManual); // Schalter bleibt auf Auto
    }

    // --- Overview table: device-action lock, calibration line, override note, back to the curve ---------

    [Fact]
    public void Calibrate_IsLocked_WithTheReason_WhileDeviceActionsAreBlocked()
    {
        var row = MakeRow();

        row.CalibrateBlockedReason = "Gesperrt - der Hintergrunddienst ist nicht erreichbar.";

        Assert.False(row.CanCalibrate);
        Assert.Equal("Gesperrt - der Hintergrunddienst ist nicht erreichbar.", row.CalibrateTooltip);

        row.CalibrateBlockedReason = "";
        Assert.True(row.CanCalibrate);
        Assert.StartsWith("Ermittelt den minimalen PWM-Wert", row.CalibrateTooltip);
    }

    [Fact]
    public void CalibrationState_SaysRunning_Calibrated_OrNotYet()
    {
        var row = MakeRow();
        Assert.Equal("Noch nicht kalibriert", row.CalibrationState);

        row.SetCalibration(new FanCalibration { StartPwm = 64, MinRpm = 400, MaxRpm = 1800 });
        Assert.Equal("Kalibriert · Anlauf bei 25 %", row.CalibrationState);

        row.SetCalibration(new FanCalibration { StartPwm = 255, MinRpm = 0, MaxRpm = 0 });
        Assert.Equal("Kalibriert · kein sicherer Anlaufpunkt", row.CalibrationState);

        row.IsCalibrating = true;
        Assert.Equal("Kalibrierung läuft …", row.CalibrationState);
    }

    [Fact]
    public void OverrideNote_OnlyWhileManual_OverACurve()
    {
        var row = MakeRow();
        row.OverriddenCurveName = "Quiet";
        Assert.False(row.ShowOverrideNote);

        row.Update(Reading(manualOverride: true));

        Assert.True(row.ShowOverrideNote);
        Assert.Equal("Übersteuert „Quiet“ - die Kurve greift erst wieder im Auto-Modus.", row.OverrideNote);
        Assert.Equal("CPU Fan · pwm 128 · 50% · manuell übersteuert", row.ChipTooltip);
    }

    [Fact]
    public void BackToCurve_SendsAuto_ButNotOffline()
    {
        var autos = new List<string>();
        var row = new FanRow("hwmon1/pwm1", "CPU Fan") { ManualThrottle = TimeSpan.Zero };
        row.BindCommands((_, _) => Task.CompletedTask, id => { autos.Add(id); return Task.CompletedTask; }, null);
        row.Update(Reading(manualOverride: true));

        row.IsOffline = true;
        Assert.False(row.BackToCurveCommand.CanExecute(null));
        Assert.False(row.CanOperate); // switch and slider locked as well

        row.IsOffline = false;
        row.BackToCurveCommand.Execute(null);

        Assert.False(row.IsManual);
        Assert.Equal(new[] { "hwmon1/pwm1" }, autos);
    }

    [Fact]
    public void ControlChip_OpensItsCurve()
    {
        string? opened = null;
        var row = new FanRow("f", "F");
        row.BindCommands(null, null, null, openCurve: id => opened = id);
        row.Control = new FanControl(Core.Services.FanControlState.Curve, "Quiet", "", ControlTone.Normal, "c1");

        Assert.True(row.ControlOpensCurve);
        row.OpenControlCurveCommand.Execute(null);

        Assert.Equal("c1", opened);
    }

    [Fact]
    public void Placement_SortsByPosition()
    {
        var row = MakeRow();
        row.SetPlacement(FanLocation.CpuCooler);
        int cooler = row.LocationRank;
        row.SetPlacement(FanLocation.Unspecified);

        Assert.True(cooler < row.LocationRank); // "no position" last
        Assert.Equal("", row.Location);
    }

    [Fact]
    public void ManualFromElsewhere_StartsTheSliderAtTheHeldValue()
    {
        var row = MakeRow();
        row.Update(Reading(manualOverride: false)); // auto, pwm 128

        row.Update(new FanReading("hwmon1/pwm1", "CPU Fan", 1200, Pwm: 204, CanControl: true, ManualOverride: true));

        Assert.True(row.IsManual);
        Assert.Equal(80, row.SliderPercent, 3);
    }

    [Fact]
    public void GoingOffline_DropsAPendingToggle_AndShowsTheConfirmedMode()
    {
        var manuals = new List<byte>();
        var autos = new List<string>();
        var row = new FanRow("hwmon1/pwm1", "CPU Fan") { ManualThrottle = TimeSpan.Zero };
        row.BindCommands((_, pwm) => { manuals.Add(pwm); return Task.CompletedTask; },
            id => { autos.Add(id); return Task.CompletedTask; }, null);
        row.Update(Reading(manualOverride: false));

        row.IsManual = true; // on its way to the daemon …
        row.IsOffline = true; // … when the connection drops

        Assert.False(row.IsManual); // the daemon confirmed auto last

        manuals.Clear();
        row.IsManual = true;        // whatever reaches the row offline is not sent
        row.SliderPercent = 90;
        Assert.Empty(manuals);
        Assert.Empty(autos);
    }

    [Fact]
    public void Texts_FollowALanguageSwitch_WithoutANewSnapshot()
    {
        var row = MakeRow();
        row.SetCalibration(new FanCalibration { StartPwm = 64, MinRpm = 400, MaxRpm = 1800 });
        row.SetPlacement(FanLocation.CaseFrontIntake);
        string germanLocation = row.Location;
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        try
        {
            LinFan.App.Localization.Localizer.Instance.SetLanguage(LanguageChoice.English);
            row.RefreshTexts();

            Assert.Equal("Calibrated · starts at 25 %", row.CalibrationState);
            Assert.NotEqual(germanLocation, row.Location);
            Assert.Contains(nameof(FanRow.CalibrationState), changed);
            Assert.Contains(nameof(FanRow.Location), changed);
        }
        finally
        {
            LinFan.App.Localization.Localizer.Instance.SetLanguage(LanguageChoice.German);
        }
    }
}
