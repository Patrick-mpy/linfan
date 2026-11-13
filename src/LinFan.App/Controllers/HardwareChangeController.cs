// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// The hardware dialog's state: one card per replaced or missing device the daemon reports. It follows the snapshots -
/// a resolved change's card goes, a new one comes, and with no card left it asks the view to close; offline it keeps
/// the last cards and locks every action. What a card does goes to the daemon as <c>acceptHardware</c> or
/// <c>forgetHardware</c>; the daemon answers only by no longer reporting the change.
/// <para>
/// Removing entries or clearing sensor names changes the config the editor holds, and the editor's next save would
/// write the old entries back. Those actions therefore arm the editor's rebuild before they go out - for the config that
/// no longer reports the change - and ask first when the rebuild would drop unsaved edits.
/// </para>
/// </summary>
public sealed partial class HardwareChangeController : ObservableObject
{
    /// <summary>
    /// How many fresh snapshots a sent action waits for the change to go before its card offers the actions again and says
    /// that nothing happened (the daemon refused it and logged why) - as long as the editor waits for its rebuild.
    /// </summary>
    internal const int PendingTicks = MainController.ResyncGraceTicks;

    private readonly Func<string, bool, Task<bool>> _sendAccept;
    private readonly Func<string, Task<bool>> _sendForget;
    private readonly Func<Func<MonitorSnapshot, bool>, Task<bool>> _armResync;
    private readonly Action _cancelResync;
    private readonly Func<bool> _hasUnsavedChanges;
    private readonly Func<string, FanRow?> _fanRow;
    private readonly Action? _onClosed;
    private readonly HashSet<string> _kept = new(StringComparer.Ordinal);       // missing devices the user chose to keep
    private readonly HashSet<string> _resyncKeys = new(StringComparer.Ordinal); // sent changes the editor rebuilds for
    private MonitorSnapshot? _last;
    private bool _closeRequested;

    /// <param name="sendAccept">Sends <c>acceptHardware</c> (key, reset sensor names); whether it went out.</param>
    /// <param name="sendForget">Sends <c>forgetHardware</c> (key); whether it went out.</param>
    /// <param name="armResync">
    /// Arms the editor's rebuild for the next config change the daemon reports that also passes the given condition.
    /// </param>
    /// <param name="cancelResync">Disarms it again when nothing went out.</param>
    /// <param name="hasUnsavedChanges">Whether the editor holds edits a rebuild would drop.</param>
    /// <param name="fanRow">The overview's row of a fan, for its „Neu kalibrieren".</param>
    /// <param name="onClosed">Called once the dialog has closed.</param>
    public HardwareChangeController(
        Func<string, bool, Task<bool>> sendAccept,
        Func<string, Task<bool>> sendForget,
        Func<Func<MonitorSnapshot, bool>, Task<bool>> armResync,
        Action cancelResync,
        Func<bool> hasUnsavedChanges,
        Func<string, FanRow?> fanRow,
        Action? onClosed = null)
    {
        _sendAccept = sendAccept;
        _sendForget = sendForget;
        _armResync = armResync;
        _cancelResync = cancelResync;
        _hasUnsavedChanges = hasUnsavedChanges;
        _fanRow = fanRow;
        _onClosed = onClosed;
    }

    public ObservableCollection<HardwareChangeCard> Cards { get; } = new();

    /// <summary>The daemon is out of reach: the cards show the last report, every action is locked.</summary>
    [ObservableProperty] private bool _isOffline;

    /// <summary>A card's action needs an answer first - the view asks and hands a „yes" to <see cref="ConfirmedAsync"/>.</summary>
    public event EventHandler<HardwareConfirmRequest>? ConfirmationRequested;

    /// <summary>No card is left: the view closes the dialog.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Follows a snapshot (every tick, also offline).</summary>
    public void Apply(MonitorSnapshot snapshot)
    {
        bool fresh = !ReferenceEquals(snapshot, _last);
        _last = snapshot;
        IsOffline = !snapshot.Connected;
        if (!snapshot.Connected)
        {
            // Frozen as last seen. A sent action can no longer be confirmed - its card offers the actions again once
            // connected, and the next snapshot shows whether the daemon took it before the connection dropped.
            foreach (HardwareChangeCard card in Cards)
            {
                card.IsOffline = true;
                if (card.IsPending)
                    card.EndPending("");
            }
            return;
        }

        List<HardwareChangeReading> changes = (snapshot.HardwareChanges ?? [])
            .Where(c => c.Kind is not null && !_kept.Contains(c.Key))
            .ToList();
        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (changes.All(c => c.Key != Cards[i].Key))
            {
                _resyncKeys.Remove(Cards[i].Key);
                Cards.RemoveAt(i);
            }
        }

