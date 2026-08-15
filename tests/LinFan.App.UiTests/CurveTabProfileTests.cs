// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Der Kurven-Tab trennt „im Editor geöffnet" von „regelt gerade": das Seitenmenü listet die Profile, ein
/// Klick öffnet sie nur. Geprüft werden die beiden Stellen, die außerhalb des Controllers liegen - die
/// Klick-Verdrahtung der rechten Seite und die Herkunft der Dashboard-Kurven.
/// </summary>
public class CurveTabProfileTests
{
    private const int TabCurves = 1;

    [AvaloniaFact]
    public void ClickingAProfile_OpensTheProfileEditor_ClickingACurveComesBack()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());

        ClickFirstRow(window, controller.Editor.Profiles);
        Assert.Equal(CurveTabPane.Profile, controller.Editor.Pane);

        ClickFirstRow(window, controller.Editor.Curves);
        Assert.Equal(CurveTabPane.Curve, controller.Editor.Pane);

        // Erneut aufs Profil - dieselbe Zeile, also OHNE Auswahländerung: nur der Tapped-Pfad bringt den
        // Profil-Editor zurück (die Regression, die ein SelectionChanged-Handler nicht fangen würde).
        ClickFirstRow(window, controller.Editor.Profiles);
        Assert.Equal(CurveTabPane.Profile, controller.Editor.Pane);
    }

    [AvaloniaFact]
    public void EditingANonActiveProfile_LeavesTheDashboardOnTheRunningCurves()
    {
        var (controller, window) = ShowCurvesTab(TwoProfileSnapshot());
        DashboardCurveRow before = Assert.Single(controller.ActiveCurves);
        Assert.Equal("quiet", before.Curve.Id); // p-silent läuft

        controller.Editor.SelectedProfile = controller.Editor.Profiles.Single(p => p.Id == "p-loud");
        controller.Editor.Curves.Single().Name = "Entwurf";
        UiTestHelpers.PumpUntil(() => false, timeoutMs: 60); // ein paar Live-Ticks abwarten
        Dispatcher.UIThread.RunJobs();

        DashboardCurveRow after = Assert.Single(controller.ActiveCurves);
        Assert.Equal("quiet", after.Curve.Id);   // unverändert das laufende Profil
        Assert.NotEqual("Entwurf", after.Curve.Name);
        Assert.Equal("p-silent", controller.Editor.ActiveProfile!.Id);
        Assert.False(controller.Editor.SelectedProfileIsActive);
        Assert.False(controller.Editor.Curves.Single().IsActive); // Badge: regelt nicht
    }

    private static (MainController Controller, MainWindow Window) ShowCurvesTab(MonitorSnapshot snapshot)
    {
        var controller = new MainController(new FakeLiveMonitor(snapshot));
        var window = new MainWindow { DataContext = controller };
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);
        window.Find<TabControl>().Single().SelectedIndex = TabCurves;
        Dispatcher.UIThread.RunJobs();
        return (controller, window);
    }

    /// <summary>Klickt die erste Zeile der ListBox, die an <paramref name="source"/> gebunden ist.</summary>
    private static void ClickFirstRow(MainWindow window, object source)
    {
        ListBox list = window.Find<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, source));
        ListBoxItem row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        Point center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)
                       ?? throw new InvalidOperationException("Zeile ist nicht gerendert.");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Zwei Profile mit je eigener Kurve; „p-silent" ist aktiv und regelt den einen Lüfter.</summary>
    private static MonitorSnapshot TwoProfileSnapshot()
    {
        var quiet = new CurveConfig
        {
            Id = "quiet",
            Name = "Quiet",
            SourceSensorIds = new[] { "hwmon0/temp1" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var loud = new CurveConfig
        {
            Id = "loud",
            Name = "Loud",
            SourceSensorIds = new[] { "hwmon0/temp1" },
            Points = new[] { new CurvePoint(30, 60), new CurvePoint(80, 100) },
        };
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU" } },
            Curves = new[] { quiet },
            Fans = new[] { new FanConfig { FanId = "hwmon0/pwm1", Name = "CPU Fan", AssignedCurveId = "quiet" } },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p-silent", Name = "Silent", Curves = new[] { quiet },
                    Assignments = new[] { new ProfileAssignment("hwmon0/pwm1", "quiet") },
                },
                new Profile
                {
                    Id = "p-loud", Name = "Loud", Curves = new[] { loud },
                    Assignments = new[] { new ProfileAssignment("hwmon0/pwm1", "loud") },
                },
            },
            ActiveProfileId = "p-silent",
        };

        return new MonitorSnapshot(
            "Verbunden",
            new[]
            {
                new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0),
                new SensorReading("hwmon0/fan1", "CPU Fan", SensorKind.FanRpm, "RPM", 1200),
            },
            new[] { new FanReading("hwmon0/pwm1", "CPU Fan", 1200, 120, FanMode.Auto, CanControl: true) },
            config,
            Connected: true);
    }
}
