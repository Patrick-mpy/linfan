// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Switching the running profile (header menu, curves tab): never locked by unsaved changes - it asks; the exact IPC per
/// path (one activation, or one config - never a save followed by an activation); a switch is never a history step;
/// the daemon's snapshot confirms it, and the editor follows the daemon otherwise.
/// </summary>
public sealed class ProfileSwitchTests
{
    private const string Fan = "hwmon7/pwm1";

    private static readonly CurveConfig Quiet = new()
    {
        Id = "quiet",
        Name = "Quiet",
        SourceSensorIds = new[] { "hwmon6/temp1" },
        Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
    };

    private static readonly CurveConfig Loud = new()
    {
        Id = "loud",
        Name = "Loud",
        SourceSensorIds = new[] { "hwmon6/temp1" },
        Points = new[] { new CurvePoint(30, 60), new CurvePoint(80, 100) },
    };

    private sealed class Sink
    {
        public List<AppConfig> Saved { get; } = new();
        public List<string> Activated { get; } = new();
        public bool Result { get; set; } = true;

        public Task<bool> SaveAsync(AppConfig config, IReadOnlyDictionary<string, byte> baseMinPwm)
        {
            Saved.Add(config);
            return Task.FromResult(Result);
        }

        public Task ActivateAsync(string id)
        {
            Activated.Add(id);
            return Task.CompletedTask;
        }
    }

    /// <summary>"Leise" (p1, one curve) runs; "Laut" (p2) drives the same fan with its own curve.</summary>
    private static AppConfig Config(string running = "p1") => ProfileService.Apply(new AppConfig
    {
        Sensors = new[] { new SensorConfig { SensorId = "hwmon6/temp1", Name = "CPU" } },
        Fans = new[] { new FanConfig { FanId = Fan, Name = "CPU Fan", MinPwm = 40, MaxPwm = 255 } },
        Curves = new[] { Quiet },
        Profiles = new[]
        {
            new Profile { Id = "p1", Name = "Leise", Curves = new[] { Quiet }, Assignments = new[] { new ProfileAssignment(Fan, "quiet") } },
            new Profile { Id = "p2", Name = "Laut", Curves = new[] { Loud }, Assignments = new[] { new ProfileAssignment(Fan, "loud") } },
        },
        ActiveProfileId = "p1",
    }, running);

    private static MonitorSnapshot Snapshot(AppConfig config) => new(
        "test",
        new[] { new SensorReading("hwmon6/temp1", "CPU", SensorKind.Temperature, "°C", 45) },
        new[] { new FanReading(Fan, "CPU Fan", 1200, 120, true) },
        config,
        Connected: true);

    private static (CurveEditorController Ctrl, Sink Sink) Editor()
    {
        var sink = new Sink();
        var ctrl = new CurveEditorController(sink.SaveAsync, sink.ActivateAsync, clock: new ManualClock()) { IsConnected = true };
        ctrl.Initialize(Snapshot(Config()));
        return (ctrl, sink);
    }

    private static List<ProfileSwitchRequest> Questions(CurveEditorController ctrl)
    {
        var asked = new List<ProfileSwitchRequest>();
        ctrl.ProfileSwitchConfirmationRequested += (_, r) => asked.Add(r);
        return asked;
    }

    [Fact]
    public void CleanSwitch_SendsOneActivation_AndNoConfig_AndIsNoHistoryStep()
    {
        var (ctrl, sink) = Editor();
        List<ProfileSwitchRequest> asked = Questions(ctrl);

        ctrl.RequestProfileSwitch("p2");

        Assert.Empty(asked);
        Assert.Equal(new[] { "p2" }, sink.Activated);
        Assert.Empty(sink.Saved);
        Assert.Equal("p2", ctrl.ActiveProfile!.Id);
        Assert.True(ctrl.Profiles.Single(p => p.Id == "p2").IsActive);
        Assert.False(ctrl.HasUnsavedChanges);           // the daemon persists the switch itself
        Assert.False(ctrl.UndoCommand.CanExecute(null)); // not a step: Ctrl+Z never switches back
        Assert.Equal("Auf „Laut“ gewechselt.", ctrl.Status.Text);
        Assert.Equal(StatusIcon.Swap, ctrl.Status.Icon);
        Assert.Equal("Rückgängig", ctrl.Status.ActionLabel);
    }

