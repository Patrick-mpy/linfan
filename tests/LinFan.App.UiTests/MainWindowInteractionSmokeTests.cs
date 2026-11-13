// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Tier-2-Smoke-Tests: Interaktions-/Zustands-Bindings im Hauptfenster - Verbindungs-Banner (offline, Dry-Run),
/// Kalibrier-Button-Sichtbarkeit nach <c>CanControl</c> und Löschen ohne Rückfrage (mit „Rückgängig").
/// </summary>
public class MainWindowInteractionSmokeTests
{
    private static (MainController ctrl, MainWindow window) ShowMain(MonitorSnapshot snapshot)
    {
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(snapshot));
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        return (ctrl, window);
    }

    private static TabControl SelectTab(MainWindow window, int index)
    {
        TabControl tabs = Assert.Single(window.Find<TabControl>());
        tabs.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
        return tabs;
    }

    // Tab-Reihenfolge nach dem Umbau: 0 = Übersicht, 1 = Kurven, 2 = Einstellungen.
    private const int TabCurves = 1;
    private const int TabSettings = 2;

    /// <summary>Wählt im Einstellungen-Tab die Sektion (die Panels sind per IsVisible/EnumMatchConverter umgeschaltet).</summary>
    private static void SelectSettingsSection(MainController ctrl, SettingsSection section)
    {
        ctrl.Settings.SelectedSectionItem = ctrl.Settings.Sections.Single(s => s.Section == section);
        Dispatcher.UIThread.RunJobs();
    }

    private static MonitorSnapshot Offline(ConnectionProblem problem) => MonitorSnapshot.Unavailable("getrennt", problem);

    private static Border Named(MainWindow window, string name) => window.Find<Border>().Single(b => b.Name == name);

    private static Button NamedButton(MainWindow window, string name) => window.Find<Button>().Single(b => b.Name == name);

    /// <summary>Clicks <paramref name="button"/> with the mouse - through hit testing, so a disabled or hidden button fails.</summary>
    private static void Click(MainWindow window, Button button)
    {
        Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
                       ?? throw new InvalidOperationException("Button is not rendered.");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static string ReconnectLabel(MainWindow window) =>
        window.Find<TextBlock>().Single(t => t.Name == "ReconnectLabel").Text ?? "";

    /// <summary>A reconnect request that stays open until the test settles it - like a running connection attempt.</summary>
    private sealed class PendingReconnect : IReconnectable
    {
        private readonly TaskCompletionSource<bool> _attempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public Task<bool> RequestReconnectAsync()
        {
            Interlocked.Increment(ref _requests);
            return _attempt.Task;
        }

        public void Settle(bool connected) => _attempt.TrySetResult(connected);
    }

    private static (MainController Ctrl, MainWindow Window, FakeLiveMonitor Monitor, PendingReconnect Reconnect) ShowOffline()
    {
        var monitor = new FakeLiveMonitor(Offline(ConnectionProblem.Unreachable));
        var reconnect = new PendingReconnect();
        var ctrl = UiTestHelpers.NewController(monitor, pollInterval: TimeSpan.FromMilliseconds(10), reconnect: reconnect);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Connection.ShowOfflineBanner);
        return (ctrl, window, monitor, reconnect);
    }

    [AvaloniaFact]
    public void OfflineBanner_Unreachable_NamesTheCause_AndTheCommandToCheckTheService()
    {
        var (ctrl, window) = ShowMain(Offline(ConnectionProblem.Unreachable));
        UiTestHelpers.PumpUntil(() => ctrl.Connection.ShowOfflineBanner);

        Assert.True(Named(window, "OfflineBanner").IsEffectivelyVisible);
        Assert.False(Named(window, "DryRunBanner").IsEffectivelyVisible);
        Assert.Equal("Nicht verbunden", ctrl.Status); // the header does not repeat the banner
        Assert.Equal("Hintergrunddienst nicht erreichbar",
            window.Find<TextBlock>().Single(t => t.Name == "OfflineBannerTitle").Text);
        SelectableTextBlock command = window.Find<SelectableTextBlock>().Single(t => t.Name == "DiagnosticCommandText");
        Assert.True(command.IsEffectivelyVisible);
        Assert.Equal(ctrl.Connection.DiagnosticCommand, command.Text);
        Assert.True(NamedButton(window, "CopyDiagnosticButton").IsEffectivelyVisible);
        // The banner sits in its own row: the tabs start below it instead of being covered.
        Assert.True(Named(window, "OfflineBanner").Bounds.Bottom
                    <= window.Find<TabControl>().Single().Bounds.Top);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void OfflineBanner_AccessDenied_ShowsTheGroupHint_WithoutTheServiceCommand()
    {
        var (ctrl, window) = ShowMain(Offline(ConnectionProblem.AccessDenied));
        UiTestHelpers.PumpUntil(() => ctrl.Connection.ShowOfflineBanner);

        Assert.True(Named(window, "OfflineBanner").IsEffectivelyVisible);
        // The long group hint shows once, in the banner - the header line only says that there is a problem.
        Assert.Equal("Nicht verbunden", ctrl.Status);
        Assert.Equal("Zugriff auf den Hintergrunddienst verweigert",
            window.Find<TextBlock>().Single(t => t.Name == "OfflineBannerTitle").Text);
        Assert.Contains(window.Find<TextBlock>(), t => t.IsEffectivelyVisible && t.Text != null
                                                      && t.Text.Contains("melde dich danach ab und wieder an"));
        Assert.False(window.Find<SelectableTextBlock>().Single(t => t.Name == "DiagnosticCommandText").IsEffectivelyVisible);
        Assert.False(NamedButton(window, "CopyDiagnosticButton").IsEffectivelyVisible);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void OfflineBanner_StaysHidden_WhileTheFirstAttemptIsConnecting()
    {
        var (ctrl, window) = ShowMain(Offline(ConnectionProblem.Connecting));
        UiTestHelpers.PumpUntil(() => ctrl.Status == "getrennt");

        Assert.False(Named(window, "OfflineBanner").IsEffectivelyVisible); // no flash at start
        Assert.False(Named(window, "DryRunBanner").IsEffectivelyVisible);
        Assert.Equal("getrennt", ctrl.Status); // without a banner the header keeps the monitor's own status
        ctrl.Dispose();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Banners_WhileConnected_OnlyTheDryRunOne_AndOnlyInDryRun(bool dryRun)
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot() with { DryRun = dryRun });
        UiTestHelpers.PumpUntil(() => ctrl.Connected);

        Assert.False(Named(window, "OfflineBanner").IsEffectivelyVisible);
        Assert.Equal(dryRun, Named(window, "DryRunBanner").IsEffectivelyVisible);
        if (dryRun)
            Assert.Contains(window.Find<TextBlock>(), t => t.IsEffectivelyVisible
                                                          && t.Text == "Dry-Run - die Kurven steuern die Lüfter nicht");
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void OfflineBanner_GoesAway_WhenTheConnectionComesBack()
    {
        var monitor = new FakeLiveMonitor(Offline(ConnectionProblem.Unreachable));
        var ctrl = UiTestHelpers.NewController(monitor, pollInterval: TimeSpan.FromMilliseconds(10));
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Connection.ShowOfflineBanner);
        Assert.True(Named(window, "OfflineBanner").IsEffectivelyVisible);

        monitor.Current = UiTestHelpers.SampleSnapshot();
        UiTestHelpers.PumpUntil(() => ctrl.Connected);

        Assert.False(Named(window, "OfflineBanner").IsEffectivelyVisible);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void ReconnectButton_SaysConnectingWhilePending_ThenToastsTheReconnect()
    {
        var (ctrl, window, monitor, reconnect) = ShowOffline();
        Button button = NamedButton(window, "ReconnectButton");
        Assert.Equal("Erneut verbinden", ReconnectLabel(window));

        Click(window, button);

        Assert.Equal(1, reconnect.Requests);
        Assert.False(button.IsEffectivelyEnabled);
        Assert.Equal("Verbinde …", ReconnectLabel(window));

        monitor.Current = UiTestHelpers.SampleSnapshot();
        reconnect.Settle(connected: true);
        UiTestHelpers.PumpUntil(() => !ctrl.Connection.IsReconnecting);

        // Checked in the same pass that ended the attempt: the toast fades out after 4 s on the real clock.
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Equal("Wieder mit dem Hintergrunddienst verbunden.", ctrl.Connection.Status.Text);
        Assert.True(Named(window, "ReconnectedToast").IsEffectivelyVisible);

        UiTestHelpers.PumpUntil(() => ctrl.Connected);
        Assert.False(Named(window, "OfflineBanner").IsEffectivelyVisible);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void ReconnectButton_KeepsItsWidth_AndSaysWhenTheAttemptFailed()
    {
        var (ctrl, window, _, reconnect) = ShowOffline();
        Button button = NamedButton(window, "ReconnectButton");
        TextBlock note = window.Find<TextBlock>().Single(t => t.Name == "ReconnectFeedback");
        double width = button.Bounds.Width;
        Assert.Equal("", note.Text);

        Click(window, button);
        window.UpdateLayout();
        Assert.Equal("Verbinde …", ReconnectLabel(window));
        Assert.Equal(width, button.Bounds.Width); // the shorter label does not shrink the button

        reconnect.Settle(connected: false);
        UiTestHelpers.PumpUntil(() => !ctrl.Connection.IsReconnecting);
        window.UpdateLayout();

        Assert.Equal("Weiterhin keine Verbindung", note.Text);
        Assert.True(note.IsEffectivelyVisible);
        Assert.Equal(width, button.Bounds.Width); // nor does the note appearing under it
        Assert.Equal("", ctrl.Connection.Status.Text); // no success toast
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void CopyButton_PutsTheCommandOnTheClipboard_AndSaysCopied()
    {
        var (ctrl, window) = ShowMain(Offline(ConnectionProblem.Unreachable));
        UiTestHelpers.PumpUntil(() => ctrl.Connection.ShowOfflineBanner);
        Button copy = NamedButton(window, "CopyDiagnosticButton");
        Assert.Equal("Kopieren", UiTestHelpers.ButtonLabel(copy));

        Click(window, copy);
        UiTestHelpers.PumpUntil(() => ctrl.Connection.IsCopied);

        Assert.Equal("Kopiert", UiTestHelpers.ButtonLabel(copy));
        Task<string?> read = window.Clipboard!.TryGetTextAsync();
        UiTestHelpers.PumpUntil(() => read.IsCompleted);
        Assert.Equal(ctrl.Connection.DiagnosticCommand, read.Result);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void Geraete_CalibrateButton_VisibleOnlyForControllableFans()
    {
        var snap = UiTestHelpers.SnapshotWithFans(
            new FanReading("pwm1", "Steuerbar", 1000, 120, CanControl: true),
            new FanReading("pwm2", "Nur lesbar", 800, 0, CanControl: false));
        var (ctrl, window) = ShowMain(snap);
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);
        SelectSettingsSection(ctrl, SettingsSection.Fans); // Kalibrieren lebt in der Lüfter-Sektion

        // Kalibrieren sitzt in der aufklappbaren „Erweitert"-Sektion - aufklappen, sonst entscheidet das
        // Aufklapp-Flag (nicht CanControl) über die effektive Sichtbarkeit.
        foreach (FanAssignRow f in ctrl.Editor.Fans)
            f.ShowAdvanced = true;
        Dispatcher.UIThread.RunJobs();

        var calButtons = window.Find<Button>().Where(b => UiTestHelpers.ButtonLabel(b) == "Kalibrieren").ToList();
        Assert.Equal(2, calButtons.Count); // je Lüfter eine

        foreach (Button b in calButtons)
        {
            var row = Assert.IsType<FanAssignRow>(b.DataContext);
            // Die Aktionen von „Kalibrierung & Tacho" stecken in einem Panel mit IsVisible="{Binding CanControl}".
            Assert.Equal(row.CanControl, b.IsEffectivelyVisible);
        }
    }

    [AvaloniaFact]
    public void StatusToast_TurnsRed_OnError()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);

        ctrl.Editor.Status.Set("Gespeichert.");
        Dispatcher.UIThread.RunJobs();
        PathIcon icon = Assert.Single(ToastIconsOf(window, "Gespeichert."));
        Assert.Equal(Token(window, "InfoAccent"), Brush(icon.Foreground));
        Assert.Same(Resource(window, "IconCheckCircle"), icon.Data);

        ctrl.Editor.Status.Set("Speichern fehlgeschlagen.", isError: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Token(window, "DangerAccent"), Brush(icon.Foreground));
        Assert.Same(Resource(window, "IconAlertCircle"), icon.Data);
    }

    /// <summary>Die Severity-Icons des Toasts, der <paramref name="text"/> zeigt (über den gemeinsamen Grid-Vorfahren).</summary>
    private static IEnumerable<PathIcon> ToastIconsOf(MainWindow window, string text) =>
        window.Find<TextBlock>()
            .Where(t => t.Text == text)
            .SelectMany(t => t.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("toast"))
                              .GetVisualDescendants().OfType<PathIcon>())
            .Where(p => p.Classes.Contains("toastIcon"));

    private static object Resource(MainWindow window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out object? value), $"Ressource '{key}' fehlt.");
        return value!;
    }

    private static Color Token(MainWindow window, string key) => Brush(Resource(window, key));

    private static Color Brush(object? value) => Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;

    /// <summary>
    /// Löschen fragt nicht mehr nach: die Knöpfe sind direkt an die Editor-Commands gebunden, der Toast bietet
    /// „Rückgängig" an - und das holt das Gelöschte zurück.
    /// </summary>
    [AvaloniaFact]
    public void Delete_IsBoundDirectly_AndTheToastUndoesIt()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabCurves); // Profile & Kurven (Profil- und Kurven-Löschen leben hier)

        Button profileDelete = Assert.Single(window.Find<Button>(), b => b.Name == "ProfileDeleteButton");
        Button curveDelete = Assert.Single(window.Find<Button>(), b => b.Name == "CurveDeleteButton");
        Assert.Same(ctrl.Editor.DeleteProfileCommand, profileDelete.Command);
        Assert.Same(ctrl.Editor.DeleteCurveCommand, curveDelete.Command);
        Assert.False(profileDelete.IsEffectivelyEnabled); // the only profile, and it is running
        Assert.True(ToolTip.GetShowOnDisabled(profileDelete)); // so the tooltip can say why

        curveDelete.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(ctrl.Editor.Curves);
        Button undo = Assert.Single(window.Find<Button>(), b => b.Name == "StatusActionButton");
        Assert.True(undo.IsEffectivelyVisible);
        Assert.Equal("Rückgängig", undo.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Assert.Contains(window.Find<TextBlock>(), t => t.Text == "Kurve „Quiet“ gelöscht." && t.IsEffectivelyVisible);

        undo.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("c1", Assert.Single(ctrl.Editor.Curves).Id);
        Assert.False(ctrl.Editor.HasUnsavedChanges);
        Assert.False(undo.IsEffectivelyVisible); // the toast now says „Rückgängig gemacht." without an action
    }

    [AvaloniaTheory]
    [InlineData(true)]   // verbunden → „Übernehmen" klickbar
    [InlineData(false)]  // getrennt → „Übernehmen" gesperrt (SaveConfig liefe sonst ins Leere)
    public void ApplyButton_IsEnabled_TracksConnected(bool connected)
    {
        // Offline after having been connected: the editor is ready and keeps its state, Connected=false.
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        var ctrl = UiTestHelpers.NewController(monitor, pollInterval: TimeSpan.FromMilliseconds(10));
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        if (!connected)
            monitor.Current = Offline(ConnectionProblem.Unreachable);
        UiTestHelpers.PumpUntil(() => ctrl.Connected == connected);
        ctrl.Editor.SelectedCurve!.AddPointRow(95, 90); // eine echte Änderung blendet die Leiste ein
        Dispatcher.UIThread.RunJobs();

        // The editor learns the connection from the MainController; the button follows the command's CanExecute.
        Assert.Equal(connected, ctrl.Editor.IsConnected);
        Button apply = Assert.Single(window.Find<Button>(), b => b.Name == "ApplyButton");
        Assert.True(apply.IsEffectivelyVisible);
        Assert.Equal(connected, apply.IsEffectivelyEnabled);

        // Undo is no daemon call - it stays available offline.
        Button undo = Assert.Single(window.Find<Button>(), b => b.Name == "UndoButton");
        Assert.True(undo.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Curve_DecreasingPowerWarning_VisibilityTracksRow()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabCurves); // Profile & Kurven

        CurveEditRow curve = ctrl.Editor.SelectedCurve!;
        TextBlock warning = window.Find<TextBlock>()
            .Single(t => t.Text != null && t.Text.Contains("Leistung sinkt bei steigender Temperatur"));

        Assert.False(curve.HasDecreasingPercent);
        Assert.False(warning.IsEffectivelyVisible); // monotone Beispielkurve → keine Warnung

        // Einen Punkt unter den davor liegenden drücken → Warnung wird sichtbar.
        curve.AddPointRow(100, 5);
        Dispatcher.UIThread.RunJobs();
        Assert.True(curve.HasDecreasingPercent);
        Assert.True(warning.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Curve_NoSourceHint_VisibilityTracksSelection()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabCurves); // Profile & Kurven

        CurveEditRow curve = ctrl.Editor.SelectedCurve!;
        TextBlock hint = window.Find<TextBlock>()
            .Single(t => t.Text != null && t.Text.Contains("Kein Quell-Sensor gewählt"));

        // Beispielkurve hat einen Quell-Sensor → Hinweis aus.
        Assert.False(curve.HasNoSource);
        Assert.False(hint.IsEffectivelyVisible);

        // Alle Quellen abwählen → Hinweis erscheint (Live-Arbeitspunkt entfiele dann).
        foreach (SensorCheck c in curve.SensorChecks)
            c.Selected = false;
        Dispatcher.UIThread.RunJobs();

        Assert.True(curve.HasNoSource);
        Assert.True(hint.IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData(true)]   // verbunden, leer → „Keine Geräte erkannt" im Kurven-Tab
    [InlineData(false)]  // getrennt → kein Leer-Hinweis, stattdessen „Verbinde …"
    public void Curve_NoDevicesBorder_TracksConnectedEmpty(bool connected)
    {
        MonitorSnapshot snap = new(
            connected ? "Verbunden" : "nicht erreichbar",
            Array.Empty<SensorReading>(), Array.Empty<FanReading>(),
            AppConfig.Empty, Connected: connected);
        var (ctrl, window) = ShowMain(snap);
        UiTestHelpers.PumpUntil(() => UiTestHelpers.Applied(ctrl));
        SelectTab(window, TabCurves); // Profile & Kurven

        TextBlock noDevices = window.Find<TextBlock>()
            .Single(t => t.Text != null && t.Text.Contains("Keine Geräte erkannt - ohne Sensoren"));
        TextBlock loading = window.Find<TextBlock>().Single(t => t.DataContext == ctrl
            && t.Text != null && (t.Text == "Verbinde mit dem Hintergrunddienst …" || t.Text == "Lade Kurven …"));

        // Verbunden+leer: Leer-Hinweis sichtbar, Lade-Hinweis weg. Getrennt: umgekehrt - kein ewiges „Lädt".
        Assert.Equal(connected, ctrl.ShowNoDevices);
        Assert.Equal(connected, noDevices.IsEffectivelyVisible);
        Assert.Equal(!connected, ctrl.ShowCurveLoading);
        Assert.Equal(!connected, loading.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Geraete_SearchBox_FiltersSensorList()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings); // Einstellungen → Sensoren (Default-Sektion)

        Assert.Single(ctrl.Editor.FilteredSensors); // SampleSnapshot: genau ein Temperatursensor („CPU")

        TextBox search = window.Find<TextBox>().Single(t => t.Name == "SensorSearchBox");
        search.Text = "zzz"; // passt auf nichts
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("zzz", ctrl.Editor.SensorSearch); // Binding wirkt View → Controller (PropertyChanged-Trigger)
        Assert.Empty(ctrl.Editor.FilteredSensors);     // … und die gefilterte Liste folgt

        search.Text = "CPU";
        Dispatcher.UIThread.RunJobs();
        Assert.Single(ctrl.Editor.FilteredSensors);
    }

    /// <summary>Two sensors: one ungrouped, one in group "Zone A" - the group whose name field the tests edit.</summary>
    private static MonitorSnapshot SnapshotWithGroupedSensor()
    {
        var config = new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU" },
                new SensorConfig { SensorId = "hwmon0/temp2", Name = "Board", Group = "Zone A" },
            },
        };
        return new MonitorSnapshot(
            "Verbunden",
            new[]
            {
                new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0),
                new SensorReading("hwmon0/temp2", "Board", SensorKind.Temperature, "°C", 40.0),
            },
            Array.Empty<FanReading>(),
            config, Connected: true);
    }

    /// <summary>The name field in the header of group <paramref name="name"/>.</summary>
    private static TextBox GroupNameBox(MainWindow window, string name) =>
        window.Find<TextBox>().Single(t => t.Name == "GroupNameBox" && t.DataContext is SensorGroupSection s && s.Name == name);

    [AvaloniaFact]
    public void ClickOnEmptySpace_ReleasesInputFocus()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);
        SelectSettingsSection(ctrl, SettingsSection.Sensors);

        TextBox search = window.Find<TextBox>().First(t => t.Name == "SensorSearchBox");
        search.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(search, window.FocusManager!.GetFocusedElement());

        // Press on empty header space (no focusable ancestor there) -> the window takes focus and the
        // TextBox gets LostFocus; before the global handler, focus stuck on the TextBox forever.
        window.MouseDown(new Point(500, 30), MouseButton.Left);
        window.MouseUp(new Point(500, 30), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(search, window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void ClickOnEmptySpace_CommitsPendingGroupRename()
    {
        var (ctrl, window) = ShowMain(SnapshotWithGroupedSensor());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);
        SelectSettingsSection(ctrl, SettingsSection.Sensors);
        TextBox box = GroupNameBox(window, "Zone A");

        box.Focus();
        box.Text = "Zone frei";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Zone A", ctrl.Editor.Sensors.Single(s => s.Id == "hwmon0/temp2").Group); // typing only drafts

        // Click-away must both release the focus AND rename the group (end to end: global press handler ->
        // window focus -> the field's LostFocus commit).
        window.MouseDown(new Point(500, 30), MouseButton.Left);
        window.MouseUp(new Point(500, 30), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Zone frei", ctrl.Editor.Sensors.Single(s => s.Id == "hwmon0/temp2").Group);
        Assert.Equal(new[] { "Zone frei" }, ctrl.Editor.SensorGroups);
    }

    [AvaloniaFact]
    public void Dashboard_SensorGroups_MergeCaseInsensitively()
    {
        var config = new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon0/temp1", Name = "A", Group = "CPU" },
                new SensorConfig { SensorId = "hwmon0/temp2", Name = "B", Group = "cpu" },
            },
        };
        var snap = new MonitorSnapshot(
            "Verbunden",
            new[]
            {
                new SensorReading("hwmon0/temp1", "A", SensorKind.Temperature, "°C", 40.0),
                new SensorReading("hwmon0/temp2", "B", SensorKind.Temperature, "°C", 41.0),
            },
            Array.Empty<FanReading>(),
            config, Connected: true);

        using var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(snap));
        UiTestHelpers.PumpUntil(() => ctrl.SensorGroups.Count > 0);

        // "CPU" and "cpu" must merge into ONE dashboard block (first-seen casing names the header).
        SensorGroup group = Assert.Single(ctrl.SensorGroups);
        Assert.Equal("CPU", group.Name);
        Assert.Equal(2, group.Sensors.Count);
    }
}
