// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// The GUI's robustness through the real poll loop and dispatcher: an exception on the UI thread is caught and shown, a
/// snapshot that fails to apply does not end the GUI, data the GUI cannot read locks it like offline, and a backup
/// import only claims success once the service reports the new configuration back.
/// </summary>
public sealed class GuiRobustnessUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"linfan-robust-{Guid.NewGuid():N}");
    private readonly List<MainController> _controllers = new();

    public void Dispose()
    {
        foreach (MainController ctrl in _controllers)
            ctrl.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private GuiErrorLog Log() => new(Path.Combine(_dir, "logs", "gui.log"));

    /// <summary>Never the user's real settings or log: an import applies UI prefs, an error writes the log.</summary>
    private MainController Start(FakeLiveMonitor monitor, GuiErrorLog? log = null)
    {
        var settings = new SettingsController(new UiSettingsStore(Path.Combine(_dir, $"ui-{Guid.NewGuid():N}.json")));
        var ctrl = new MainController(monitor, log ?? Log(), pollInterval: TimeSpan.FromMilliseconds(10),
            settings: settings);
        _controllers.Add(ctrl);
        return ctrl;
    }

    private static Border Named(MainWindow window, string name) => window.Find<Border>().Single(b => b.Name == name);

    // --- The dispatcher barrier relies on these Avalonia 12 semantics -------------------------------------------------

    [AvaloniaFact]
    public void APostedJobThatThrows_ReachesUnhandledException_AndHandledKeepsTheDispatcherGoing()
    {
        var seen = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
        {
            seen.Add(e.Exception);
            e.Handled = true;
        };
        Dispatcher.UIThread.UnhandledException += handler;
        try
        {
            bool ranAfter = false;
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("posted"));
            Dispatcher.UIThread.Post(() => ranAfter = true);
            FailInAsyncVoid(); // like a failing click handler in the code-behind
            UiTestHelpers.PumpUntil(() => seen.Count == 2);

            Assert.True(ranAfter);
            Assert.Equal(new[] { "async void", "posted" }, seen.Select(e => e.Message).Order());
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= handler;
        }
    }

    private static async void FailInAsyncVoid()
    {
        await Task.Yield();
        throw new InvalidOperationException("async void");
    }

    // --- A snapshot that fails to apply --------------------------------------------------------------------------------

    [AvaloniaFact]
    public void ASnapshotThatFailsToApply_IsLoggedAndShown_AndTheNextOneApplies()
    {
        GuiErrorLog log = Log();
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot() with { Sensors = null! });
        MainController ctrl = Start(monitor, log);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();

        UiTestHelpers.PumpUntil(() => ctrl.ErrorNotice.Text.Length > 0);

        Assert.True(ctrl.ErrorNotice.IsError); // stays until closed
        Assert.EndsWith("Details im Log der App:", ctrl.ErrorNotice.Text);
        Assert.True(Named(window, "ErrorNoticeToast").IsEffectivelyVisible);
        SelectableTextBlock path = window.Find<SelectableTextBlock>().Single(t => t.Name == "ErrorLogPathText");
        Assert.True(path.IsEffectivelyVisible); // selectable, to open or paste it
        Assert.Equal(log.FilePath, path.Text);
        Assert.Contains("[ERR] Snapshot: Applying a snapshot from the service failed", File.ReadAllText(log.FilePath));

        monitor.Current = UiTestHelpers.SampleSnapshot();
        UiTestHelpers.PumpUntil(() => ctrl.HasSensors);

        Assert.True(ctrl.Connected); // the poll loop went on
        ctrl.ErrorNotice.DismissCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named(window, "ErrorNoticeToast").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void AnUnhandledUiException_IsRecordedAndShown_OncePerMinute()
    {
        GuiErrorLog log = Log();
        MainController ctrl = Start(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()), log);

        ctrl.ReportUnhandled(new InvalidOperationException("boom"));

        Assert.True(ctrl.ErrorNotice.IsError);
        Assert.Contains("[ERR] UI thread: Unhandled exception - the GUI keeps running", File.ReadAllText(log.FilePath));

        ctrl.ErrorNotice.DismissCommand.Execute(null);
        ctrl.ReportUnhandled(new InvalidOperationException("boom")); // e.g. a pointer handler failing on every move
        Assert.Equal("", ctrl.ErrorNotice.Text); // the log counts the repeat; the toast does not come back each time
    }

    [AvaloniaFact]
    public void RecordError_OnlyRecords()
    {
        GuiErrorLog log = Log();
        MainController ctrl = Start(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()), log);

        ctrl.RecordError("Task", "Unobserved task exception", new IOException("gone"));

        Assert.Equal("", ctrl.ErrorNotice.Text);
        Assert.Contains("[ERR] Task: Unobserved task exception", File.ReadAllText(log.FilePath));
    }

    [AvaloniaFact]
    public void TheTestController_FailsTheTest_WhenItCatchesAnError()
    {
        // UiTestHelpers.NewController: without it, ApplyGuarded would hide a regression in Apply from every other test.
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot() with { Sensors = null! });
        MainController ctrl = UiTestHelpers.NewController(monitor, pollInterval: TimeSpan.FromMilliseconds(10));
        _controllers.Add(ctrl);

        var failure = Assert.ThrowsAny<InvalidOperationException>(() => UiTestHelpers.PumpUntil(() => false, 2000));

        Assert.Contains("The controller caught an error", failure.Message);
        Assert.Contains("Applying a snapshot from the service failed", failure.Message);
    }

    // --- Data the GUI cannot read ----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void UnreadableData_LocksLikeOffline_AndSaysWhy()
    {
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        MainController ctrl = Start(monitor);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Connected && ctrl.Fans.Count > 0);

        monitor.Current = MonitorSnapshot.Unavailable("unlesbar", ConnectionProblem.UnreadableData);
        UiTestHelpers.PumpUntil(() => !ctrl.Connected);

        // Nothing current to show: the overview keeps the last state, dimmed and stamped, like offline.
        Assert.True(ctrl.OverviewFrozen);
        OverviewHint hint = Assert.Single(ctrl.Hints);
        Assert.Equal("unreadable", hint.Key);
        // Nothing to act on: saving, switching and the device actions are locked.
        Assert.False(ctrl.Editor.IsConnected);
        Assert.False(ctrl.Editor.CanSwitchProfile);
        FanRow fan = Assert.Single(ctrl.Fans);
        Assert.True(fan.IsOffline);
        Assert.False(fan.CanCalibrate);
        // The header and the banner name the cause - no "Reconnect", the connection stands.
        Assert.Equal("Profil · Daten unlesbar", ctrl.ProfileCaption);
        Assert.Equal("Daten nicht lesbar", ctrl.Status);
        Assert.True(Named(window, "OfflineBanner").IsEffectivelyVisible);
        Assert.Equal("Daten des Hintergrunddienstes nicht lesbar",
            window.Find<TextBlock>().Single(t => t.Name == "OfflineBannerTitle").Text);
        Assert.False(window.Find<Button>().Single(b => b.Name == "ReconnectButton").IsEffectivelyVisible);
        SelectableTextBlock path = window.Find<SelectableTextBlock>().Single(t => t.Name == "UnreadableLogPathText");
        Assert.True(path.IsEffectivelyVisible);
        Assert.Equal(ctrl.ErrorLogPath, path.Text);

        monitor.Current = UiTestHelpers.SampleSnapshot();
        UiTestHelpers.PumpUntil(() => ctrl.Connected);

        Assert.False(Named(window, "OfflineBanner").IsEffectivelyVisible);
        Assert.False(ctrl.OverviewFrozen);
    }

    // --- Backup import: success only once the service reports the new configuration back -------------------------------

    private static string BackupWith(string fanName) =>
        new BackupController(
            () => UiTestHelpers.SampleSnapshot().Config with
            {
                Fans = new[] { new FanConfig { FanId = "hwmon0/pwm1", Name = fanName, AssignedCurveId = "c1" } },
            },
            _ => Task.FromResult(true), () => Task.FromResult(true),
            new SettingsController(new UiSettingsStore(Path.Combine(Path.GetTempPath(), $"linfan-src-{Guid.NewGuid():N}.json"))),
            () => Task.FromResult(true), () => { }).BuildBackupJson();

    private static Task<BackupImportResult> Import(MainController ctrl, string json) =>
        ctrl.Backup.ImportAsync(ctrl.Backup.ReadImport(json) ?? throw new InvalidOperationException("not importable"));

    /// <summary>Hands the poll loop a new instance of the same state every few ms - daemon ticks that change nothing.</summary>
    private static void TickUnchanged(FakeLiveMonitor monitor, Func<bool> until, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
        {
            monitor.Current = monitor.Current with { };
            Thread.Sleep(5);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Import_ReportedBackByTheService_IsImported_AndRebuildsTheEditor()
    {
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        MainController ctrl = Start(monitor);
        UiTestHelpers.PumpUntil(() => ctrl.Connected && ctrl.Editor.IsReady);

        Task<BackupImportResult> import = Import(ctrl, BackupWith("Imported Fan"));
        UiTestHelpers.PumpUntil(() => monitor.ReplaceCalls.Count == 1);
        Assert.False(import.IsCompleted);
        Assert.Equal("Backup wird übernommen … warte auf die Bestätigung des Dienstes.", ctrl.Backup.Status.Text);

        monitor.Current = UiTestHelpers.SampleSnapshot() with { Config = monitor.ReplaceCalls[0] }; // the daemon took it
        UiTestHelpers.PumpUntil(() => import.IsCompleted);

        Assert.True(import.IsCompleted, "the import has no verdict"); // .Result would block the dispatcher otherwise
        Assert.True(import.Result.Success);
        Assert.Equal("Backup importiert.", ctrl.Backup.Status.Text);
        Assert.Equal("Imported Fan", Assert.Single(ctrl.Editor.Fans).Name);
    }

    [AvaloniaFact]
    public void Import_TheServiceDoesNotReportBack_IsNotClaimed_AfterTheGraceTicks()
    {
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        MainController ctrl = Start(monitor);
        UiTestHelpers.PumpUntil(() => ctrl.Connected && ctrl.Editor.IsReady);

        Task<BackupImportResult> import = Import(ctrl, BackupWith("Refused Fan"));
        UiTestHelpers.PumpUntil(() => monitor.ReplaceCalls.Count == 1);

        // The same snapshot again and again is no daemon tick - it must not use up the grace.
        int reads = monitor.ReadCount;
        UiTestHelpers.PumpUntil(() => monitor.ReadCount > reads + MainController.ResyncGraceTicks * 2);
        Assert.False(import.IsCompleted);

        TickUnchanged(monitor, () => import.IsCompleted); // the daemon refused it (limits): it keeps sending the old config

        Assert.True(import.IsCompleted, "the import has no verdict");
        Assert.False(import.Result.Success);
        Assert.True(ctrl.Backup.Status.IsError);
        Assert.Contains("nicht bestätigt", ctrl.Backup.Status.Text);
    }

    [AvaloniaFact]
    public void Import_WhileTheConnectionDrops_IsNotClaimed()
    {
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        MainController ctrl = Start(monitor);
        UiTestHelpers.PumpUntil(() => ctrl.Connected && ctrl.Editor.IsReady);

        Task<BackupImportResult> import = Import(ctrl, BackupWith("Lost Fan"));
        UiTestHelpers.PumpUntil(() => monitor.ReplaceCalls.Count == 1);
        monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
        UiTestHelpers.PumpUntil(() => import.IsCompleted);

        Assert.True(import.IsCompleted, "the import has no verdict");
        Assert.False(import.Result.Success);
        Assert.Contains("nicht bestätigt", ctrl.Backup.Status.Text);
    }
}
