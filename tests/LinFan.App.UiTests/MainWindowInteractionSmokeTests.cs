// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Tier-2-Smoke-Tests: Interaktions-/Zustands-Bindings im Hauptfenster — Disconnect-Banner,
/// Kalibrier-Button-Sichtbarkeit nach <c>CanControl</c> und die Löschen-Bestätigung (kein direkter Command).
/// </summary>
public class MainWindowInteractionSmokeTests
{
    private static (MainController ctrl, MainWindow window) ShowMain(MonitorSnapshot snapshot)
    {
        var ctrl = new MainController(new FakeLiveMonitor(snapshot));
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

    [AvaloniaTheory]
    [InlineData(true)]   // connected -> toast hidden
    [InlineData(false)]  // disconnected -> toast shown
    public void DisconnectToast_TracksConnected(bool connected)
    {
        MonitorSnapshot snap = connected
            ? UiTestHelpers.SampleSnapshot()
            : new MonitorSnapshot("getrennt", Array.Empty<SensorReading>(), Array.Empty<FanReading>(),
                AppConfig.Empty, Connected: false);
        var (ctrl, window) = ShowMain(snap);
        UiTestHelpers.PumpUntil(() => ctrl.Status == (connected ? "Verbunden" : "getrennt"));

        TextBlock banner = window.Find<TextBlock>()
            .Single(t => t.Text != null && t.Text.Contains("Hintergrunddienst nicht erreichbar"));

        Assert.Equal(!connected, banner.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void DisconnectToast_HiddenByX_RearmsOnNextDisconnect()
    {
        var snap = new MonitorSnapshot("getrennt", Array.Empty<SensorReading>(), Array.Empty<FanReading>(),
            AppConfig.Empty, Connected: false);
        var (ctrl, window) = ShowMain(snap);
        UiTestHelpers.PumpUntil(() => ctrl.Status == "getrennt");

        TextBlock toast = window.Find<TextBlock>()
            .Single(t => t.Text != null && t.Text.Contains("Hintergrunddienst nicht erreichbar"));
        Assert.True(toast.IsEffectivelyVisible);

        // X hides the toast while still disconnected.
        ctrl.HideDisconnectedToastCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(toast.IsEffectivelyVisible);

        // Reconnect, then the next disconnect transition re-arms the toast.
        ctrl.Connected = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(toast.IsEffectivelyVisible);

        ctrl.Connected = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(toast.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Geraete_CalibrateButton_VisibleOnlyForControllableFans()
    {
        var snap = UiTestHelpers.SnapshotWithFans(
            new FanReading("pwm1", "Steuerbar", 1000, 120, FanMode.Auto, CanControl: true),
            new FanReading("pwm2", "Nur lesbar", 800, 0, FanMode.Auto, CanControl: false));
        var (ctrl, window) = ShowMain(snap);
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);
        SelectSettingsSection(ctrl, SettingsSection.Fans); // Kalibrieren lebt in der Lüfter-Sektion

        // Kalibrieren sitzt in der aufklappbaren „Erweitert"-Sektion — aufklappen, sonst entscheidet das
        // Aufklapp-Flag (nicht CanControl) über die effektive Sichtbarkeit.
        foreach (FanAssignRow f in ctrl.Editor.Fans)
            f.ShowAdvanced = true;
        Dispatcher.UIThread.RunJobs();

        var calButtons = window.Find<Button>().Where(b => UiTestHelpers.ButtonLabel(b) == "Kalibrieren").ToList();
        Assert.Equal(2, calButtons.Count); // je Lüfter eine

        foreach (Button b in calButtons)
        {
            var row = Assert.IsType<FanAssignRow>(b.DataContext);
            // Der Kalibrier-Block steckt in einem StackPanel mit IsVisible="{Binding CanControl}".
            Assert.Equal(row.CanControl, b.IsEffectivelyVisible);
        }
    }

    [AvaloniaFact]
    public void Delete_HasNoDirectCommand_AndDeleteCommandsWork()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabCurves); // Kurven & Zuordnung (Profil- und Kurven-Löschen leben hier)

        var deleteButtons = window.Find<Button>().Where(b => UiTestHelpers.ButtonLabel(b) == "Löschen").ToList();
        Assert.Equal(2, deleteButtons.Count); // Profil + Kurve

        // Löschen ist über einen modalen ConfirmDialog (Click-Handler im Code-Behind) bestätigungs-gegated,
        // NICHT über einen direkten Command am Button. Ein versehentliches direktes Command-Binding (das ohne
        // Nachfrage löschen würde) wäre die Regression, die dieser Test fängt.
        Assert.All(deleteButtons, b => Assert.Null(b.Command));

        // Die Aktionen, die der Dialog bei Bestätigung ausführt, löschen tatsächlich. Der modale Dialog selbst
        // ist dünne Code-Behind-Verdrahtung (ConfirmThen) und wird headless nicht getrieben.
        int curvesBefore = ctrl.Editor.Curves.Count;
        int profilesBefore = ctrl.Editor.Profiles.Count;
        Assert.True(curvesBefore >= 1 && profilesBefore >= 1);

        ctrl.Editor.DeleteCurveCommand.Execute(null);
        Assert.Equal(curvesBefore - 1, ctrl.Editor.Curves.Count);

        ctrl.Editor.DeleteProfileCommand.Execute(null);
        Assert.Equal(profilesBefore - 1, ctrl.Editor.Profiles.Count);
    }

    [AvaloniaTheory]
    [InlineData(true)]   // verbunden → „Übernehmen" klickbar
    [InlineData(false)]  // getrennt → „Übernehmen" gesperrt (SaveConfig liefe sonst ins Leere)
    public void ApplyButton_IsEnabled_TracksConnected(bool connected)
    {
        MonitorSnapshot snap = connected
            ? UiTestHelpers.SampleSnapshot()
            // getrennt, aber mit derselben Config: der Editor wird bereit, Connected=false
            : UiTestHelpers.SampleSnapshot() with { Status = "getrennt", Connected = false };
        var (ctrl, window) = ShowMain(snap);
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.Connected == connected);
        ctrl.Editor.HasUnsavedChanges = true; // globales Banner (Übernehmen/Verwerfen) einblenden

        Button apply = window.Find<Button>().Single(b => UiTestHelpers.ButtonLabel(b) == "Übernehmen");

        // DataContext am Button ist der MainController: {Binding Connected} löst gegen ihn auf.
        Assert.IsType<MainController>(apply.DataContext);
        Assert.Equal(connected, apply.IsEnabled);
    }

    [AvaloniaFact]
    public void Curve_DecreasingPowerWarning_VisibilityTracksRow()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabCurves); // Kurven & Zuordnung

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
        SelectTab(window, TabCurves); // Kurven & Zuordnung

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
        UiTestHelpers.PumpUntil(() => ctrl.HasSnapshot);
        SelectTab(window, TabCurves); // Kurven & Zuordnung