    [Fact]
    public void SwitchBack_IsANewLiveAction_NotAHistoryStep()
    {
        var (ctrl, sink) = Editor();
        ctrl.Fans.Single().Name = "Umbenannt"; // one real edit in the history …
        ctrl.SaveCommand.Execute(null);        // … saved, so nothing is unsaved
        ctrl.RequestProfileSwitch("p2");
        System.Windows.Input.ICommand back = ctrl.Status.ActionCommand!;
        Assert.True(back.CanExecute(null));

        back.Execute(null);

        Assert.Equal(new[] { "p2", "p1" }, sink.Activated);
        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.Equal("Zurück auf „Leise“.", ctrl.Status.Text);
        Assert.False(ctrl.Status.HasAction);
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.UndoCommand.Execute(null); // the history holds the rename, not the switches
        Assert.Equal("CPU Fan", ctrl.Fans.Single().Name);
        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.Equal(2, sink.Activated.Count);
    }

    [Fact]
    public void SwitchBack_OnlyWhileItWouldUndoThatSwitch()
    {
        var (ctrl, _) = Editor();
        ctrl.RequestProfileSwitch("p2");
        System.Windows.Input.ICommand back = ctrl.Status.ActionCommand!;
        int raised = 0;
        back.CanExecuteChanged += (_, _) => raised++;

        ctrl.IsConnected = false;
        Assert.False(back.CanExecute(null)); // offline the command would go nowhere
        Assert.True(raised > 0);
        ctrl.IsConnected = true;
        Assert.True(back.CanExecute(null));

        ctrl.UpdateLive(Snapshot(Config("p1"))); // still unconfirmed - a tick of the old state changes nothing yet
        Assert.True(back.CanExecute(null));

        ctrl.Fans.Single().Name = "Umbenannt"; // an edit: the switch-back would need the question now …
        Assert.False(back.CanExecute(null));
        Assert.False(ctrl.Status.HasAction);   // … and the edit drops the toast
        Assert.Equal("", ctrl.Status.Text);
    }

    [Fact]
    public void SwitchWithUnsavedChanges_AsksFirst_AndSendsNothing()
    {
        var (ctrl, sink) = Editor();
        List<ProfileSwitchRequest> asked = Questions(ctrl);
        ctrl.Curves.Single().Name = "Quiet 2";

        ctrl.RequestProfileSwitch("p2");

        ProfileSwitchRequest request = Assert.Single(asked);
        Assert.Equal("p2", request.ProfileId);
        Assert.Equal("Auf „Laut“ wechseln?", request.Title);
        Assert.Equal("Der Dienst aktiviert den gespeicherten Stand des Profils. Deine Änderungen (1 Kurve) sind noch nicht übernommen.",
            request.Message);
        Assert.Null(request.DiscardBlockedReason); // "Laut" is saved: discarding leaves something to switch to
        Assert.Empty(sink.Activated);
        Assert.Empty(sink.Saved);
        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
    }

    /// <summary>
    /// The daemon applies one pending change per tick, an activation before a save: a save followed by a separate
    /// activation would switch to the stored profile first and back with the save. So it is ONE config.
    /// </summary>
    [Fact]
    public async Task SaveAndSwitch_SendsOneConfig_WithTheTargetRunning_AndNoActivation()
    {
        var (ctrl, sink) = Editor();
        ctrl.Curves.Single().Name = "Quiet 2";

        Assert.True(await ctrl.SaveAndSwitchAsync("p2"));

        AppConfig sent = Assert.Single(sink.Saved);
        Assert.Empty(sink.Activated);
        Assert.Equal("p2", sent.ActiveProfileId);
        Assert.Equal("loud", Assert.Single(sent.Curves).Id);              // the running curves are the target's
        Assert.Equal("loud", Assert.Single(sent.Fans).AssignedCurveId);
        Assert.Equal("Quiet 2", sent.Profiles.Single(p => p.Id == "p1").Curves.Single().Name); // the draft is saved
        Assert.Equal("p2", ctrl.ActiveProfile!.Id);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Equal("Auf „Laut“ gewechselt.", ctrl.Status.Text);
        Assert.Equal(StatusIcon.Swap, ctrl.Status.Icon);
    }