        bool busy = DeviceActionBlock.Busy(snapshot);
        for (int i = 0; i < changes.Count; i++)
        {
            HardwareChangeReading change = changes[i];
            HardwareChangeCard? card = Cards.FirstOrDefault(c => c.Key == change.Key);
            if (card is null)
            {
                card = new HardwareChangeCard(change.Key, change.Kind!.Value, this);
                Cards.Insert(Math.Min(i, Cards.Count), card);
            }
            else if (fresh && card.IsPending && --card.PendingTicks <= 0)
            {
                _resyncKeys.Remove(card.Key);
                card.EndPending(Localizer.Instance["HardwareDialog.NotTaken"]);
            }
            card.IsOffline = false;
            card.Update(change, snapshot, _fanRow, busy);
        }
        RequestCloseIfEmpty();
    }

    /// <summary>The dialog has closed (any way) - its <c>Closed</c> event, which comes once.</summary>
    public void Close() => _onClosed?.Invoke();

    /// <summary>Sends what the user confirmed - unless the card has gone or the connection with it in the meantime.</summary>
    public Task ConfirmedAsync(HardwareConfirmRequest request) =>
        SendAsync(request.Card, request.Forget, request.ResetSensorNames);

    internal Task AcceptAsync(HardwareChangeCard card)
    {
        bool resetNames = card.CanResetSensorNames && card.ResetSensorNames;
        if (!resetNames || !_hasUnsavedChanges())
            return SendAsync(card, forget: false, resetNames);

        Localizer l = Localizer.Instance;
        ConfirmationRequested?.Invoke(this, new HardwareConfirmRequest(card, Forget: false, ResetSensorNames: true,
            l["HardwareDialog.ResetNamesTitle"], l["HardwareDialog.ResetNamesMessage"] + "\n\n" + l["HardwareDialog.DiscardsUnsaved"],
            l["HardwareDialog.ResetNamesConfirm"]));
        return Task.CompletedTask;
    }

    internal void RequestForget(HardwareChangeCard card)
    {
        Localizer l = Localizer.Instance;
        string message = l[card.IsMissing ? "HardwareDialog.ForgetMissingMessage"
            : card.HasFans ? "HardwareDialog.ForgetReplacedMessage"
            : "HardwareDialog.ForgetReplacedSensorsMessage"];
        if (_hasUnsavedChanges())
            message += "\n\n" + l["HardwareDialog.DiscardsUnsaved"];
        ConfirmationRequested?.Invoke(this, new HardwareConfirmRequest(card, Forget: true, ResetSensorNames: false,
            l[card.IsMissing ? "HardwareDialog.ForgetMissingTitle" : "HardwareDialog.ForgetReplacedTitle"], message,
            l[card.IsMissing ? "HardwareDialog.ForgetMissingConfirm" : "HardwareDialog.ForgetReplacedConfirm"]));
    }

    internal void Keep(HardwareChangeCard card)
    {
        _kept.Add(card.Key);
        Cards.Remove(card);
        RequestCloseIfEmpty();
    }

    private async Task SendAsync(HardwareChangeCard card, bool forget, bool resetSensorNames)
    {
        if (IsOffline || card.IsPending || !Cards.Contains(card))
            return;

        // Armed before the command goes out, for the config that no longer reports any change sent this way: a config
        // change that overtakes it (a calibration landing) must not use up the rebuild.
        bool resync = forget || resetSensorNames;
        if (resync)
        {
            _resyncKeys.Add(card.Key);
            _ = _armResync(Resolved);
        }
        card.BeginPending(PendingTicks);
        bool sent = forget ? await _sendForget(card.Key) : await _sendAccept(card.Key, resetSensorNames);
        if (sent)
            return;

        if (resync && _resyncKeys.Remove(card.Key) && _resyncKeys.Count == 0)
            _cancelResync();
        card.EndPending(Localizer.Instance["HardwareDialog.SendFailed"]);
    }

    private bool Resolved(MonitorSnapshot snapshot) =>
        !(snapshot.HardwareChanges ?? []).Any(c => _resyncKeys.Contains(c.Key));

    private void RequestCloseIfEmpty()
    {
        if (Cards.Count > 0 || _closeRequested)
            return;
        _closeRequested = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
