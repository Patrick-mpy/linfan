// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.Tests;

public sealed class ConnectionControllerTests
{
    /// <summary>
    /// Runs <paramref name="test"/> without a synchronization context, so continuations (a settled reconnect, the
    /// clock's timers) run right where the test triggers them instead of being posted to the runner's context.
    /// </summary>
    private static void Inline(Action test)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            test();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>A reconnect request that stays open until the test settles it - like a running connection attempt.</summary>
    private sealed class FakeReconnect : IReconnectable
    {
        private TaskCompletionSource<bool> _attempt = new();

        public int Requests { get; private set; }

        public Task<bool> RequestReconnectAsync()
        {
            Requests++;
            return _attempt.Task;
        }

        /// <summary>Ends the running attempt; <paramref name="connected"/> is its outcome.</summary>
        public void Settle(bool connected)
        {
            TaskCompletionSource<bool> attempt = _attempt;
            _attempt = new TaskCompletionSource<bool>();
            attempt.SetResult(connected);
        }

        public void Fail()
        {
            TaskCompletionSource<bool> attempt = _attempt;
            _attempt = new TaskCompletionSource<bool>();
            attempt.SetException(new InvalidOperationException("boom"));
        }
    }

    private static MonitorSnapshot Offline(ConnectionProblem problem) => MonitorSnapshot.Unavailable("offline", problem);

    private static MonitorSnapshot Online(bool dryRun = false) =>
        new("ok", Array.Empty<SensorReading>(), Array.Empty<FanReading>(), AppConfig.Empty, Connected: true, DryRun: dryRun);

    private static ConnectionController Create(IReconnectable? reconnect = null, TimeProvider? clock = null) =>
        new(reconnect, LogPath, clock);

    private const string LogPath = "/home/u/.config/linfan/logs/gui.log";

    [Fact]
    public void BeforeTheFirstOutcome_NoBannerShows()
    {
        using ConnectionController c = Create();

        Assert.Equal(ConnectionProblem.Connecting, c.Problem);
        Assert.False(c.ShowOfflineBanner);
        Assert.False(c.ShowDryRunBanner);
        Assert.Equal("", c.OfflineTitle);
    }

    [Fact]
    public void Connecting_KeepsTheBannerHidden()
    {
        using ConnectionController c = Create();

        c.Apply(Offline(ConnectionProblem.Connecting));

        Assert.False(c.ShowOfflineBanner); // the initial attempt is not a failure - no flash at start
        Assert.Equal("", c.OfflineTitle);
        Assert.Equal("", c.OfflineText);
    }

    [Fact]
    public void Unreachable_ShowsTheBanner_WithTheCommandToCheckTheService()
    {
        using ConnectionController c = Create();

        c.Apply(Offline(ConnectionProblem.Unreachable));

        Assert.True(c.ShowOfflineBanner);
        Assert.True(c.ShowDiagnosticCommand);
        Assert.False(c.ShowDryRunBanner);
        Assert.Equal("Hintergrunddienst nicht erreichbar", c.OfflineTitle);
        Assert.Contains("Läuft der Dienst?", c.OfflineText);
    }

    [Fact]
    public void AccessDenied_ShowsTheGroupHint_WithoutTheServiceCommand()
    {
        using ConnectionController c = Create();

        c.Apply(Offline(ConnectionProblem.AccessDenied));

        Assert.True(c.ShowOfflineBanner);
        Assert.False(c.ShowDiagnosticCommand); // the service runs - checking it would send the user the wrong way
        Assert.Equal("Zugriff auf den Hintergrunddienst verweigert", c.OfflineTitle);
        Assert.Equal(Localizer.Instance["IpcLiveMonitor.AccessDenied"], c.OfflineText);
        Assert.Contains("melde dich danach ab und wieder an", c.OfflineText);
    }