    [Fact]
    public async Task SaveAndSwitch_HoldsFurtherSwitches_UntilTheSnapshotConfirmsIt()
    {
        var (ctrl, sink) = Editor();
        ctrl.Curves.Single().Name = "Quiet 2";
        await ctrl.SaveAndSwitchAsync("p2");
        System.Windows.Input.ICommand back = ctrl.Status.ActionCommand!;

        // Not applied yet: an activation now could reach the daemon before the save and be undone by it.
        Assert.False(ctrl.CanSwitchProfile);
        Assert.False(back.CanExecute(null));
        Assert.Equal("Der letzte Wechsel wird gerade übernommen …", ctrl.ProfileSwitchBlockedReason);
        ctrl.RequestProfileSwitch("p1");
        Assert.Empty(sink.Activated);

        ctrl.UpdateLive(Snapshot(sink.Saved.Single())); // the daemon reports "Laut" running

        Assert.True(ctrl.CanSwitchProfile);
        Assert.True(back.CanExecute(null));
        back.Execute(null);
        Assert.Equal(new[] { "p1" }, sink.Activated);
        Assert.Single(sink.Saved);
    }

    [Fact]
    public async Task WhileTheConfigIsOnItsWay_NothingElseIsSent_AndTheSnapshotIsNotTakenForAnOutsideSwitch()
    {
        var pending = new TaskCompletionSource<bool>();
        var activated = new List<string>();
        var ctrl = new CurveEditorController((_, _) => pending.Task, id => { activated.Add(id); return Task.CompletedTask; },
            clock: new ManualClock())
        { IsConnected = true };
        ctrl.Initialize(Snapshot(Config()));
        ctrl.Curves.Single().Name = "Quiet 2";

        Task<bool> saving = ctrl.SaveAndSwitchAsync("p2");

        Assert.False(ctrl.CanSwitchProfile);
        Assert.False(ctrl.SaveCommand.CanExecute(null)); // Ctrl+S could overtake it with the old running profile
        Assert.Equal("Der letzte Wechsel wird gerade übernommen …", ctrl.ProfileSwitchBlockedReason);
        ctrl.UpdateLive(Snapshot(Config("p2")));          // the daemon was quicker than the reply
        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.Equal("", ctrl.Status.Text);              // no "switched elsewhere"

        pending.SetResult(true);
        Assert.True(await saving);
        Assert.Equal("p2", ctrl.ActiveProfile!.Id);
        Assert.Equal("Auf „Laut“ gewechselt.", ctrl.Status.Text);
        Assert.Empty(activated);
    }

    [Fact]
    public async Task WhileAPlainSaveIsOnItsWay_SwitchingWaits_AndSaysWhy()
    {
        var pending = new TaskCompletionSource<bool>();
        var ctrl = new CurveEditorController((_, _) => pending.Task, _ => Task.CompletedTask, clock: new ManualClock()) { IsConnected = true };
        ctrl.Initialize(Snapshot(Config()));
        ctrl.Curves.Single().Name = "Quiet 2";

        Task saving = ctrl.SaveCommand.ExecuteAsync(null);

        Assert.False(ctrl.CanSwitchProfile);
        Assert.Equal("Die Änderungen werden gerade übernommen …", ctrl.ProfileSwitchBlockedReason);
        pending.SetResult(true);
        await saving;
        Assert.True(ctrl.CanSwitchProfile);
        Assert.Equal("", ctrl.ProfileSwitchBlockedReason);
    }

    [Fact]
    public async Task ASecondSaveAndSwitch_IsRefused_WhileTheFirstIsUnconfirmed()
    {
        var (ctrl, sink) = Editor();
        ctrl.Curves.Single().Name = "Quiet 2";
        await ctrl.SaveAndSwitchAsync("p2");
        ctrl.Curves.Single().Name = "Loud 2";

        Assert.False(await ctrl.SaveAndSwitchAsync("p1"));

        Assert.Single(sink.Saved);
        Assert.True(ctrl.Status.IsError);
        Assert.Equal("Der letzte Wechsel wird gerade übernommen …", ctrl.Status.Text);
    }

    [Fact]
    public async Task ASendThatThrows_CountsAsFailed()
    {
        var ctrl = new CurveEditorController((_, _) => throw new IOException("socket gone"), _ => Task.CompletedTask,
            clock: new ManualClock())
        { IsConnected = true };
        ctrl.Initialize(Snapshot(Config()));
        ctrl.Curves.Single().Name = "Quiet 2";

        Assert.False(await ctrl.SaveAndSwitchAsync("p2"));

        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.True(ctrl.Status.IsError);
        Assert.True(ctrl.CanSwitchProfile); // nothing left on its way
        Assert.True(ctrl.HasUnsavedChanges);
    }