        TextBlock noDevices = window.Find<TextBlock>()
            .Single(t => t.Text != null && t.Text.Contains("Keine Geräte erkannt — ohne Sensoren"));
        TextBlock loading = window.Find<TextBlock>().Single(t => t.DataContext == ctrl
            && t.Text != null && (t.Text == "Verbinde mit dem Hintergrunddienst …" || t.Text == "Lade Kurven …"));

        // Verbunden+leer: Leer-Hinweis sichtbar, Lade-Hinweis weg. Getrennt: umgekehrt — kein ewiges „Lädt".
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

    [AvaloniaFact]
    public void Geraete_PwmAdjustHint_VisibilityTracksHint()
    {
        var snap = UiTestHelpers.SnapshotWithFans(
            new FanReading("hwmon0/pwm1", "CPU Fan", 1000, 120, FanMode.Auto, CanControl: true));
        var (ctrl, window) = ShowMain(snap);
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);
        SelectSettingsSection(ctrl, SettingsSection.Fans); // Min/Max-PWM leben in der Lüfter-Sektion

        // Min/Max-PWM samt Hinweis stecken in der „Erweitert"-Sektion — aufklappen, sonst nie effektiv sichtbar.
        foreach (FanAssignRow f in ctrl.Editor.Fans)
            f.ShowAdvanced = true;
        Dispatcher.UIThread.RunJobs();

        FanAssignRow fan = ctrl.Editor.Fans.Single();
        fan.MinPwm = 30;
        fan.MaxPwm = 200; // gültig → Hinweis bleibt leer
        Dispatcher.UIThread.RunJobs();

        // Der PWM-Hinweis-TextBlock dieser Zeile: einziger #FBBF24-TextBlock mit DataContext == fan.
        TextBlock hint = window.Find<TextBlock>().Single(t =>
            t.DataContext == fan
            && t.Foreground is Avalonia.Media.ISolidColorBrush b
            && b.Color == Avalonia.Media.Color.Parse("#FBBF24"));

        Assert.Equal("", fan.PwmAdjustHint);
        Assert.False(hint.IsEffectivelyVisible); // leerer Hinweis → über StringNotEmptyConverter ausgeblendet

        // Min über Max anheben → Hinweis wird gesetzt und (über den Converter) sichtbar.
        fan.MinPwm = 220;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual("", fan.PwmAdjustHint);
        Assert.Equal(fan.PwmAdjustHint, hint.Text);
        Assert.True(hint.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Geraete_GroupSelect_BindsToAvailableGroups_AndFansHaveNone()
    {
        // Only sensor groups feed the auto-completion now; fans group via their location.
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU", Group = "CPU" } },
            Fans = new[] { new FanConfig { FanId = "hwmon0/pwm1", Name = "CPU Fan" } },
        };
        var snap = new MonitorSnapshot(
            "Verbunden",
            new[] { new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0) },
            new[] { new FanReading("hwmon0/pwm1", "CPU Fan", 1200, 120, FanMode.Auto, CanControl: true) },
            config, Connected: true);