    [Fact]
    public void UnreadableData_ShowsTheBanner_NamingAnotherVersion_AndTheGuiLog_WithoutReconnect()
    {
        using ConnectionController c = Create();

        c.Apply(Offline(ConnectionProblem.UnreadableData));

        Assert.True(c.ShowOfflineBanner);
        Assert.False(c.ShowDiagnosticCommand); // the service runs
        Assert.False(c.ShowReconnect);         // the connection stands - another attempt would read the same
        Assert.False(c.ShowDryRunBanner);
        Assert.Equal("Daten des Hintergrunddienstes nicht lesbar", c.OfflineTitle);
        Assert.Contains("läuft dort eine andere Version?", c.OfflineText);
        Assert.EndsWith("Details im Log der App:", c.OfflineText);
        Assert.True(c.ShowErrorLogPath); // the path follows selectable
        Assert.Equal(LogPath, c.ErrorLogPath);
        Assert.Equal("Daten nicht lesbar", c.StatusText);
    }

    [Fact]
    public void UnreadableData_NamesTheServicesVersion_WhenItSentOne()
    {
        using ConnectionController c = Create();

        c.Apply(Offline(ConnectionProblem.UnreadableData) with { DaemonVersion = "0.6.0+abcdef123456" });

        Assert.StartsWith("Der Dienst läuft mit Version 0.6.0 · abcdef1 und sendet Daten,", c.OfflineText);

        c.Apply(Offline(ConnectionProblem.Unreachable) with { DaemonVersion = "0.6.0+abcdef123456" });
        Assert.DoesNotContain("0.6.0", c.OfflineText); // only the unreadable data carries it
        Assert.False(c.ShowErrorLogPath);
    }

    [Theory]
    [InlineData(ConnectionProblem.Unreachable, "Nicht verbunden")]
    [InlineData(ConnectionProblem.AccessDenied, "Nicht verbunden")]
    [InlineData(ConnectionProblem.UnreadableData, "Daten nicht lesbar")]
    [InlineData(ConnectionProblem.Connecting, "")]
    public void StatusText_SaysThatThereIsAProblem_WhileTheBannerSaysWhich(ConnectionProblem problem, string expected)
    {
        using ConnectionController c = Create();

        c.Apply(Offline(problem));

        Assert.Equal(expected, c.StatusText);
    }

    [Theory]
    [InlineData(ConnectionProblem.Unreachable, true)]
    [InlineData(ConnectionProblem.AccessDenied, true)]
    [InlineData(ConnectionProblem.UnreadableData, false)]
    [InlineData(ConnectionProblem.Connecting, false)]
    public void Reconnect_IsOfferedWhereAnotherAttemptCanHelp(ConnectionProblem problem, bool expected)
    {
        using ConnectionController c = Create();

        c.Apply(Offline(problem));

        Assert.Equal(expected, c.ShowReconnect);
    }