    /// <summary>
    /// A save-and-switch the daemon does not confirm (it refused the config - limits, a write error) must not leave the
    /// editor believing the draft was saved: the changes are unsaved again, the running profile is the daemon's.
    /// </summary>
    [Fact]
    public async Task ARefusedSaveAndSwitch_LeavesTheChangesUnsaved_AndTheDaemonsProfileRunning()
    {
        var (ctrl, sink) = Editor();
        ctrl.Curves.Single().Name = "Quiet 2";
        await ctrl.SaveAndSwitchAsync("p2");
        Assert.False(ctrl.HasUnsavedChanges);

        for (int i = 0; i < CurveEditorController.SwitchConfirmTicks; i++)
            ctrl.UpdateLive(Snapshot(Config("p1"))); // the daemon kept its config

        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.Equal("Geändert: 1 Kurve", ctrl.DirtySummary);
        Assert.Equal("Quiet 2", ctrl.Curves.Single().Name); // the draft is still there to apply again
        Assert.True(ctrl.Status.IsError);
        Assert.Equal("„Laut“ wurde nicht aktiviert und deine Änderungen nicht übernommen - der Dienst regelt weiter mit „Leise“.",
            ctrl.Status.Text);
        Assert.True(ctrl.CanSwitchProfile);
        Assert.True(ctrl.SaveCommand.CanExecute(null));
        Assert.Empty(sink.Activated);
    }

    [Fact]
    public void ADelete_Waits_WhileASwitchIsUnconfirmed()
    {
        var (ctrl, _) = Editor();
        ctrl.RequestProfileSwitch("p2");
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p1"); // the previous profile - the daemon may keep it
        int notified = 0;
        ctrl.DeleteProfileCommand.CanExecuteChanged += (_, _) => notified++;

        Assert.False(ctrl.DeleteProfileCommand.CanExecute(null));
        ctrl.DeleteProfileCommand.Execute(null);
        Assert.Contains(ctrl.Profiles, p => p.Id == "p1");

        ctrl.UpdateLive(Snapshot(Config("p2"))); // confirmed
        Assert.True(notified > 0);
        Assert.True(ctrl.DeleteProfileCommand.CanExecute(null));
    }

    /// <summary>An undo during the save's round trip may remove the target from the draft: it comes back as the running row.</summary>
    [Fact]
    public async Task AnUndoDuringTheRoundTrip_CannotLoseTheProfileThatRunsNow()
    {
        var pending = new TaskCompletionSource<bool>();
        var saved = new List<AppConfig>();
        var ctrl = new CurveEditorController((c, _) => { saved.Add(c); return pending.Task; }, _ => Task.CompletedTask,
            clock: new ManualClock())
        { IsConnected = true };
        ctrl.Initialize(Snapshot(Config()));
        ctrl.AddProfileCommand.Execute(null);
        string draft = ctrl.SelectedProfile!.Id;

        Task<bool> saving = ctrl.SaveAndSwitchAsync(draft);
        ctrl.UndoCommand.Execute(null); // takes the new profile out of the draft
        Assert.DoesNotContain(ctrl.Profiles, p => p.Id == draft);
        pending.SetResult(true);
        Assert.True(await saving);

        ProfileRow running = Assert.Single(ctrl.Profiles, p => p.Id == draft);
        Assert.Same(running, ctrl.ActiveProfile);
        Assert.True(running.IsActive);
        ctrl.UpdateLive(Snapshot(saved.Single())); // confirmed
        Assert.Same(running, ctrl.ActiveProfile);
        Assert.False(ctrl.Status.IsError);
    }

    /// <summary>The daemon runs a profile the editor does not have (created by another client): it comes back, loudly.</summary>
    [Fact]
    public void ARunningProfileTheEditorLacks_ComesBack_WithAnError()
    {
        var (ctrl, sink) = Editor();
        AppConfig daemon = Config();
        daemon = ProfileService.Apply(daemon with
        {
            Profiles = daemon.Profiles.Append(new Profile
            {
                Id = "p3",
                Name = "Nachts",
                Curves = new[] { Quiet },
                Assignments = new[] { new ProfileAssignment(Fan, "quiet") },
            }).ToList(),
        }, "p3");

        ctrl.UpdateLive(Snapshot(daemon));

        ProfileRow night = Assert.Single(ctrl.Profiles, p => p.Id == "p3");
        Assert.Same(night, ctrl.ActiveProfile);
        Assert.True(ctrl.Status.IsError);
        Assert.Equal("Der Dienst regelt mit „Nachts“ - das Profil fehlte in deinen Änderungen und ist wieder da.", ctrl.Status.Text);
        Assert.False(ctrl.HasUnsavedChanges); // the daemon has it - no draft change
        Assert.False(ctrl.UndoCommand.CanExecute(null));
        Assert.Empty(sink.Activated);
        Assert.Empty(sink.Saved);
    }

