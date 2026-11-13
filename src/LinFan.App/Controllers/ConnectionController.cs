// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// The banners above the tabs about the daemon connection: offline (cause, diagnostic command, "Reconnect"), data this
/// GUI cannot read, and dry run. <see cref="MainController"/> feeds it every snapshot; it holds no connection of its
/// own - "Reconnect" only brings the monitor's next attempt forward. No dispatcher: delays run on the injected clock, so
/// plain unit tests drive it.
/// </summary>
public sealed partial class ConnectionController : ObservableObject, IDisposable
{
    /// <summary>
    /// How long "Reconnect" stays pending at most. A connect that hangs never completes the request; the button must
    /// not stay locked with it.
    /// </summary>
    public static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long "Reconnect" stays pending at least. Without a daemon socket an attempt fails within milliseconds - the
    /// label would only flicker, and the click would look like it did nothing.
    /// </summary>
    public static readonly TimeSpan MinimumPending = TimeSpan.FromMilliseconds(600);

    /// <summary>How long the note under the button says that the attempt did not connect.</summary>
    public static readonly TimeSpan FailedFeedback = TimeSpan.FromSeconds(2);

    /// <summary>How long the copy button says "Copied".</summary>
    public static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(2);

    private readonly IReconnectable? _reconnectable;
    private readonly TimeProvider _clock;

    // Each feedback run gets a number; a run's delayed reset only acts if no newer run started meanwhile.
    private int _copiedRun;
    private int _failedRun;
    private string? _unreadableServiceVersion; // what the service reported with the data this GUI could not read

