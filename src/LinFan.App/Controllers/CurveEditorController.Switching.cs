// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

// Switching the running profile (header menu, the curves tab's banner and switch). Unsaved changes do not lock it - they
// make it ask first. A switch is a live action the daemon persists itself: it is never a history step, and undo never
// takes it back. The daemon's snapshot confirms it; a switch it does not confirm is reported and the editor follows the
// daemon - as it follows any change of the running profile made elsewhere.
public partial class CurveEditorController
{
    /// <summary>
    /// How many live ticks a switch may take until the snapshot has to show it. The daemon applies one pending change per
    /// tick and ticks every 0.2 to 5 s; the GUI reads once a second.
    /// </summary>
    public const int SwitchConfirmTicks = 10;

    private string? _pendingSwitchId;   // the profile the daemon is expected to report as running
    private int _pendingSwitchTicks;
    private bool _pendingSwitchViaSave; // the pending switch rides on a saved config (SaveAndSwitchAsync)
    private string? _preSwitchBaselineJson; // the baseline before that save - back in place if the daemon does not confirm
    private RelayCommand? _switchBackCommand; // "Rückgängig" in the toast of the last switch

    /// <summary>A switch needs the user's decision (unsaved changes): the view shows the dialog and answers.</summary>
    public event EventHandler<ProfileSwitchRequest>? ProfileSwitchConfirmationRequested;

    /// <summary>
    /// Whether the running profile can be switched now: loaded and connected (offline the command would go nowhere, yet the
    /// editor would rebase onto the new profile), no config on its way and no save-and-switch waiting for its confirmation.
    /// A switch sent meanwhile could reach the daemon before the save and be undone by it - holding it narrows that race;
    /// the daemon's own ordering of pending changes closes it.
    /// </summary>
    public bool CanSwitchProfile => _initialized && IsConnected && !_saveInFlight && !_pendingSwitchViaSave;

    /// <summary>Why switching is not possible right now; empty while it is (or before anything is loaded).</summary>
    public string ProfileSwitchBlockedReason =>
        !IsConnected ? Localizer.Instance["MainWindow.ProfileSwitchOffline"]
        : _switchSaveInFlight || _pendingSwitchViaSave ? Localizer.Instance["ProfileSwitch.Pending"]
        : _saveInFlight ? Localizer.Instance["ProfileSwitch.Saving"]
        : "";

    /// <summary>
    /// Switches the running profile to <paramref name="profileId"/>: right away while nothing is unsaved, otherwise after
    /// the user's decision (<see cref="ProfileSwitchConfirmationRequested"/>). Ignored while switching is not possible,
    /// for the running profile and for an unknown id.
    /// </summary>
    public void RequestProfileSwitch(string profileId)
    {
        if (!CanSwitchProfile || Profiles.FirstOrDefault(p => p.Id == profileId) is not { } target
            || ReferenceEquals(target, ActiveProfile))
            return;

        FlushHistory(); // a deferred check may still count an edit that went back to the baseline
        if (!HasUnsavedChanges)
            SwitchLive(target, discarded: false);
        else
            ProfileSwitchConfirmationRequested?.Invoke(this, BuildSwitchRequest(target));
    }

    /// <summary>
    /// "Übernehmen &amp; wechseln": saves the draft with <paramref name="profileId"/> as the running profile - in ONE
    /// config. The daemon takes the active profile from the config it saves (<c>ConfigMapper.Merge</c>) and applies it
    /// (<c>Normalize</c> → <see cref="ProfileService.EnsureProfiles"/>). A save followed by a separate activation would
    /// race: the daemon applies one pending change per tick, an activation before a save - it would switch to the old
    /// stored profile (or refuse a new one), and the save's active id would then switch back.
    /// </summary>
    /// <returns>Whether the config went out; the snapshot confirms the switch later.</returns>
    public async Task<bool> SaveAndSwitchAsync(string profileId)
    {
        if (!CanSwitchProfile || _save is null)
        {
            SetStatus(SwitchRefusal(), isError: true);
            return false;
        }
        if (Profiles.FirstOrDefault(p => p.Id == profileId) is not { } target)
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.NotSwitched"], isError: true);
            return false;
        }
        if (ReferenceEquals(target, ActiveProfile))
        {
            await Save(); // switched elsewhere while the dialog was open - what is left is applying the changes
            return true;
        }