    [Fact]
    public async Task SaveAndSwitch_ToAProfileThatIsOnlyADraft_SavesItRunning_DiscardingIsRefused()
    {
        var (ctrl, sink) = Editor();
        List<ProfileSwitchRequest> asked = Questions(ctrl);
        ctrl.AddProfileCommand.Execute(null);
        string draft = ctrl.SelectedProfile!.Id;

        ctrl.RequestProfileSwitch(draft);
        Assert.Equal("„Neues Profil“ gibt es nur in den ungespeicherten Änderungen - nach dem Verwerfen gäbe es nichts, worauf gewechselt werden kann.",
            Assert.Single(asked).DiscardBlockedReason);
        Assert.False(ctrl.DiscardAndSwitch(draft)); // a direct call is refused as well - the draft stays
        Assert.True(ctrl.Status.IsError);
        Assert.StartsWith("„Neues Profil“ gibt es nur", ctrl.Status.Text);
        Assert.Contains(ctrl.Profiles, p => p.Id == draft);
        Assert.True(ctrl.HasUnsavedChanges);

        Assert.True(await ctrl.SaveAndSwitchAsync(draft));

        AppConfig sent = Assert.Single(sink.Saved);
        Assert.Equal(draft, sent.ActiveProfileId);
        Assert.Contains(sent.Profiles, p => p.Id == draft);
        Assert.Empty(sink.Activated);
        Assert.Equal(draft, ctrl.ActiveProfile!.Id);
    }

    [Fact]
    public async Task SaveAndSwitch_ThatFails_KeepsTheDraft_AndSwitchesNothing()
    {
        var (ctrl, sink) = Editor();
        sink.Result = false;
        ctrl.Curves.Single().Name = "Quiet 2";

        Assert.False(await ctrl.SaveAndSwitchAsync("p2"));

        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.True(ctrl.Status.IsError);
        Assert.True(ctrl.CanSwitchProfile); // nothing on its way
        Assert.Empty(sink.Activated);
    }

    [Fact]
    public void DiscardAndSwitch_IsAHistoryStep_SwitchesLive_AndFindsTheTargetById()
    {
        var (ctrl, sink) = Editor();
        ProfileRow loudBefore = ctrl.Profiles.Single(p => p.Id == "p2");
        ctrl.Curves.Single().Name = "Quiet 2";

        Assert.True(ctrl.DiscardAndSwitch("p2"));

        Assert.Equal(new[] { "p2" }, sink.Activated);
        Assert.Empty(sink.Saved);
        // Discarding rebuilt the rows: the running one is the new row, not the stale one from before.
        Assert.Same(ctrl.Profiles.Single(p => p.Id == "p2"), ctrl.ActiveProfile);
        Assert.NotSame(loudBefore, ctrl.ActiveProfile);
        Assert.True(ctrl.ActiveProfile!.IsActive);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Equal("Auf „Laut“ gewechselt - Änderungen verworfen (Strg+Z holt sie zurück).", ctrl.Status.Text);

        ctrl.UndoCommand.Execute(null); // Ctrl+Z brings the draft back - and does not switch back

        Assert.Equal("Quiet 2", CurveNameIn(ctrl, "p1"));
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.Equal("p2", ctrl.ActiveProfile!.Id);
        Assert.Single(sink.Activated);
    }

    /// <summary>The name of the first curve of a profile as the editor holds it (the shown one from the editor).</summary>
    private static string CurveNameIn(CurveEditorController ctrl, string profileId) =>
        ReferenceEquals(ctrl.SelectedProfile, ctrl.Profiles.Single(p => p.Id == profileId))
            ? ctrl.Curves.First().Name
            : ctrl.Profiles.Single(p => p.Id == profileId).Curves.First().Name;

