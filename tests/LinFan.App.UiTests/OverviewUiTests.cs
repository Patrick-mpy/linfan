// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.UiTests;

/// <summary>
/// The overview tab through the real poll loop and window: offline freeze, tile order, hints, the control loops and
/// where the tiles lead.
/// </summary>
public class OverviewUiTests
{
    /// <summary>The sample with a calibrated fan, and the daemon reporting that its curve drives the fan.</summary>
    private static MonitorSnapshot Driven()
    {
        MonitorSnapshot sample = UiTestHelpers.SampleSnapshot();
        return sample with
        {
            Config = sample.Config with
            {
                Fans = new[]
                {
                    new FanConfig
                    {
                        FanId = "hwmon0/pwm1", Name = "CPU Fan", AssignedCurveId = "c1",
                        Calibration = new FanCalibration { StartPwm = 60, MinRpm = 400, MaxRpm = 1800 },
                    },
                },
            },
            Fans = new[]
            {
                new FanReading("hwmon0/pwm1", "CPU Fan", 1200, 120, CanControl: true,
                    ControlState: FanControlState.Curve, ControlCurveId: "c1"),
            },
        };
    }

    private static (MainController Ctrl, FakeLiveMonitor Monitor, MainWindow Window) Show(MonitorSnapshot snapshot)
    {
        var monitor = new FakeLiveMonitor(snapshot);
        var ctrl = UiTestHelpers.NewController(monitor, pollInterval: TimeSpan.FromMilliseconds(10));
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        return (ctrl, monitor, window);
    }

    private static T Named<T>(MainWindow window, string name) where T : Control =>
        window.Find<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void Tiles_SummariseTheRunningState()
    {
        var (ctrl, _, _) = Show(Driven());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Loops.Count == 1 && ctrl.Fans.Count == 1 && ctrl.Tiles.FanDetail.Contains("Kurven"));

            Assert.Equal("OK", ctrl.OverviewStatus.Headline);
            Assert.Equal("Temperaturen und Lüfter im Rahmen", ctrl.OverviewStatus.Detail);
            Assert.False(ctrl.ToggleHintsCommand.CanExecute(null)); // nothing to open
            Assert.Equal("45.0 °C", ctrl.Tiles.HottestValue);
            Assert.Equal("CPU · Ungruppiert", ctrl.Tiles.HottestName);
            Assert.Equal("Ø 47 % · 1200 RPM", ctrl.Tiles.FanHeadline);
            Assert.Equal("1 steuerbar · 1 über Kurven", ctrl.Tiles.FanDetail);
            Assert.Equal("Standard", ctrl.Tiles.ProfileName);
            Assert.Equal("1 Kurve aktiv", ctrl.Tiles.ProfileDetail);
            Assert.Equal("Sensor → Kurve → Lüfter · Profil „Standard“", ctrl.Tiles.LoopsSubtitle);

            FanRow fan = ctrl.Fans.Single();
            Assert.Equal("Quiet", fan.ControlLabel);
            Assert.True(fan.ControlOpensCurve);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Offline_FreezesTheLastState_AndLocksTheControls()
    {
        var (ctrl, monitor, window) = Show(Driven());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Loops.Count == 1 && ctrl.Fans.Count == 1);
            LoopRow loop = ctrl.Loops.Single();
            FanRow fan = ctrl.Fans.Single();
            UiTestHelpers.PumpUntil(() => loop.History.Count > 0);
            Assert.True(loop.CanToggle);
            Assert.False(Named<Border>(window, "FrozenStamp").IsEffectivelyVisible);

            monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
            UiTestHelpers.PumpUntil(() => ctrl.OverviewFrozen);
            int history = loop.History.Count;
            UiTestHelpers.PumpUntil(() => false, timeoutMs: 80); // several offline ticks

            // The last state stays - nothing is blanked, nothing says "no sensors".
            Assert.Same(loop, Assert.Single(ctrl.Loops)); // the running curves are not dropped
            Assert.Equal(history, loop.History.Count);     // ... and gain no points
            Assert.Equal("45.0 °C", ctrl.Temperatures.Single().Display);
            Assert.Equal("1200 RPM", fan.Rpm);
            Assert.False(ctrl.ShowNoSensors);
            Assert.False(ctrl.ShowNoFans);
            Assert.StartsWith("Stand von ", ctrl.FrozenAtText);
            Assert.True(Named<Border>(window, "FrozenStamp").IsEffectivelyVisible);

