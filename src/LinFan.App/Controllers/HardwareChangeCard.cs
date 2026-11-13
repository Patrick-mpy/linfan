// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// One device of the hardware dialog: what changed, the fans and sensors it covers, and what the user can do about it - a
/// replaced device is kept for the new one or set up anew, a missing one's entries are removed or kept. Its
/// <see cref="HardwareChangeController"/> follows the daemon's report and decides what each action sends.
/// </summary>
public sealed partial class HardwareChangeCard : ObservableObject
{
    private readonly HardwareChangeController _owner;

    internal HardwareChangeCard(string key, HardwareChangeKind kind, HardwareChangeController owner)
    {
        Key = key;
        Kind = kind;
        _owner = owner;
    }

    /// <summary>The change's key - the target of what the card sends.</summary>
    public string Key { get; }

    public HardwareChangeKind Kind { get; }

    public bool IsReplaced => Kind == HardwareChangeKind.Replaced;

    public bool IsMissing => Kind == HardwareChangeKind.Missing;

    /// <summary>The device (see <see cref="HardwareChangeText.DeviceName"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _title = "";

    /// <summary>„Getauscht" or „Nicht gefunden" - the chip next to the title.</summary>
    public string KindLabel => Localizer.Instance[IsReplaced ? "HardwareDialog.KindReplaced" : "HardwareDialog.KindMissing"];

    public StatusTone KindTone => IsReplaced ? StatusTone.Warn : StatusTone.Muted;

    public string Summary => Localizer.Instance[IsReplaced ? "HardwareDialog.ReplacedSummary" : "HardwareDialog.MissingSummary"];

    /// <summary>The card's accessible name: its kind and device - the chip's colour is not read out.</summary>
    public string AccessibleName => Localizer.Instance.Format("HardwareDialog.CardName", KindLabel, Title);

    /// <summary>The device before (replaced cards only; see <see cref="HardwareChangeText.Devices"/>).</summary>
    [ObservableProperty] private string _oldDevice = "";

    /// <summary>The device now (replaced cards only).</summary>
    [ObservableProperty] private string _newDevice = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFans), nameof(ShowStartPointNote))]
    private IReadOnlyList<HardwareChangeEntry> _fans = [];

    /// <summary>The sensors' names, comma-separated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSensors))]
    private string _sensors = "";

    public bool HasFans => Fans.Count > 0;

    public bool HasSensors => Sensors.Length > 0;

    /// <summary>The fans of a replaced device keep a calibration that was measured on the old one.</summary>
    public bool ShowStartPointNote => IsReplaced && HasFans;

    /// <summary>A sensor of the replaced device has an own name, so resetting them to the hardware names does something.</summary>
    [ObservableProperty] private bool _canResetSensorNames;

    /// <summary>The checkbox „Sensoren auf Hardware-Namen zurücksetzen" (only counts while it is offered).</summary>
    [ObservableProperty] private bool _resetSensorNames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForgetTooltip))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(ForgetCommand))]
    private bool _isOffline;

    /// <summary>Sent; the card waits for the daemon to stop reporting the change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(ForgetCommand))]
    private bool _isPending;

    /// <summary>
    /// A calibration, identification or coupling runs while the change covers fans: the daemon would refuse to remove
    /// them (it may be one of them), so the card does not offer it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForgetTooltip))]
    [NotifyCanExecuteChangedFor(nameof(ForgetCommand))]
    private bool _forgetBlocked;

    /// <summary>What the card waits for, or why the last action came to nothing; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = "";

    public bool HasNote => Note.Length > 0;

    /// <summary>Fresh snapshots a sent action still waits for the change to go (see <see cref="HardwareChangeController"/>).</summary>
    internal int PendingTicks { get; set; }

    /// <summary>The remove button's tooltip: why it is locked, else what it does.</summary>
    public string ForgetTooltip => IsOffline ? Localizer.Instance["FanSections.BlockedOffline"]
        : ForgetBlocked ? Localizer.Instance["FanSections.BlockedBusy"]
        : Localizer.Instance[IsReplaced ? "HardwareDialog.ForgetReplacedTooltip" : "HardwareDialog.ForgetMissingTooltip"];

    /// <summary>Keeps the entries for the new device - with the checkbox, the sensors also lose their own names.</summary>
    [RelayCommand(CanExecute = nameof(CanAccept))]
    private Task Accept() => _owner.AcceptAsync(this);

    private bool CanAccept() => IsReplaced && !IsOffline && !IsPending;

    /// <summary>Removes the entries (after a confirmation).</summary>
    [RelayCommand(CanExecute = nameof(CanForget))]
    private void Forget() => _owner.RequestForget(this);

    private bool CanForget() => !IsOffline && !IsPending && !ForgetBlocked;

    /// <summary>A missing device's „Behalten": nothing is sent, the card leaves this dialog.</summary>
    [RelayCommand]
    private void Keep() => _owner.Keep(this);

    internal void BeginPending(int ticks)
    {
        PendingTicks = ticks;
        IsPending = true;
        Note = Localizer.Instance["HardwareDialog.Waiting"];
    }

    internal void EndPending(string note)
    {
        IsPending = false;
        Note = note;
    }

    /// <summary>Takes the daemon's current report of the change and the names the snapshot gives its entries.</summary>
    internal void Update(HardwareChangeReading change, MonitorSnapshot snapshot, Func<string, FanRow?> fanRow, bool busy)
    {
        Title = HardwareChangeText.DeviceName(change);
        (OldDevice, NewDevice) = IsReplaced ? HardwareChangeText.Devices(change) : ("", "");
        List<HardwareChangeEntry> fans = change.FanIds
            .Select(id => new HardwareChangeEntry(
                Name(snapshot.Fans.FirstOrDefault(f => f.Id == id)?.Name,
                    snapshot.Config.Fans.FirstOrDefault(f => f.FanId == id)?.Name, id),
                IsReplaced && fanRow(id) is { CanControl: true } row ? row : null))
            .ToList();
        if (!fans.SequenceEqual(Fans))
            Fans = fans;
        Sensors = string.Join(", ", change.SensorIds.Select(id =>
            Name(snapshot.Sensors.FirstOrDefault(s => s.Id == id)?.Name, OwnSensorName(id, snapshot), id)));
        // The daemon clears a name that is just the hardware label: whatever name the config holds is the user's own.
        CanResetSensorNames = IsReplaced && change.SensorIds.Any(id => !string.IsNullOrWhiteSpace(OwnSensorName(id, snapshot)));
        ForgetBlocked = busy && change.FanIds.Count > 0;
    }

    // The name the snapshot shows (fans: own name, else „Lüfter N"); an entry that is not detected keeps its configured
    // one, else its id.
    private static string Name(string? shown, string? configured, string id) =>
        shown is { Length: > 0 } ? shown
        : !string.IsNullOrWhiteSpace(configured) ? configured.Trim()
        : UntrustedText.Strip(id);

    private static string? OwnSensorName(string id, MonitorSnapshot snapshot) =>
        snapshot.Config.Sensors.FirstOrDefault(s => s.SensorId == id)?.Name;
}