        FlushHistory();
        FillEmptyNames();
        StashEditorIntoSelectedProfile();
        string? previousId = ActiveProfile?.Id;
        string? baselineBefore = _savedConfigJson;
        AppConfig config = ProfileService.Apply(BuildConfig(), profileId);
        if (!await SendConfigAsync(config, switches: true))
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.SaveFailed"], isError: true);
            return false;
        }

        // The saved config switches - no activation command on top. Adopting also puts the target back if an undo during
        // the round trip removed it from the draft.
        AdoptRunningProfile(profileId, config, out _);
        BeginPendingSwitch(profileId, viaSave: true, baselineBefore);
        ShowSwitchedToast(profileId, target.Name, previousId, discarded: false);
        return true;
    }

    /// <summary>
    /// "Verwerfen &amp; wechseln": discards the draft - a history step, so Ctrl+Z brings it back - and switches live.
    /// Refused for a profile that exists only in the draft: discarding removes it, there would be nothing to switch to.
    /// </summary>
    /// <returns>Whether the draft was discarded; a refusal says why in the status toast.</returns>
    public bool DiscardAndSwitch(string profileId)
    {
        if (!CanSwitchProfile)
        {
            SetStatus(SwitchRefusal(), isError: true);
            return false;
        }
        if (SavedConfig is not { } saved || saved.Profiles.All(p => p.Id != profileId))
        {
            string name = Profiles.FirstOrDefault(p => p.Id == profileId)?.Name ?? profileId;
            SetStatus(Localizer.Instance.Format("ProfileSwitch.DiscardDraftOnly", name), isError: true);
            return false;
        }
        if (HasUnsavedChanges)
            Revert();
        // By id: discarding rebuilds the profile rows.
        if (Profiles.FirstOrDefault(p => p.Id == profileId) is not { } target)
        {
            SetStatus(Localizer.Instance["CurveEditorCtrl.NotSwitched"], isError: true);
            return false;
        }
        if (!ReferenceEquals(target, ActiveProfile)) // it may have been switched to elsewhere while the dialog was open
            SwitchLive(target, discarded: true);
        return true;
    }

    /// <summary>The status text for a refused switch: why switching is not possible, or plainly that it did not happen.</summary>
    private string SwitchRefusal() =>
        ProfileSwitchBlockedReason is { Length: > 0 } reason ? reason : Localizer.Instance["CurveEditorCtrl.NotSwitched"];

    private void SwitchLive(ProfileRow target, bool discarded)
    {
        string? previousId = ActiveProfile?.Id;
        ActiveProfile = target; // sends the activation, see OnActiveProfileChanged
        ShowSwitchedToast(target.Id, target.Name, previousId, discarded);
    }

    /// <summary>„Auf „X“ gewechselt.“ with „Rückgängig“ = switch back - a new live action, not the history's undo.</summary>
    private void ShowSwitchedToast(string targetId, string targetName, string? previousId, bool discarded)
    {
        string text = Localizer.Instance.Format(discarded ? "CurveEditorCtrl.SwitchedDiscarded" : "CurveEditorCtrl.Switched",
            targetName);
        if (previousId is null)
        {
            _switchBackCommand = null;
            SetStatus(text, autoHide: true);
            return;
        }
        _switchBackCommand = new RelayCommand(
            () => SwitchBack(previousId, targetId), () => CanSwitchBack(previousId, targetId));
        Status.SetWithAction(text, Localizer.Instance["CurveEditorCtrl.UndoAction"], _switchBackCommand, StatusIcon.Swap);
    }

    /// <summary>
    /// Only while it would really undo that switch: the target still runs, the previous profile still exists, and nothing
    /// is unsaved (the switch-back would otherwise need the dialog). The next edit drops the toast anyway.
    /// </summary>
    private bool CanSwitchBack(string previousId, string targetId) =>
        CanSwitchProfile && !HasUnsavedChanges && ActiveProfile?.Id == targetId && Profiles.Any(p => p.Id == previousId);

    private void SwitchBack(string previousId, string targetId)
    {
        if (!CanSwitchBack(previousId, targetId))
            return;
        ProfileRow previous = Profiles.First(p => p.Id == previousId);
        _switchBackCommand = null;
        ActiveProfile = previous;
        SetStatus(Localizer.Instance.Format("CurveEditorCtrl.SwitchedBack", previous.Name), autoHide: true);
    }

    /// <param name="baselineBefore">For a save-and-switch: the baseline before the save, restored if it is not confirmed.</param>
    private void BeginPendingSwitch(string profileId, bool viaSave, string? baselineBefore = null)
    {
        _pendingSwitchId = profileId;
        _pendingSwitchTicks = SwitchConfirmTicks;
        _pendingSwitchViaSave = viaSave;
        _preSwitchBaselineJson = viaSave ? baselineBefore : null;
        NotifySwitchAvailability();
    }

    private void EndPendingSwitch()
    {
        _pendingSwitchId = null;
        _pendingSwitchViaSave = false;
        _preSwitchBaselineJson = null;
        NotifySwitchAvailability();
    }

    /// <summary>
    /// Every live tick: the running profile is the daemon's. A pending switch is confirmed once the snapshot shows it; if
    /// it does not within <see cref="SwitchConfirmTicks"/>, the error says so and the editor follows the daemon - and a
    /// save-and-switch the daemon did not take leaves its changes unsaved again. Without a pending switch, a different
    /// running profile was switched elsewhere - the editor follows that too. Following is no edit: the baseline and the
    /// history move along (as with every live change), the draft stays.
    /// </summary>
    private void FollowDaemonProfile(AppConfig daemon)
    {
        // While a config is on its way, the snapshot may already show what it switches to - the save settles that.
        if (_saveInFlight || daemon.ActiveProfileId is not { } running || Profiles.Count == 0)
            return;

        if (_pendingSwitchId is { } expected)
        {
            if (running == expected)
            {
                EndPendingSwitch();
                return;
            }
            if (--_pendingSwitchTicks > 0)
                return;
            string wanted = Profiles.FirstOrDefault(p => p.Id == expected)?.Name ?? expected;
            bool viaSave = _pendingSwitchViaSave;
            string? unsavedAgain = _preSwitchBaselineJson;
            EndPendingSwitch();
            if (viaSave && unsavedAgain is not null)
                SetBaseline(unsavedAgain); // the daemon did not take the config: what it carried is a draft again
            string kept = AdoptRunningProfile(running, daemon, out _)?.Name ?? running;
            SetStatus(Localizer.Instance.Format(viaSave ? "CurveEditorCtrl.SaveSwitchNotConfirmed" : "CurveEditorCtrl.SwitchNotConfirmed",
                wanted, kept), isError: true);
            return;
        }

        if (running == ActiveProfile?.Id || AdoptRunningProfile(running, daemon, out bool restored) is not { } followed)
            return;
        if (restored)
            SetStatus(Localizer.Instance.Format("CurveEditorCtrl.RunningProfileRestored", followed.Name), isError: true);
        else
            SetStatus(Localizer.Instance.Format("CurveEditorCtrl.RunningProfileChanged", followed.Name), autoHide: true);
    }

    /// <summary>
    /// Marks the daemon's running profile as running here, without sending anything. A profile the editor does not have
    /// (removed from the draft, created elsewhere) comes back as a row from <paramref name="daemon"/>'s config, also in the
    /// baseline and the history - showing another profile as running would let the next save switch to it silently.
    /// Null only if <paramref name="daemon"/> does not list it either.
    /// </summary>
    /// <param name="restored">Whether the profile had to be put back.</param>
    private ProfileRow? AdoptRunningProfile(string profileId, AppConfig daemon, out bool restored)
    {
        restored = false;
        ProfileRow? row = Profiles.FirstOrDefault(p => p.Id == profileId);
        if (row is null)
        {
            if (daemon.Profiles.FirstOrDefault(p => p.Id == profileId) is not { } missing)
                return null;
            ProfileRow added = RowOf(missing);
            WithoutHistory(() => Profiles.Add(added));
            Profile restoredProfile = added.ToProfile();
            RebaselineLive(config => config.Profiles.Any(p => p.Id == profileId)
                ? config
                : config with { Profiles = config.Profiles.Append(restoredProfile).ToList() });
            row = added;
            restored = true;
        }
        _applyingActiveProfile = true;
        ActiveProfile = row;
        _applyingActiveProfile = false;
        RebaselineActiveProfile(profileId);
        RefreshDirty();
        return row;
    }

    /// <summary>Re-reports everything that depends on whether a switch is possible now.</summary>
    private void NotifySwitchAvailability()
    {
        OnPropertyChanged(nameof(CanSwitchProfile));
        OnPropertyChanged(nameof(ProfileSwitchBlockedReason));
        OnPropertyChanged(nameof(CanActivateSelectedProfile));
        OnPropertyChanged(nameof(ActivateProfileHint));
        ActivateSelectedProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged(); // nothing is deleted while a switch waits for its confirmation
        _switchBackCommand?.NotifyCanExecuteChanged();
    }

    private ProfileSwitchRequest BuildSwitchRequest(ProfileRow target)
    {
        Localizer l = Localizer.Instance;
        List<string> parts = SummaryParts(_diff);
        string message = parts.Count == 0
            ? l["ProfileSwitch.MessageGeneric"]
            : l.Format("ProfileSwitch.Message", string.Join(", ", parts));
        string? discardBlocked = SavedConfig?.Profiles.Any(p => p.Id == target.Id) == true
            ? null
            : l.Format("ProfileSwitch.DiscardDraftOnly", target.Name);
        return new ProfileSwitchRequest(target.Id, l.Format("ProfileSwitch.Title", target.Name), message,
            l["ProfileSwitch.Discard"], l["ProfileSwitch.Save"], discardBlocked);
    }
}