            // Nothing about the control is known, and nothing can be sent.
            Assert.Equal("unbekannt", fan.ControlLabel);
            Assert.False(fan.ControlOpensCurve);
            Assert.True(fan.IsOffline);
            Assert.False(fan.CanOperate);
            Assert.False(fan.CanCalibrate);
            Assert.Equal("Gesperrt - der Hintergrunddienst ist nicht erreichbar.", fan.CalibrateTooltip);
            Assert.False(loop.CanToggle);
            Assert.Equal("Kritisch", ctrl.OverviewStatus.Headline);
            Assert.Equal(HintSeverity.Critical, ctrl.Hints[0].Severity);

            monitor.Current = Driven();
            UiTestHelpers.PumpUntil(() => !ctrl.OverviewFrozen && loop.CanToggle);
            Assert.False(fan.IsOffline);
            Assert.Equal("Quiet", fan.ControlLabel);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void TemperatureTiles_FollowTheSavedGroupOrder_UngroupedLast()
    {
        MonitorSnapshot Snapshot(params string[] order) => new(
            "ok",
            new[]
            {
                new SensorReading("t1", "Tctl", SensorKind.Temperature, "°C", 50),
                new SensorReading("t2", "Board", SensorKind.Temperature, "°C", 40),
                new SensorReading("t3", "Loose", SensorKind.Temperature, "°C", 30),
                new SensorReading("t4", "Disk", SensorKind.Temperature, "°C", 35),
            },
            Array.Empty<FanReading>(),
            new AppConfig
            {
                Sensors = new[]
                {
                    new SensorConfig { SensorId = "t1", Group = "CPU" },
                    new SensorConfig { SensorId = "t2", Group = "Zone" },
                    new SensorConfig { SensorId = "t4", Group = "aaa" }, // used, but not in the saved list
                },
                SensorGroups = order,
            },
            Connected: true);

        var (ctrl, monitor, _) = Show(Snapshot("Zone", "CPU"));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.SensorGroups.Count == 4);
            Assert.Equal(new[] { "Zone", "CPU", "aaa", "Ungruppiert" }, ctrl.SensorGroups.Select(g => g.Name));