    /// <param name="reconnectable">Brings the next connection attempt forward; <c>null</c> disables "Reconnect".</param>
    /// <param name="errorLogPath">The GUI log - the unreadable-data banner points there.</param>
    /// <param name="clock">Time source of the reconnect timings and the copy feedback (tests advance it by hand).</param>
    public ConnectionController(IReconnectable? reconnectable, string errorLogPath, TimeProvider? clock = null)
    {
        _reconnectable = reconnectable;
        ErrorLogPath = errorLogPath;
        _clock = clock ?? TimeProvider.System;
        Status = new TransientStatus(clock: _clock);
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>The short "reconnected" toast after a successful "Reconnect".</summary>
    public TransientStatus Status { get; }

    /// <summary>
    /// The connection state of the last snapshot. Starts at <see cref="ConnectionProblem.Connecting"/>: before the
    /// first attempt has an outcome there is nothing to report, so no banner flashes at start.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOfflineBanner), nameof(ShowDiagnosticCommand), nameof(ShowReconnect),
        nameof(ShowErrorLogPath), nameof(ShowDryRunBanner), nameof(OfflineTitle), nameof(OfflineText),
        nameof(StatusText))]
    private ConnectionProblem _problem = ConnectionProblem.Connecting;

    /// <summary>The daemon's control loop computes curves but writes no PWM (it runs without the rights to).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDryRunBanner))]
    private bool _dryRun;

    /// <summary>Only for the label: the command itself is disabled while it runs (async command).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReconnectLabel))]
    private bool _isReconnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReconnectFeedback))]
    private bool _reconnectFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    private bool _isCopied;

    public bool ShowOfflineBanner =>
        Problem is ConnectionProblem.Unreachable or ConnectionProblem.AccessDenied or ConnectionProblem.UnreadableData;

    /// <summary>
    /// Not for unreadable data: the connection stands, another attempt would read the same - it ends when the service
    /// sends what this GUI can read (both on the same version).
    /// </summary>
    public bool ShowReconnect => Problem is ConnectionProblem.Unreachable or ConnectionProblem.AccessDenied;

    /// <summary>
    /// Only for an unreachable service: with access denied the service runs, checking it would not help. Nor where no
    /// service is installed (<see cref="DiagnosticCommand"/> empty).
    /// </summary>
    public bool ShowDiagnosticCommand => Problem == ConnectionProblem.Unreachable && DiagnosticCommand.Length > 0;

    public bool ShowDryRunBanner => Problem == ConnectionProblem.None && DryRun;

    /// <summary>Unreadable data: the details are in the GUI log, whose path the banner shows selectable.</summary>
    public bool ShowErrorLogPath => Problem == ConnectionProblem.UnreadableData;

    /// <summary>
    /// The header's short status while the offline banner explains the cause - the profile button's tooltip only says
    /// that there is one. Empty without a banner.
    /// </summary>
    public string StatusText => Problem switch
    {
        ConnectionProblem.Unreachable or ConnectionProblem.AccessDenied => Localizer.Instance["OfflineBanner.Status"],
        ConnectionProblem.UnreadableData => Localizer.Instance["OfflineBanner.UnreadableStatus"],
        _ => "",
    };

    /// <summary>
    /// Empty while no offline banner shows. The title is the banner's live region, so its text has to appear together
    /// with the banner - that change is what screen readers announce.
    /// </summary>
    public string OfflineTitle => Problem switch
    {
        ConnectionProblem.Unreachable => Localizer.Instance["OfflineBanner.Title"],
        ConnectionProblem.AccessDenied => Localizer.Instance["OfflineBanner.AccessDeniedTitle"],
        ConnectionProblem.UnreadableData => Localizer.Instance["OfflineBanner.UnreadableTitle"],
        _ => "",
    };

    public string OfflineText => Problem switch
    {
        ConnectionProblem.Unreachable => Localizer.Instance[DiagnosticCommand.Length > 0 ? "OfflineBanner.Text" : "OfflineBanner.TextManualStart"],
        ConnectionProblem.AccessDenied => Localizer.Instance["IpcLiveMonitor.AccessDenied"],
        // The versions, where the service sent its own, point at the likely cause; the path follows selectable.
        ConnectionProblem.UnreadableData => _unreadableServiceVersion is { } service
            ? Localizer.Instance.Format("OfflineBanner.UnreadableTextVersions",
                VersionController.Short(service), VersionController.Short(AppVersion.InformationalRaw))
            : Localizer.Instance["OfflineBanner.UnreadableText"],
        _ => "",
    };

    /// <summary>Where the GUI log keeps the details of a snapshot it could not read.</summary>
    public string ErrorLogPath { get; }

    /// <summary>
    /// The command that shows whether the service runs - not translated, it is typed into a shell. Empty where LinFan
    /// installs no service (macOS: the daemon is started by hand), the banner text says so instead.
    /// </summary>
    public string DiagnosticCommand { get; internal init; } = DiagnosticCommandFor(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    public string ReconnectLabel => Localizer.Instance[IsReconnecting ? "OfflineBanner.Reconnecting" : "OfflineBanner.Reconnect"];

    /// <summary>Empty unless the last "Reconnect" did not connect - a live region as well, so the outcome is announced.</summary>
    public string ReconnectFeedback => ReconnectFailed ? Localizer.Instance["OfflineBanner.StillNotConnected"] : "";

    public string CopyLabel => Localizer.Instance[IsCopied ? "OfflineBanner.Copied" : "OfflineBanner.Copy"];

    /// <summary>
    /// Service names as installed by <c>packaging/linux/linfan-daemon.service</c> and
    /// <c>packaging/windows/Install-LinFan.ps1</c> (PowerShell, like the installer's own hint); macOS has no service yet.
    /// A runtime check rather than <c>#if</c>, so one binary is right everywhere.
    /// </summary>
    internal static string DiagnosticCommandFor(bool windows, bool macOS) =>
        windows ? "Get-Service LinFan" : macOS ? "" : "systemctl status linfan-daemon";

    public void Apply(MonitorSnapshot snapshot)
    {
        string? serviceVersion = snapshot.Problem == ConnectionProblem.UnreadableData ? snapshot.DaemonVersion : null;
        bool versionChanged = serviceVersion != _unreadableServiceVersion;
        _unreadableServiceVersion = serviceVersion;
        Problem = snapshot.Problem;
        DryRun = snapshot.DryRun;
        if (versionChanged)
            OnPropertyChanged(nameof(OfflineText));
    }

    /// <summary>
    /// Asks for a connection attempt now and reports its outcome: the toast on success, a short note under the button
    /// otherwise. Pending for at least <see cref="MinimumPending"/> and at most <see cref="ReconnectTimeout"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private async Task ReconnectAsync()
    {
        IsReconnecting = true;
        ReconnectFailed = false;
        Task minimum = Task.Delay(MinimumPending, _clock);
        bool connected = await AttemptAsync();
        await minimum;
        IsReconnecting = false;

        if (connected)
            Status.Set(Localizer.Instance["OfflineBanner.Reconnected"], autoHide: true);
        else
            ShowReconnectFailed();
    }

    /// <summary>
    /// Whether the requested attempt connected. A request still open after <see cref="ReconnectTimeout"/> counts as
    /// failed and is not awaited further - the monitor keeps retrying on its own, and the next snapshot shows it.
    /// </summary>
    private async Task<bool> AttemptAsync()
    {
        if (_reconnectable is null)
            return false;
        try
        {
            Task<bool> attempt = _reconnectable.RequestReconnectAsync();
            return await Task.WhenAny(attempt, Task.Delay(ReconnectTimeout, _clock)) == attempt && await attempt;
        }
        catch
        {
            return false; // a failing request is a failed attempt
        }
    }

    private bool CanReconnect() => _reconnectable is not null;

    /// <summary>The view copied <see cref="DiagnosticCommand"/>: "Copied" for <see cref="CopiedFeedback"/>, restarted by each copy.</summary>
    public void MarkCopied()
    {
        int run = ++_copiedRun;
        IsCopied = true;
        _ = AfterAsync(CopiedFeedback, () =>
        {
            if (run == _copiedRun)
                IsCopied = false;
        });
    }

    private void ShowReconnectFailed()
    {
        int run = ++_failedRun;
        ReconnectFailed = true;
        _ = AfterAsync(FailedFeedback, () =>
        {
            if (run == _failedRun)
                ReconnectFailed = false;
        });
    }

    private async Task AfterAsync(TimeSpan delay, Action then)
    {
        await Task.Delay(delay, _clock);
        then();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(OfflineTitle));
        OnPropertyChanged(nameof(OfflineText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ReconnectLabel));
        OnPropertyChanged(nameof(ReconnectFeedback));
        OnPropertyChanged(nameof(CopyLabel));
    }

    public void Dispose() => Localizer.Instance.PropertyChanged -= OnLanguageChanged;
}
