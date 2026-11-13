// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Undo/redo and the change markers of the curve editor. The binding rules: only user edits are recorded; live
/// changes (calibrated start PWM, curve on/off, running profile) are never undone but folded into every step;
/// Resync starts a fresh history; the running profile survives an undo that predates it.
/// </summary>
public sealed class CurveEditorHistoryTests
{
    private const string Fan = "hwmon7/pwm1";

    private sealed class Sink
    {
        public List<AppConfig> Saved { get; } = new();
        public List<string> Activated { get; } = new();
        public List<(string Id, bool Enabled)> Toggles { get; } = new();

        public Task<bool> SaveAsync(AppConfig config, IReadOnlyDictionary<string, byte> baseMinPwm)
        {
            Saved.Add(config);
            return Task.FromResult(true);
        }
    }

    private static MonitorSnapshot Snapshot(AppConfig config, double temp = 40) => new(
        "test",
        new[]
        {
            new SensorReading("hwmon6/temp1", "k10temp Tctl", SensorKind.Temperature, "°C", temp),
            new SensorReading("hwmon7/temp1", "thinkpad CPU", SensorKind.Temperature, "°C", 38),
        },
        new[] { new FanReading(Fan, "thinkpad pwm1", 1900, 255, true) },
        config);

    private static readonly CurveConfig Quiet = new()
    {
        Id = "c1",
        Name = "Quiet",
        SourceSensorIds = new[] { "hwmon6/temp1" },
        Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
    };

    /// <summary>One running profile p1 with curve c1 driving the fan, plus groups that must survive every restore.</summary>
    private static AppConfig OneProfile() => new()
    {
        Curves = new[] { Quiet },
        Fans = new[] { new FanConfig { FanId = Fan, Name = "thinkpad pwm1", MinPwm = 40, MaxPwm = 255, AssignedCurveId = "c1" } },
        SensorGroups = new[] { "CPU", "Mainboard" },
        Profiles = new[]
        {
            new Profile { Id = "p1", Name = "Eins", Curves = new[] { Quiet }, Assignments = new[] { new ProfileAssignment(Fan, "c1") } },
        },
        ActiveProfileId = "p1",
    };

    /// <summary>Like <see cref="OneProfile"/>, plus p2 - a duplicate sharing the curve id c1.</summary>
    private static AppConfig TwoProfiles()
    {
        AppConfig one = OneProfile();
        return one with
        {
            Profiles = one.Profiles.Append(new Profile
            {
                Id = "p2",
                Name = "Zwei",
                Curves = new[] { Quiet },
                Assignments = new[] { new ProfileAssignment(Fan, "c1") },
            }).ToList(),
        };
    }

    private static AppConfig WithMinPwm(AppConfig config, byte minPwm) => config with
    {
        Fans = config.Fans.Select(f => f with { MinPwm = minPwm }).ToList(),
    };

    private static (CurveEditorController Ctrl, ManualClock Clock, Sink Sink) Editor(AppConfig config)
    {
        var sink = new Sink();
        var clock = new ManualClock();
        var ctrl = new CurveEditorController(
            sink.SaveAsync,
            activateProfile: id => { sink.Activated.Add(id); return Task.CompletedTask; },
            setCurveEnabled: (id, on) => { sink.Toggles.Add((id, on)); return Task.CompletedTask; },
            clock: clock)
        {
            IsConnected = true,
        };
        ctrl.Initialize(Snapshot(config));
        return (ctrl, clock, sink);
    }

    /// <summary>Lets the burst go quiet and runs a live tick, which closes it.</summary>
    private static void Settle(CurveEditorController ctrl, ManualClock clock, AppConfig daemonConfig)
    {
        clock.AdvanceMs(1000);
        ctrl.UpdateLive(Snapshot(daemonConfig));
    }

    [Fact]
    public void Undo_RestoresAnEdit_RedoReappliesIt()
    {
        var (ctrl, _, _) = Editor(OneProfile());
        Assert.False(ctrl.UndoCommand.CanExecute(null));

        ctrl.Curves.Single().Name = "Leise";
        Assert.True(ctrl.UndoCommand.CanExecute(null));

        ctrl.UndoCommand.Execute(null);
        Assert.Equal("Quiet", ctrl.Curves.Single().Name);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Equal("Rückgängig gemacht.", ctrl.Status.Text);
        Assert.True(ctrl.RedoCommand.CanExecute(null));

        ctrl.RedoCommand.Execute(null);
        Assert.Equal("Leise", ctrl.Curves.Single().Name);
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.Equal("Wiederhergestellt.", ctrl.Status.Text);
    }