    [Fact]
    public void Connected_HidesTheOfflineBanner_AndClearsItsText()
    {
        using ConnectionController c = Create();
        c.Apply(Offline(ConnectionProblem.Unreachable));

        c.Apply(Online());

        Assert.False(c.ShowOfflineBanner);
        Assert.Equal("", c.OfflineTitle); // empty again, so the next outage is a text change the reader announces
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DryRunBanner_ShowsOnlyWhileConnectedInDryRun(bool dryRun, bool expected)
    {
        using ConnectionController c = Create();

        c.Apply(Online(dryRun));

        Assert.Equal(expected, c.ShowDryRunBanner);
        Assert.False(c.ShowOfflineBanner);
    }

    [Fact]
    public void DryRunBanner_GoesAway_WhenTheConnectionDrops()
    {
        using ConnectionController c = Create();
        c.Apply(Online(dryRun: true));

        // Not connected but still flagged: the flag is stale, the offline banner speaks instead.
        c.Apply(new MonitorSnapshot("x", Array.Empty<SensorReading>(), Array.Empty<FanReading>(), AppConfig.Empty,
            Connected: false, DryRun: true, ConnectionProblem: ConnectionProblem.Unreachable));

        Assert.False(c.ShowDryRunBanner);
        Assert.True(c.ShowOfflineBanner);
    }

    [Theory]
    [InlineData(false, false, "systemctl status linfan-daemon")]
    [InlineData(true, false, "Get-Service LinFan")]
    [InlineData(false, true, "")] // macOS: no service to check
    public void DiagnosticCommand_MatchesThePlatformsService(bool windows, bool macOS, string expected) =>
        Assert.Equal(expected, ConnectionController.DiagnosticCommandFor(windows, macOS));

    /// <summary>Without an installed service (macOS) there is nothing to check - the banner says to start the daemon by hand.</summary>
    [Fact]
    public void Unreachable_WithoutAService_ShowsNoCommand_ButHowToStartTheDaemon()
    {
        using ConnectionController c = new(null, LogPath) { DiagnosticCommand = "" };

        c.Apply(Offline(ConnectionProblem.Unreachable));

        Assert.True(c.ShowOfflineBanner);
        Assert.False(c.ShowDiagnosticCommand);
        Assert.Contains("von Hand mit sudo", c.OfflineText);
        Assert.DoesNotContain("Läuft der Dienst?", c.OfflineText);
    }

    [Fact]
    public void Reconnect_IsPendingUntilTheAttemptSettles_ThenToastsSuccess() => Inline(() =>
    {
        var clock = new ManualClock();
        var reconnect = new FakeReconnect();
        using ConnectionController c = Create(reconnect, clock);
        c.Apply(Offline(ConnectionProblem.Unreachable));
        Assert.Equal("Erneut verbinden", c.ReconnectLabel);

        c.ReconnectCommand.Execute(null);

        Assert.Equal(1, reconnect.Requests);
        Assert.True(c.IsReconnecting);
        Assert.False(c.ReconnectCommand.CanExecute(null)); // no second click while the attempt runs
        Assert.Equal("Verbinde …", c.ReconnectLabel);

        clock.Advance(TimeSpan.FromSeconds(1)); // the attempt takes longer than the minimum
        Assert.True(c.IsReconnecting);
        Assert.Equal("", c.Status.Text);

        reconnect.Settle(connected: true);

        Assert.False(c.IsReconnecting);
        Assert.True(c.ReconnectCommand.CanExecute(null));
        Assert.Equal("Erneut verbinden", c.ReconnectLabel);
        Assert.Equal("Wieder mit dem Hintergrunddienst verbunden.", c.Status.Text);
        Assert.Equal("", c.ReconnectFeedback);

        clock.Advance(TimeSpan.FromSeconds(4.1));
        Assert.Equal("", c.Status.Text); // a short toast, it fades out by itself
    });

    [Fact]
    public void Reconnect_ThatFailsAtOnce_StaysPendingForTheMinimum_ThenSaysSoForTwoSeconds() => Inline(() =>
    {
        var clock = new ManualClock();
        var reconnect = new FakeReconnect();
        using ConnectionController c = Create(reconnect, clock);
        c.Apply(Offline(ConnectionProblem.Unreachable));

        c.ReconnectCommand.Execute(null);
        reconnect.Settle(connected: false); // no socket: the attempt is over within milliseconds

        clock.Advance(ConnectionController.MinimumPending - TimeSpan.FromMilliseconds(50));
        Assert.True(c.IsReconnecting); // still "Verbinde …" - a flicker would look like nothing happened
        Assert.Equal("", c.ReconnectFeedback);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(c.IsReconnecting);
        Assert.True(c.ReconnectCommand.CanExecute(null));
        Assert.Equal("Weiterhin keine Verbindung", c.ReconnectFeedback);
        Assert.Equal("", c.Status.Text); // no success toast

        clock.Advance(ConnectionController.FailedFeedback - TimeSpan.FromMilliseconds(100));
        Assert.Equal("Weiterhin keine Verbindung", c.ReconnectFeedback);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal("", c.ReconnectFeedback);
    });

    [Fact]
    public void Reconnect_Again_ClearsTheNote_AndANewFailureGetsItsFullTwoSeconds() => Inline(() =>
    {
        var clock = new ManualClock();
        var reconnect = new FakeReconnect();
        using ConnectionController c = Create(reconnect, clock);

        c.ReconnectCommand.Execute(null);
        reconnect.Settle(connected: false);
        clock.Advance(ConnectionController.MinimumPending); // first failure: note on, its reset due 2 s from now
        Assert.False(c.IsReconnecting);
        clock.Advance(TimeSpan.FromSeconds(1));

        c.ReconnectCommand.Execute(null);
        Assert.Equal("", c.ReconnectFeedback); // the new attempt runs - the old outcome no longer applies
        reconnect.Settle(connected: false);
        clock.Advance(ConnectionController.MinimumPending); // second failure
        Assert.Equal("Weiterhin keine Verbindung", c.ReconnectFeedback);

        clock.Advance(TimeSpan.FromSeconds(1)); // the first failure's reset falls due here
        Assert.Equal("Weiterhin keine Verbindung", c.ReconnectFeedback);
    });

    [Fact]
    public void Reconnect_ThatFails_GivesTheButtonBack() => Inline(() =>
    {
        var clock = new ManualClock();
        var reconnect = new FakeReconnect();
        using ConnectionController c = Create(reconnect, clock);

        c.ReconnectCommand.Execute(null);
        reconnect.Fail();
        clock.Advance(ConnectionController.MinimumPending);

        Assert.False(c.IsReconnecting);
        Assert.True(c.ReconnectCommand.CanExecute(null));
        Assert.Equal("Weiterhin keine Verbindung", c.ReconnectFeedback);
        Assert.Equal("", c.Status.Text);
    });

    [Fact]
    public void Reconnect_ThatHangs_ReleasesTheButtonAfterTheTimeout() => Inline(() =>
    {
        var clock = new ManualClock();
        var reconnect = new FakeReconnect();
        using ConnectionController c = Create(reconnect, clock);

        c.ReconnectCommand.Execute(null);
        clock.Advance(ConnectionController.ReconnectTimeout - TimeSpan.FromMilliseconds(100));
        Assert.True(c.IsReconnecting);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(c.IsReconnecting);
        Assert.True(c.ReconnectCommand.CanExecute(null));
        Assert.Equal("Weiterhin keine Verbindung", c.ReconnectFeedback);
        Assert.Equal("", c.Status.Text);

        // The abandoned attempt connecting late claims nothing; the banner follows the next snapshot anyway.
        reconnect.Settle(connected: true);
        Assert.Equal("", c.Status.Text);

        c.ReconnectCommand.Execute(null);
        Assert.Equal(2, reconnect.Requests); // the user can try again
    });

    [Fact]
    public void Reconnect_IsDisabled_WithoutAMonitorThatCanReconnect()
    {
        using ConnectionController c = Create(reconnect: null);

        Assert.False(c.ReconnectCommand.CanExecute(null));
    }

    [Fact]
    public void MarkCopied_SaysCopiedForTwoSeconds_RestartedByEachCopy() => Inline(() =>
    {
        var clock = new ManualClock();
        using ConnectionController c = Create(clock: clock);
        Assert.Equal("Kopieren", c.CopyLabel);

        c.MarkCopied();
        Assert.True(c.IsCopied);
        Assert.Equal("Kopiert", c.CopyLabel);

        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal("Kopiert", c.CopyLabel);

        c.MarkCopied(); // copied again: the two seconds start over
        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal("Kopiert", c.CopyLabel);

        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.False(c.IsCopied);
        Assert.Equal("Kopieren", c.CopyLabel);
    });

    [Fact]
    public void LanguageChange_RefreshesTheBannerTexts()
    {
        using ConnectionController c = Create();
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Localizer.Instance.SetLanguage(LanguageChoice.German); // same language - still a change notification

        Assert.Contains(nameof(ConnectionController.OfflineTitle), changed);
        Assert.Contains(nameof(ConnectionController.OfflineText), changed);
        Assert.Contains(nameof(ConnectionController.ReconnectLabel), changed);
        Assert.Contains(nameof(ConnectionController.ReconnectFeedback), changed);
        Assert.Contains(nameof(ConnectionController.CopyLabel), changed);
    }

    [Fact]
    public void Dispose_EndsTheLanguageSubscription()
    {
        ConnectionController c = Create();
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.Dispose();
        Localizer.Instance.SetLanguage(LanguageChoice.German);

        Assert.Empty(changed);
    }
}