            // Reordering the saved list alone rebuilds the tiles - the order is part of the signature.
            monitor.Current = Snapshot("CPU", "Zone");
            UiTestHelpers.PumpUntil(() => ctrl.SensorGroups[0].Name == "CPU");
            Assert.Equal(new[] { "CPU", "Zone", "aaa", "Ungruppiert" }, ctrl.SensorGroups.Select(g => g.Name));
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void HotSensor_StatusOpensTheHints_AndShowOpensTheTileAcrossItsRow()
    {
        MonitorSnapshot hot = Driven() with
        {
            Sensors = new[] { new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 86.0) },
        };
        var (ctrl, _, window) = Show(hot);
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.OverviewStatus.Level == OverviewLevel.Critical);
            Assert.Equal("Kritisch", ctrl.OverviewStatus.Headline);
            Assert.Equal("CPU ist heiß (86.0 °C).", ctrl.OverviewStatus.Detail);
            Assert.True(ctrl.Tiles.HottestIsHot);
            Assert.True(ctrl.Temperatures.Single().IsHot);
            Assert.False(Named<Border>(window, "HintList").IsEffectivelyVisible);

            Button status = Named<Button>(window, "StatusTile");
            Assert.True(status.IsEffectivelyEnabled);
            status.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Named<Border>(window, "HintList").IsEffectivelyVisible);

            OverviewHint hint = ctrl.Hints.Single(h => h.Action == HintAction.ShowTemperatureGroup);
            hint.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            SensorGroup tile = ctrl.SensorGroups.Single();
            Assert.True(tile.IsOpen);
            ContentPresenter container = window.Find<ContentPresenter>()
                .Single(p => p.DataContext == tile && p.GetVisualParent() is AutoFillGrid);
            Assert.True(AutoFillGrid.GetSpansRow(container)); // the style binding reaches the panel
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Tiles_LeadToTheProfile_AndCurvesFallBackToIt()
    {
        var (ctrl, _, _) = Show(Driven());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.Loops.Count == 1);

            ctrl.EditCurve("c1");
            Assert.Equal(1, ctrl.SelectedTabIndex);
            Assert.Equal(CurveTabPane.Curve, ctrl.Editor.Pane);
            Assert.Equal("c1", ctrl.Editor.SelectedCurve!.Id);

            ctrl.SelectedTabIndex = 0;
            ctrl.EditCurve("deleted-in-the-editor");
            Assert.Equal(1, ctrl.SelectedTabIndex);
            Assert.Equal(CurveTabPane.Profile, ctrl.Editor.Pane); // not some other curve

            ctrl.SelectedTabIndex = 0;
            ctrl.Editor.Pane = CurveTabPane.Curve;
            ctrl.OpenRunningProfileCommand.Execute(null);
            Assert.Equal(1, ctrl.SelectedTabIndex);
            Assert.Equal(CurveTabPane.Profile, ctrl.Editor.Pane);
            Assert.Same(ctrl.Editor.ActiveProfile, ctrl.Editor.SelectedProfile);

            OverviewSection? requested = null;
            ctrl.OverviewScrollRequested += (_, s) => requested = s;
            ctrl.ShowFansCommand.Execute(null);
            Assert.Equal(OverviewSection.Fans, requested);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void LoopRow_IsRendered_WithItsMiniCurve()
    {
        var (ctrl, _, window) = Show(Driven());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Loops.Count == 1);
            Dispatcher.UIThread.RunJobs();

            MiniCurve mini = window.Find<MiniCurve>().Single();
            Assert.Equal(LoopRow.SampleCount, mini.Samples!.Count);
            Assert.Equal(45, mini.DotTemperature);
            ToggleSwitch toggle = window.Find<ToggleSwitch>().Single(t => t.DataContext is LoopRow);
            Assert.True(toggle.IsEffectivelyEnabled);
            Assert.Equal("Kurve „Quiet“ an/aus", Avalonia.Automation.AutomationProperties.GetName(toggle));
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void ThemeGrid_ArrowKeys_HomeAndEnd_MoveTheSelection()
    {
        string path = Path.Combine(Path.GetTempPath(), $"linfan-themegrid-{Guid.NewGuid():N}.json");
        var settings = new SettingsController(new UiSettingsStore(path)); // never the user's real settings
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()), settings: settings);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
            window.Find<TabControl>().Single().SelectedIndex = 2;
            ctrl.Settings.SelectedSectionItem = ctrl.Settings.Sections.Single(s => s.Section == SettingsSection.Appearance);
            Dispatcher.UIThread.RunJobs();

            ListBox grid = Named<ListBox>(window, "ThemeGrid");
            Assert.True(grid.IsEffectivelyVisible);
            Assert.Equal(ThemeChoice.System, settings.Theme);
            ListBoxItem first = grid.GetVisualDescendants().OfType<ListBoxItem>().First();
            first.Focus();

            void Press(Key key, PhysicalKey physical)
            {
                window.KeyPress(key, RawInputModifiers.None, physical, null);
                Dispatcher.UIThread.RunJobs();
            }

            Press(Key.Right, PhysicalKey.ArrowRight);
            Assert.Equal(ThemeChoice.Light, settings.Theme);
            Press(Key.End, PhysicalKey.End);
            Assert.Equal(ThemeChoice.HighContrast, settings.Theme);
            Press(Key.Home, PhysicalKey.Home);
            Assert.Equal(ThemeChoice.System, settings.Theme);
            Press(Key.Left, PhysicalKey.ArrowLeft); // wraps, like the radio group of the design
            Assert.Equal(ThemeChoice.HighContrast, settings.Theme);
        }
        finally
        {
            ctrl.Dispose();
            File.Delete(path);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [AvaloniaFact]
    public void Frozen_SaysTheTimeOfTheLastConnectedSnapshot()
    {
        var monitor = new FakeLiveMonitor(Driven());
        var ctrl = UiTestHelpers.NewController(monitor, pollInterval: TimeSpan.FromMilliseconds(10),
            clock: new FixedClock(new DateTimeOffset(2026, 11, 8, 14, 3, 12, TimeSpan.Zero)));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.HasLiveData);
            monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
            UiTestHelpers.PumpUntil(() => ctrl.OverviewFrozen);

            Assert.Equal("Stand von 14:03:12", ctrl.FrozenAtText);
            OverviewHint only = Assert.Single(ctrl.Hints); // offline: nothing from the frozen state, it may be stale
            Assert.Equal("offline", only.Key);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void Offline_LocksTheCurveSwitches_InTheOverviewAndTheCurvesTab()
    {
        var (ctrl, monitor, window) = Show(Driven());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.Loops.Count == 1);
            window.Find<TabControl>().Single().SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            ToggleSwitch curveTab = Named<ToggleSwitch>(window, "CurveEnabledSwitch");
            Assert.True(curveTab.IsEffectivelyEnabled);

            monitor.Current = MonitorSnapshot.Unavailable("weg", ConnectionProblem.Unreachable);
            UiTestHelpers.PumpUntil(() => ctrl.OverviewFrozen);
            Dispatcher.UIThread.RunJobs();

            Assert.False(curveTab.IsEffectivelyEnabled);
            Assert.False(ctrl.Loops.Single().CanToggle);
            ctrl.Editor.SelectedCurveEnabled = false; // even if something sets it: refused, nothing sent
            Assert.True(ctrl.Editor.SelectedCurveEnabled);
            Assert.Empty(monitor.CurveEnabledCalls);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void ALoopToggle_ReachesTheDaemon_AndTheRowSurvivesTheConfirmingRebuild()
    {
        var (ctrl, monitor, _) = Show(Driven());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Loops.Count == 1);
            LoopRow loop = ctrl.Loops.Single();
            UiTestHelpers.PumpUntil(() => loop.History.Count >= 2);
            int revision = ctrl.Editor.LiveCurvesRevision;

            loop.Enabled = false;
            Assert.Equal(("c1", false), Assert.Single(monitor.CurveEnabledCalls));

            // The daemon persists it: the running set comes back with the curve off and is rebuilt.
            MonitorSnapshot off = Driven();
            CurveConfig quietOff = off.Config.Curves.Single() with { Enabled = false };
            monitor.Current = off with { Config = off.Config with { Curves = new[] { quietOff } } };
            UiTestHelpers.PumpUntil(() => ctrl.Editor.LiveCurvesRevision > revision);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(loop, Assert.Single(ctrl.Loops)); // re-pointed, not replaced - a focused switch stays
            Assert.Same(ctrl.Editor.LiveCurves.Single(), loop.Curve);
            Assert.False(loop.Enabled);
            Assert.True(loop.History.Count >= 2); // the history is kept per curve id
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void AnOpenTile_StaysOpen_WhenTheTilesAreRebuilt()
    {
        var (ctrl, monitor, _) = Show(Driven());
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.SensorGroups.Count == 1);
            ctrl.SensorGroups.Single().ToggleCommand.Execute(null);

            MonitorSnapshot more = Driven();
            monitor.Current = more with
            {
                Sensors = more.Sensors.Append(new SensorReading("hwmon0/temp9", "Board", SensorKind.Temperature, "°C", 35)).ToArray(),
                Config = more.Config with { Sensors = more.Config.Sensors.Append(new SensorConfig { SensorId = "hwmon0/temp9", Group = "Mainboard" }).ToArray() },
            };
            UiTestHelpers.PumpUntil(() => ctrl.SensorGroups.Count == 2);

            Assert.True(ctrl.SensorGroups.Single(g => g.Key == "").IsOpen);
            Assert.False(ctrl.SensorGroups.Single(g => g.Key == "Mainboard").IsOpen);
        }
        finally { ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public void AHint_KeepsItsInstance_WhileItsValueChanges()
    {
        MonitorSnapshot Hot(double value) => Driven() with
        {
            Sensors = new[] { new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", value) },
        };
        var (ctrl, monitor, _) = Show(Hot(86.0));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Hints.Any(h => h.Text.Contains("86.0")));
            OverviewHint hint = ctrl.Hints.Single();

            monitor.Current = Hot(86.4);
            UiTestHelpers.PumpUntil(() => hint.Text.Contains("86.4"));

            Assert.Same(hint, ctrl.Hints.Single());
            Assert.Equal("Kritisch", ctrl.OverviewStatus.Headline);
        }
        finally { ctrl.Dispose(); }
    }
}
