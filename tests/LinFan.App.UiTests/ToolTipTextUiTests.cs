// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.UiTests;

/// <summary>
/// A tooltip is a logical child of its control, so a rule like "Border.navBadge TextBlock" also styled the tooltip's
/// text: the hint counter's tooltip came out dark on the dark tooltip and looked empty, and a disabled primary button's
/// read faint grey. Theme/ToolTipText.axaml gives tooltip text the tooltip's own look back. MainWindow includes it after its
/// own rules (those beat the App's include there); the App's include serves the other windows - see
/// <see cref="HardwareChangeDialogSmokeTests"/>.
/// </summary>
public class ToolTipTextUiTests
{
    private const string Fan = "hwmon0/pwm1";

    /// <summary>A dry run: its notice puts a 1 on the hint counter. Nothing unsaved, so "Apply" is disabled.</summary>
    private static (MainController Ctrl, MainWindow Window) Show()
    {
        var curve = new CurveConfig
        {
            Id = "quiet",
            Name = "Quiet",
            SourceSensorIds = new[] { "hwmon0/temp1" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU" } },
            Curves = new[] { curve },
            Fans = new[] { new FanConfig { FanId = Fan, Name = "CPU Fan", AssignedCurveId = "quiet" } },
            Profiles = new[] { new Profile { Id = "p", Name = "P", Curves = new[] { curve }, Assignments = new[] { new ProfileAssignment(Fan, "quiet") } } },
            ActiveProfileId = "p",
        };
        var snapshot = new MonitorSnapshot(
            "Verbunden",
            new[] { new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0) },
            new[] { new FanReading(Fan, "CPU Fan", 1200, 120, CanControl: true, ControlState: FanControlState.Curve, ControlCurveId: "quiet") },
            ProfileService.Apply(config, "p"),
            Connected: true,
            DryRun: true);
        var ctrl = UiTestHelpers.NewController(new FakeLiveMonitor(snapshot), pollInterval: TimeSpan.FromMilliseconds(20));
        var window = new MainWindow { DataContext = ctrl, Width = 1000, Height = 800 };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.HasLiveData && ctrl.NavHintCount == 1);
        return (ctrl, window);
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaTheory]
    [InlineData("NavHintBadge", "1 Hinweis")]   // a Window rule: Border.navBadge TextBlock (dark, bold, 11 px)
    [InlineData("ApplyButton", "Übernehmen")]   // an App rule: Button.primary:disabled TextBlock (faint)
    public void ToolTipText_KeepsTheToolTipsLook_UnderAStyledControl(string control, string text)
    {
        var (_, window) = Show();

        var (tip, block) = UiTestHelpers.OpenToolTip(Assert.Single(window.Find<Control>(), c => c.Name == control));

        Assert.StartsWith(text, block.Text);
        Assert.Equal(ColorOf(tip.Foreground), ColorOf(block.Foreground));
        Assert.Equal(tip.FontWeight, block.FontWeight);
        Assert.Equal(tip.FontSize, block.FontSize);
    }
}