        var (ctrl, window) = ShowMain(snap);
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);

        SelectSettingsSection(ctrl, SettingsSection.Sensors);
        AutoCompleteBox sensorBox = window.Find<AutoCompleteBox>().Single(b => b.DataContext is SensorOption);
        Assert.Same(ctrl.Editor.AvailableGroups, sensorBox.ItemsSource);
        Assert.Equal(new[] { "CPU" }, ctrl.Editor.AvailableGroups);

        // The fan group field is gone — a reappearing AutoCompleteBox in the fans section would be a regression.
        SelectSettingsSection(ctrl, SettingsSection.Fans);
        Assert.DoesNotContain(window.Find<AutoCompleteBox>(), b => b.DataContext is FanAssignRow);
    }

    /// <summary>Opens the sensors section and returns the group chip together with its sensor row.</summary>
    private static (AutoCompleteBox box, SensorOption row) FindGroupChip(MainController ctrl, MainWindow window)
    {
        SelectSettingsSection(ctrl, SettingsSection.Sensors);
        AutoCompleteBox box = window.Find<AutoCompleteBox>().First(b => b.DataContext is SensorOption);
        return (box, (SensorOption)box.DataContext!);
    }

    /// <summary>Two sensors: one ungrouped (the chip under test), one with existing group "Zone A" (the suggestion).</summary>
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

    [AvaloniaFact]
    public void Geraete_GroupChip_CommitsSuggestion_OnDropDownClose()
    {
        var (ctrl, window) = ShowMain(SnapshotWithGroupedSensor());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);
        SelectSettingsSection(ctrl, SettingsSection.Sensors);
        AutoCompleteBox box = window.Find<AutoCompleteBox>()
            .First(b => b.DataContext is SensorOption { Group: "" });
        var row = (SensorOption)box.DataContext!;

        // Deterministic stand-in for a popup click: type a prefix, open the popup, let the adapter
        // write the picked suggestion into Text (SelectedItem), then the popup closes -> the
        // DropDownClosed handler must commit the SUGGESTION, not the typed prefix.
        box.Focus();
        box.Text = "Zo";
        Dispatcher.UIThread.RunJobs();
        box.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        box.SelectedItem = "Zone A";
        box.IsDropDownOpen = false; // raises DropDownClosed
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Zone A", row.Group);
    }

    [AvaloniaFact]
    public void Geraete_GroupChip_CommitsFreeText_OnFocusLoss()
    {
        var (ctrl, window) = ShowMain(UiTestHelpers.SampleSnapshot());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);
        (AutoCompleteBox box, SensorOption row) = FindGroupChip(ctrl, window);

        box.Focus();
        box.Text = "Zone neu";
        Dispatcher.UIThread.RunJobs();

        // Click-away: focus moves to another focusable control. Depending on whether typing opened the
        // popup, the commit runs via LostFocus (closed) or via the DropDownClosed that the focus loss
        // triggers — either path must land the typed text in the row.
        window.Find<TextBox>().First(t => t.Name == "SensorSearchBox").Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Zone neu", row.Group);
        Assert.Contains("Zone neu", ctrl.Editor.AvailableGroups);
    }

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
    public void ClickOnEmptySpace_CommitsPendingGroupEdit()
    {
        var (ctrl, window) = ShowMain(SnapshotWithGroupedSensor());
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        SelectTab(window, TabSettings);
        SelectSettingsSection(ctrl, SettingsSection.Sensors);
        AutoCompleteBox box = window.Find<AutoCompleteBox>()
            .First(b => b.DataContext is SensorOption { Group: "" });
        var row = (SensorOption)box.DataContext!;

        box.Focus();
        box.Text = "Zone frei";
        Dispatcher.UIThread.RunJobs();

        // Click-away must both release the focus AND land the typed group in the row (end to end:
        // global press handler -> window focus -> chip LostFocus/DropDownClosed commit).
        window.MouseDown(new Point(500, 30), MouseButton.Left);
        window.MouseUp(new Point(500, 30), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Zone frei", row.Group);
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

        using var ctrl = new MainController(new FakeLiveMonitor(snap));
        UiTestHelpers.PumpUntil(() => ctrl.SensorGroups.Count > 0);

        // "CPU" and "cpu" must merge into ONE dashboard block (first-seen casing names the header).
        SensorGroup group = Assert.Single(ctrl.SensorGroups);
        Assert.Equal("CPU", group.Name);
        Assert.Equal(2, group.Sensors.Count);
    }
}