    [Fact]
    public async Task Offline_NoSwitchPathSendsAnything()
    {
        var (ctrl, sink) = Editor();
        List<ProfileSwitchRequest> asked = Questions(ctrl);
        ctrl.IsConnected = false;

        ctrl.RequestProfileSwitch("p2");
        ctrl.Curves.Single().Name = "Quiet 2";
        ctrl.RequestProfileSwitch("p2");
        Assert.False(await ctrl.SaveAndSwitchAsync("p2"));
        Assert.False(ctrl.DiscardAndSwitch("p2"));

        Assert.Empty(asked);
        Assert.Empty(sink.Activated);
        Assert.Empty(sink.Saved);
        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.Equal("Quiet 2", ctrl.Curves.Single().Name); // the draft is untouched
        Assert.StartsWith("Nicht verbunden", ctrl.ProfileSwitchBlockedReason);
        Assert.True(ctrl.Status.IsError);                   // a refused answer of the dialog says why
        Assert.StartsWith("Nicht verbunden", ctrl.Status.Text);
    }

    [Fact]
    public void BeforeTheFirstSnapshot_NothingCanSwitch()
    {
        var sink = new Sink();
        var ctrl = new CurveEditorController(sink.SaveAsync, sink.ActivateAsync) { IsConnected = true };

        ctrl.RequestProfileSwitch("p2");

        Assert.False(ctrl.CanSwitchProfile);
        Assert.Empty(sink.Activated);
    }

    [Fact]
    public void ASwitchTheSnapshotDoesNotConfirm_IsReported_AndTheEditorFollowsTheDaemon()
    {
        var (ctrl, sink) = Editor();
        ctrl.RequestProfileSwitch("p2");

        for (int i = 1; i < CurveEditorController.SwitchConfirmTicks; i++)
            ctrl.UpdateLive(Snapshot(Config("p1"))); // the daemon has not got there yet
        Assert.Equal("p2", ctrl.ActiveProfile!.Id);
        Assert.False(ctrl.Status.IsError);

        ctrl.UpdateLive(Snapshot(Config("p1")));

        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.True(ctrl.Profiles.Single(p => p.Id == "p1").IsActive);
        Assert.True(ctrl.Status.IsError);
        Assert.Equal("„Laut“ wurde nicht aktiviert - der Dienst regelt weiter mit „Leise“.", ctrl.Status.Text);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Equal(new[] { "p2" }, sink.Activated); // following sends nothing
    }

    [Fact]
    public void AConfirmedSwitch_Stays()
    {
        var (ctrl, _) = Editor();
        ctrl.RequestProfileSwitch("p2");

        for (int i = 0; i < 2 * CurveEditorController.SwitchConfirmTicks; i++)
            ctrl.UpdateLive(Snapshot(Config("p2")));

        Assert.Equal("p2", ctrl.ActiveProfile!.Id);
        Assert.False(ctrl.Status.IsError);
    }

    [Fact]
    public void ASwitchMadeElsewhere_IsFollowed_WithoutSendingAnything_AndKeepsTheDraft()
    {
        var (ctrl, sink) = Editor();
        ctrl.Fans.Single().Name = "Umbenannt";

        ctrl.UpdateLive(Snapshot(Config("p2")));

        Assert.Equal("p2", ctrl.ActiveProfile!.Id);
        Assert.True(ctrl.Profiles.Single(p => p.Id == "p2").IsActive);
        Assert.Equal("Der Dienst regelt jetzt mit „Laut“.", ctrl.Status.Text);
        Assert.Empty(sink.Activated);
        Assert.Empty(sink.Saved);
        Assert.True(ctrl.HasUnsavedChanges);                     // the rename is still a draft …
        Assert.Equal("Geändert: Lüfter", ctrl.DirtySummary);     // … and the switch is not part of it
        ctrl.UndoCommand.Execute(null);
        Assert.False(ctrl.HasUnsavedChanges);                    // undo does not bring the old running profile back
        Assert.Equal("p2", ctrl.ActiveProfile!.Id);
    }

    [Fact]
    public void ProfileSummary_CountsCurvesAndDistinctFans_TheShownProfileFromTheEditor()
    {
        var (ctrl, _) = Editor();
        ProfileRow quiet = ctrl.Profiles.Single(p => p.Id == "p1");
        Assert.Equal("1 Kurve · 1 Lüfter", ctrl.ProfileSummary(quiet));

        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced); // the open profile, not yet in its row
        Assert.Equal("2 Kurven · 1 Lüfter", ctrl.ProfileSummary(quiet));
        ctrl.Fans.Single().Selected = null;
        Assert.Equal("2 Kurven · 0 Lüfter", ctrl.ProfileSummary(quiet));
    }
}