    [Fact]
    public void StepperTyping_IsOneStep()
    {
        // NumericUpDown commits per keystroke: typing „4" then „5" sets 4, then 45.
        var (ctrl, clock, _) = Editor(OneProfile());
        CurveEditRow curve = ctrl.Curves.Single();

        curve.Hysteresis = 4m;
        clock.AdvanceMs(180);
        curve.Hysteresis = 15m;
        Settle(ctrl, clock, OneProfile());

        ctrl.UndoCommand.Execute(null);
        Assert.Equal(2m, ctrl.Curves.Single().Hysteresis);
        Assert.False(ctrl.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void LiveTick_IsNotAStep()
    {
        var (ctrl, clock, _) = Editor(OneProfile());

        clock.AdvanceMs(1000);
        ctrl.UpdateLive(Snapshot(OneProfile(), temp: 77));

        Assert.False(ctrl.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void CalibratedMinPwm_AdoptedFromTheDaemon_IsNotAStep_AndNeverUndone()
    {
        var (ctrl, clock, sink) = Editor(OneProfile());
        FanAssignRow fan = ctrl.Fans.Single();

        fan.Name = "Vorher"; // a step from before the calibration (its snapshot still says MinPwm 40)
        clock.AdvanceMs(1000);
        AppConfig calibrated = WithMinPwm(OneProfile(), 96);
        ctrl.UpdateLive(Snapshot(calibrated)); // the daemon calibrated: MinPwm 40 → 96
        Assert.Equal(96, fan.MinPwm);
        clock.AdvanceMs(1000);
        ctrl.Curves.Single().Name = "Danach";

        ctrl.UndoCommand.Execute(null);
        ctrl.UndoCommand.Execute(null);
        Assert.False(ctrl.UndoCommand.CanExecute(null)); // the calibration was no step of its own

        Assert.Equal("thinkpad pwm1", fan.Name);
        Assert.Equal("Quiet", ctrl.Curves.Single().Name);
        Assert.Equal(96, fan.MinPwm);            // the calibrated start point stays
        Assert.False(ctrl.HasUnsavedChanges);    // and is no unsaved change either

        // Redo steps carry it as well.
        ctrl.RedoCommand.Execute(null);
        Assert.Equal(96, fan.MinPwm);
        ctrl.SaveCommand.Execute(null);
        Assert.Equal((byte)96, sink.Saved.Single().Fans.Single().MinPwm);
    }

    [Fact]
    public void StoredCalibration_AdoptedFromTheDaemon_IsNoUnsavedChange_AndSurvivesDiscardAndUndo()
    {
        // The row writes its calibration into the config (max RPM for the airflow balance), so a result the daemon
        // stored must reach the baseline and every step - otherwise the bar shows „Lüfter" until the next save.
        var (ctrl, clock, _) = Editor(OneProfile());
        FanAssignRow fan = ctrl.Fans.Single();
        ctrl.Curves.Single().Name = "Leise";
        clock.AdvanceMs(1000);

        var calibration = new FanCalibration { StartPwm = 40, MinRpm = 900, MaxRpm = 4200 };
        AppConfig calibrated = OneProfile() with
        {
            Fans = OneProfile().Fans.Select(f => f with { Calibration = calibration }).ToList(),
        };
        ctrl.UpdateLive(Snapshot(calibrated));
        Assert.Equal(4200, fan.MaxRpm);
        Assert.Equal("Geändert: 1 Kurve", ctrl.DirtySummary); // the curve edit only, no „Lüfter"

        ctrl.RevertCommand.Execute(null);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Equal(4200, fan.ToConfig().Calibration?.MaxRpm);

        ctrl.Curves.Single().Name = "Danach";
        clock.AdvanceMs(1000);
        ctrl.UndoCommand.Execute(null);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void CalibratedMinPwm_EqualToTheDraft_StillReachesBaselineAndHistory()
    {
        // The draft already shows the calibrated value, so the row does not change - but baseline and history still
        // hold the old, lower start point. Discard or undo must not bring it back, and neither may the next save.
        var (ctrl, clock, sink) = Editor(OneProfile());
        FanAssignRow fan = ctrl.Fans.Single();
        fan.MinPwm = 96;
        clock.AdvanceMs(1000);
        ctrl.Curves.Single().Name = "Leise";
        clock.AdvanceMs(1000);

        ctrl.UpdateLive(Snapshot(WithMinPwm(OneProfile(), 96))); // the daemon calibrated: 40 → 96

        ctrl.RevertCommand.Execute(null);
        Assert.Equal(96, fan.MinPwm);
        Assert.Equal("Quiet", ctrl.Curves.Single().Name);
        Assert.False(ctrl.HasUnsavedChanges);
        while (ctrl.UndoCommand.CanExecute(null))
        {
            ctrl.UndoCommand.Execute(null);
            Assert.Equal(96, fan.MinPwm);
        }

        ctrl.Curves.Single().Name = "Unrelated";
        ctrl.SaveCommand.Execute(null);
        Assert.Equal((byte)96, sink.Saved.Single().Fans.Single().MinPwm);
    }

    [Fact]
    public void SaveEcho_OfTheOwnMinPwm_IsNoLiveChange()
    {
        // The daemon reporting back what the editor just saved must not flatten the history: the edit stays undoable.
        var (ctrl, clock, _) = Editor(OneProfile());
        FanAssignRow fan = ctrl.Fans.Single();
        fan.MinPwm = 80;
        ctrl.SaveCommand.Execute(null);
        clock.AdvanceMs(1000);

        ctrl.UpdateLive(Snapshot(WithMinPwm(OneProfile(), 80)));
        ctrl.UndoCommand.Execute(null);

        Assert.Equal(40, fan.MinPwm);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void CalibratedMinPwm_KeepsAnUnsavedMaxPwmDraft()
    {
        // The daemon changes only MinPwm. An unsaved MaxPwm edit must stay an edit - neither absorbed into the
        // baseline (it would never be saved) nor flattened in the history.
        var (ctrl, clock, _) = Editor(OneProfile());
        FanAssignRow fan = ctrl.Fans.Single();
        fan.MaxPwm = 200;
        clock.AdvanceMs(1000);

        ctrl.UpdateLive(Snapshot(WithMinPwm(OneProfile(), 96)));

        Assert.Equal(96, fan.MinPwm);
        Assert.Equal(200, fan.MaxPwm);
        Assert.True(ctrl.HasUnsavedChanges);
        ctrl.UndoCommand.Execute(null);
        Assert.Equal(255, fan.MaxPwm);
        Assert.Equal(96, fan.MinPwm);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void CurveToggledLive_IsNeverUndone()
    {
        var (ctrl, clock, sink) = Editor(OneProfile());
        CurveEditRow curve = ctrl.Curves.Single();

        curve.Name = "Leise";
        clock.AdvanceMs(1000);
        ctrl.SetCurveEnabled(curve, false); // live: the daemon persists it on its own
        Assert.Equal(("c1", false), Assert.Single(sink.Toggles));

        ctrl.UndoCommand.Execute(null);

        CurveEditRow restored = ctrl.Curves.Single();
        Assert.Equal("Quiet", restored.Name);
        Assert.False(restored.Enabled);         // the live toggle survives the undo …
        Assert.False(ctrl.HasUnsavedChanges);   // … and the baseline knows it
        Assert.Single(sink.Toggles);            // undo sends nothing to the daemon
        Assert.False(ctrl.UndoCommand.CanExecute(null)); // the toggle was no step of its own
    }

    [Fact]
    public void RunningProfileSwitchedLive_IsNeverUndone()
    {
        var (ctrl, clock, sink) = Editor(TwoProfiles());
        ctrl.Fans.Single().Name = "Umbenannt";
        ctrl.SaveCommand.Execute(null);
        clock.AdvanceMs(1000);

        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2"); // only opens it - no step
        ctrl.SelectedProfileActive = true;                               // live switch
        Assert.Equal("p2", Assert.Single(sink.Activated));

        ctrl.UndoCommand.Execute(null); // the rename (history survives the save)

        Assert.Equal("thinkpad pwm1", ctrl.Fans.Single().Name);
        Assert.Equal("p2", ctrl.ActiveProfile!.Id); // still the running one
        Assert.Single(sink.Activated);             // undo switched nothing
        Assert.True(ctrl.HasUnsavedChanges);       // the rename was saved - undoing it is a draft change
        Assert.False(ctrl.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void RunningProfile_MissingFromAnEarlierStep_IsKept()
    {
        var (ctrl, clock, sink) = Editor(OneProfile());
        ctrl.AddProfileCommand.Execute(null); // p-new, its own step
        ProfileRow added = ctrl.SelectedProfile!;
        ctrl.SaveCommand.Execute(null);
        clock.AdvanceMs(1000);
        ctrl.SelectedProfileActive = true;    // the new profile runs now
        Assert.Equal(added.Id, Assert.Single(sink.Activated));

        ctrl.UndoCommand.Execute(null);       // back to before the profile existed

        // Falling back to p1 here would let the next save switch the running profile without an activation.
        Assert.Contains(ctrl.Profiles, p => p.Id == added.Id);
        Assert.Equal(added.Id, ctrl.ActiveProfile!.Id);
        ctrl.SaveCommand.Execute(null);
        AppConfig sent = sink.Saved[^1];
        Assert.Equal(added.Id, sent.ActiveProfileId);
        Assert.Contains(sent.Profiles, p => p.Id == added.Id);
        Assert.Single(sink.Activated);
    }

    [Fact]
    public void Resync_ClearsTheHistory()
    {
        // After an import, reset or the assistant, undo must not bring the replaced configuration back.
        var (ctrl, clock, _) = Editor(OneProfile());
        ctrl.Curves.Single().Name = "Leise";
        clock.AdvanceMs(1000);
        Assert.True(ctrl.UndoCommand.CanExecute(null));

        ctrl.Resync(Snapshot(TwoProfiles()));

        Assert.False(ctrl.UndoCommand.CanExecute(null));
        Assert.False(ctrl.RedoCommand.CanExecute(null));
        ctrl.UndoCommand.Execute(null); // a direct call is a no-op as well
        Assert.Equal(2, ctrl.Profiles.Count);
    }

    [Fact]
    public void SelectingAProfile_IsNotAStep()
    {
        var (ctrl, _, _) = Editor(TwoProfiles());

        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p1");

        Assert.False(ctrl.UndoCommand.CanExecute(null));
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Undo_PutsTheSelectionBack_WhereTheChangeHappened()
    {
        var (ctrl, clock, _) = Editor(TwoProfiles());
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");
        ctrl.Curves.Single().Name = "Nur in Zwei";
        clock.AdvanceMs(1000);
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p1");

        ctrl.UndoCommand.Execute(null);

        Assert.Equal("p2", ctrl.SelectedProfile!.Id);
        Assert.Equal("Quiet", ctrl.Curves.Single().Name);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Undo_KeepsTheSensorGroups()
    {
        var (ctrl, clock, sink) = Editor(OneProfile());
        ctrl.Fans.Single().Name = "Umbenannt";
        clock.AdvanceMs(1000);
        ctrl.Curves.Single().Name = "Leise";

        ctrl.UndoCommand.Execute(null);
        ctrl.SaveCommand.Execute(null);

        Assert.Equal(new[] { "CPU", "Mainboard" }, ctrl.SensorGroups);
        Assert.Equal(new[] { "CPU", "Mainboard" }, sink.Saved.Single().SensorGroups);
    }

    [Fact]
    public void History_SurvivesSave()
    {
        var (ctrl, _, sink) = Editor(OneProfile());
        ctrl.Curves.Single().Name = "Leise";
        ctrl.SaveCommand.Execute(null);
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.UndoCommand.Execute(null);

        Assert.Equal("Quiet", ctrl.Curves.Single().Name);
        Assert.True(ctrl.HasUnsavedChanges); // differs from what was saved now
        Assert.Single(sink.Saved);
    }

    [Fact]
    public void DiscardAll_IsAStep_CtrlZBringsTheChangesBack()
    {
        var (ctrl, _, _) = Editor(OneProfile());
        ctrl.Curves.Single().Name = "Leise";
        ctrl.Fans.Single().Name = "Umbenannt";

        ctrl.RevertCommand.Execute(null);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Equal("Quiet", ctrl.Curves.Single().Name);

        ctrl.UndoCommand.Execute(null);
        Assert.Equal("Leise", ctrl.Curves.Single().Name);
        Assert.Equal("Umbenannt", ctrl.Fans.Single().Name);
    }

    [Fact]
    public void Save_NeedsTheConnection_UndoDoesNot()
    {
        var (ctrl, _, _) = Editor(OneProfile());
        ctrl.Curves.Single().Name = "Leise";
        Assert.True(ctrl.SaveCommand.CanExecute(null));

        ctrl.IsConnected = false;

        Assert.False(ctrl.SaveCommand.CanExecute(null));
        Assert.True(ctrl.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void Save_OnlyWhileSomethingIsUnsaved()
    {
        var (ctrl, _, _) = Editor(OneProfile());
        Assert.False(ctrl.SaveCommand.CanExecute(null)); // Ctrl+S on a clean editor does nothing
    }

    // --- Change markers ---------------------------------------------------------------------

    [Fact]
    public void Markers_CurveInADuplicatedProfile_OnlyThatProfile()
    {
        var (ctrl, _, _) = Editor(TwoProfiles());
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");

        ctrl.Curves.Single().Name = "Nur in Zwei";

        Assert.True(ctrl.Curves.Single().IsDirty);
        Assert.True(ctrl.Profiles.Single(p => p.Id == "p2").IsDirty);
        Assert.False(ctrl.Profiles.Single(p => p.Id == "p1").IsDirty); // shares the curve id, but is untouched
        Assert.True(ctrl.CurvesAreaDirty);
        Assert.False(ctrl.SettingsAreaDirty);
        Assert.Equal("Geändert: 1 Kurve", ctrl.DirtySummary);

        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p1");
        Assert.False(ctrl.Curves.Single().IsDirty); // p1's c1 row
    }

    [Fact]
    public void Markers_SettingsArea_AndSummary()
    {
        var (ctrl, clock, _) = Editor(OneProfile());

        ctrl.Fans.Single().Name = "Umbenannt";
        Assert.True(ctrl.SettingsAreaDirty);
        Assert.False(ctrl.CurvesAreaDirty);
        Assert.Equal("Geändert: Lüfter", ctrl.DirtySummary);

        clock.AdvanceMs(1000);
        ctrl.Sensors.First().Name = "CPU-Paket";
        ctrl.AddProfileCommand.Execute(null);
        Assert.Equal("Geändert: 1 Kurve, Profile, Lüfter, Sensoren & Gruppen", ctrl.DirtySummary);

        ctrl.RevertCommand.Execute(null);
        Assert.False(ctrl.SettingsAreaDirty);
        Assert.False(ctrl.CurvesAreaDirty);
        Assert.Equal("", ctrl.DirtySummary);
    }

    [Fact]
    public void Markers_EditWithinABurst_FollowAtTheNextTick()
    {
        // While a burst runs, the comparison waits for the tick instead of running per drag sample.
        var (ctrl, clock, _) = Editor(OneProfile());
        ctrl.Curves.Single().Name = "Leise"; // clean → dirty: compared at once
        clock.AdvanceMs(100);
        ctrl.Fans.Single().Name = "Umbenannt"; // same burst: deferred
        Assert.False(ctrl.SettingsAreaDirty);

        Settle(ctrl, clock, OneProfile());

        Assert.True(ctrl.SettingsAreaDirty);
    }

    [Fact]
    public void DirtySummary_FollowsALanguageSwitch_UntilDisposed()
    {
        var (ctrl, _, _) = Editor(OneProfile());
        ctrl.Fans.Single().Name = "Umbenannt";
        var raised = new List<string?>();
        ctrl.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);
            Assert.Contains(nameof(CurveEditorController.DirtySummary), raised);
            Assert.Equal("Changed: fans", ctrl.DirtySummary);

            ctrl.Dispose();
            raised.Clear();
            Localizer.Instance.SetLanguage(LanguageChoice.German);
            Assert.DoesNotContain(nameof(CurveEditorController.DirtySummary), raised);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // restore the pinned test culture
        }
    }

    // --- „Lüfter N": saving fills an empty fan name, and the history follows -------------------------------------------

    private static AppConfig Unnamed() => OneProfile() with
    {
        Fans = OneProfile().Fans.Select(f => f with { Name = "" }).ToList(),
    };

    [Fact]
    public async Task AfterASave_UndoOfALaterEdit_IsClean()
    {
        var (ctrl, clock, _) = Editor(Unnamed());
        ctrl.Curves.Single().Name = "Leise";
        await ctrl.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Lüfter 1", ctrl.Fans.Single().Name);

        Settle(ctrl, clock, Unnamed());
        ctrl.Curves.Single().Name = "Leiser";
        ctrl.UndoCommand.Execute(null);

        Assert.Equal("Lüfter 1", ctrl.Fans.Single().Name); // the name went into the history too
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public async Task AfterASave_UndoThenRedo_IsClean()
    {
        var (ctrl, _, _) = Editor(Unnamed());
        ctrl.Curves.Single().Name = "Leise";
        await ctrl.SaveCommand.ExecuteAsync(null);

        ctrl.UndoCommand.Execute(null);
        Assert.Equal(("Quiet", "Lüfter 1"), (ctrl.Curves.Single().Name, ctrl.Fans.Single().Name));
        ctrl.RedoCommand.Execute(null);

        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void ALanguageSwitch_RenamesTheDefault_AndChangesNothing()
    {
        var (ctrl, _, _) = Editor(Unnamed());
        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);

            Assert.Equal("Fan 1", ctrl.Fans.Single().DefaultName);
            Assert.False(ctrl.HasUnsavedChanges); // an empty field is no own name, in any language
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // restore the pinned test culture
        }
    }

    [Fact]
    public void AFanNotDetectedRightNow_KeepsItsNumber()
    {
        AppConfig config = Unnamed() with
        {
            Fans = [.. Unnamed().Fans, new FanConfig { FanId = "usb/fan1", Name = "Lüfter 1" }],
        };

        var (ctrl, _, _) = Editor(config);

        Assert.Equal("Lüfter 2", ctrl.Fans.Single().DefaultName);
    }

    // --- Edits that used to lose data or leak across profiles ---------------------------------

    [Fact]
    public void AddProfile_KeepsTheUnsavedEditsOfTheProfileShownBefore()
    {
        var (ctrl, _, sink) = Editor(OneProfile());
        ctrl.Curves.Single().Name = "Leise";

        ctrl.AddProfileCommand.Execute(null);
        ctrl.SaveCommand.Execute(null);

        Assert.Equal("Leise", sink.Saved.Single().Profiles.Single(p => p.Id == "p1").Curves.Single().Name);
    }

    [Fact]
    public void DuplicateProfile_KeepsTheOriginalsUnsavedEdits()
    {
        var (ctrl, _, sink) = Editor(OneProfile());
        ctrl.Curves.Single().Name = "Leise";

        ctrl.DuplicateProfileCommand.Execute(null);
        ctrl.SaveCommand.Execute(null);

        AppConfig sent = sink.Saved.Single();
        Assert.All(sent.Profiles, p => Assert.Equal("Leise", p.Curves.Single().Name));
        Assert.Equal("Leise", sent.Curves.Single().Name); // p1 still runs and carries the edit
    }

    [Fact]
    public void DashboardToggle_WhileAnotherProfileIsOpen_StaysLive()
    {
        // p2 shares the curve id c1. Switching the RUNNING c1 off must neither touch p2's c1 nor leave the
        // running profile's own curve list behind (the next save would switch the curve back on).
        var (ctrl, _, sink) = Editor(TwoProfiles());
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");
        CurveEditRow live = ctrl.LiveCurves.Single();

        ctrl.SetCurveEnabled(live, false);

        Assert.Equal(("c1", false), Assert.Single(sink.Toggles));
        Assert.True(ctrl.Curves.Single().Enabled); // p2's c1
        Assert.False(ctrl.HasUnsavedChanges);
        ctrl.Curves.Single().Name = "Zwei";
        ctrl.SaveCommand.Execute(null);
        AppConfig sent = sink.Saved.Single();
        Assert.False(sent.Curves.Single().Enabled);
        Assert.False(sent.Profiles.Single(p => p.Id == "p1").Curves.Single().Enabled);
        Assert.True(sent.Profiles.Single(p => p.Id == "p2").Curves.Single().Enabled);
    }
}
