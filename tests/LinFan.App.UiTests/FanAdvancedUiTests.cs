// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Services;
using LinFan.App.Views;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.UiTests;

/// <summary>
/// Settings › Fans › Advanced in the real window: the range slider and the steppers are bound to the row's limits, the
/// start-point hint offers to raise Min, the test switch drives manual control, read-only fans only get the state
/// block, and a speed sensor another fan holds is locked in „assign by hand".
/// </summary>
public class FanAdvancedUiTests
{
    private const int TabSettings = 2;

    private static MonitorSnapshot Snapshot() => new(
        "Verbunden",
        new[]
        {
            new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0),
            new SensorReading("hwmon0/fan1", "Fan 1", SensorKind.FanRpm, "RPM", 1180),
            new SensorReading("hwmon0/fan2", "Fan 2", SensorKind.FanRpm, "RPM", 820),
        },
        new[]
        {
            new FanReading("hwmon0/pwm1", "CPU Fan", 1180, 128, CanControl: true, ControlState: FanControlState.Curve),
            new FanReading("hwmon0/pwm2", "Front", 820, 100, CanControl: true, ControlState: FanControlState.HardwareAuto),
            new FanReading("gpu/pwm1", "GPU", 1450, 0, CanControl: false),
        },
        new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU" } },
            Fans = new[]
            {
                new FanConfig
                {
                    FanId = "hwmon0/pwm1", Name = "CPU Fan", MinPwm = 30, MaxPwm = 255,
                    Calibration = new FanCalibration { StartPwm = 46 },
                },
                new FanConfig { FanId = "hwmon0/pwm2", Name = "Front", RpmSource = "hwmon0/fan2" },
            },
        },
        Connected: true);

    private static (MainController Ctrl, MainWindow Window, FakeLiveMonitor Monitor) Show()
    {
        var monitor = new FakeLiveMonitor(Snapshot());
        var ctrl = UiTestHelpers.NewController(monitor);
        var window = new MainWindow { DataContext = ctrl, Height = 1400 };
        window.Show();
        UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady);
        Assert.Single(window.Find<TabControl>()).SelectedIndex = TabSettings;
        ctrl.Settings.SelectedSectionItem = ctrl.Settings.Sections.Single(s => s.Section == SettingsSection.Fans);
        foreach (FanAssignRow f in ctrl.Editor.Fans)
            f.ShowAdvanced = true;
        Dispatcher.UIThread.RunJobs();
        return (ctrl, window, monitor);
    }

    private static FanAssignRow Fan(MainController ctrl, string id) => ctrl.Editor.Fans.Single(f => f.FanId == id);

    private static T InRow<T>(MainWindow window, FanAssignRow row, Func<T, bool>? where = null) where T : Control =>
        window.Find<T>().Single(c => c.DataContext == row && c.IsEffectivelyVisible && (where?.Invoke(c) ?? true));

    [AvaloniaFact]
    public void RangeSlider_AndSteppers_AreBoundToTheLimits_WithTheStartPointMarked()
    {
        var (ctrl, window, _) = Show();
        FanAssignRow cpu = Fan(ctrl, "hwmon0/pwm1");
        RangeSlider range = InRow<RangeSlider>(window, cpu);

        Assert.Equal(cpu.MinPercent, range.LowerValue);
        Assert.Equal(100, range.UpperValue);
        Assert.Equal(18, range.Marker);

        range.SetCurrentValue(RangeSlider.UpperValueProperty, 80d); // as a drag does
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(80, cpu.MaxPercent);

        NumericUpDown max = InRow<NumericUpDown>(window, cpu, n => n.Minimum > 0);
        Assert.Equal(80, max.Value);
        Assert.True(cpu.MinPercent < 18);
        Assert.Equal(18, max.Minimum); // the max stepper cannot go below the start point (nor below min)

        range.SetCurrentValue(RangeSlider.UpperValueProperty, 5d); // dragged below the start point
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(18, cpu.MaxPercent);
        Assert.Equal(18, range.UpperValue); // the thumb snaps back to it
    }

    [AvaloniaFact]
    public void BelowStartHint_Shows_AndItsButtonRaisesMin()
    {
        var (ctrl, window, _) = Show();
        FanAssignRow cpu = Fan(ctrl, "hwmon0/pwm1");
        Grid hint = InRow<Grid>(window, cpu, g => g.Name == "StartHint");

        Button raise = hint.Find<Button>().Single();
        Assert.Equal("Min auf 18 % setzen", raise.Content);
        raise.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(46, cpu.MinPwm);
        Assert.False(hint.IsEffectivelyVisible);
    }

    /// <summary>
    /// The daemon's calibration check reaches the fan in the settings, not only the overview: a warning in place of the
    /// „calibrated" badge, the finding above the recalibrate button - and both go once the fan fits again.
    /// </summary>
    [AvaloniaFact]
    public void CalibrationMismatch_ShowsAtTheFan_AboveTheRecalibrateButton()
    {
        var (ctrl, window, monitor) = Show();
        FanAssignRow cpu = Fan(ctrl, "hwmon0/pwm1");
        MonitorSnapshot fits = monitor.Current;
        Grid hint = window.Find<Grid>().Single(g => g.DataContext == cpu && g.Name == "CalibrationMismatchHint");
        PathIcon badge = window.Find<PathIcon>().Single(i => i.DataContext == cpu && i.Name == "CalibrationMismatchBadge");
        Assert.False(hint.IsEffectivelyVisible);
        Assert.False(badge.IsEffectivelyVisible);

        monitor.Current = fits with
        {
            Fans = fits.Fans.Select(f => f.Id == cpu.FanId ? f with { CalibrationMismatch = CalibrationMismatch.Slower } : f).ToArray(),
        };
        UiTestHelpers.PumpUntil(() => cpu.HasCalibrationMismatch);
        Dispatcher.UIThread.RunJobs();

        const string Slower = "Dreht deutlich langsamer, als die Kalibrierung erwarten lässt - wurde der Lüfter getauscht? Neu kalibrieren empfohlen.";
        Assert.True(hint.IsEffectivelyVisible);
        Assert.Equal(Slower, hint.Find<TextBlock>().Single().Text);
        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal(Slower, ToolTip.GetTip(badge));
        Assert.DoesNotContain(window.Find<PathIcon>(), i => i.DataContext == cpu && i.IsEffectivelyVisible
            && ToolTip.GetTip(i) is string tip && tip == cpu.CalibrationBadgeHint);
        Assert.True(InRow<Button>(window, cpu, b => b.Name == "CalibrateButton").IsEffectivelyEnabled);

        monitor.Current = fits;
        UiTestHelpers.PumpUntil(() => !cpu.HasCalibrationMismatch);
        Dispatcher.UIThread.RunJobs();
        Assert.False(hint.IsEffectivelyVisible);
        Assert.False(badge.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TestSwitch_DrivesManualControl_FromTheLivePower()
    {
        var (ctrl, window, monitor) = Show();
        FanAssignRow cpu = Fan(ctrl, "hwmon0/pwm1");
        ListBox modes = InRow<ListBox>(window, cpu, l => l.Name == "TestModeSwitch");
        Assert.Equal(0, modes.SelectedIndex);

        modes.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.True(cpu.Manual.IsActive);
        Assert.Equal(50, cpu.Manual.Percent); // pwm 128 ≈ 50 % - where the fan already ran
        UiTestHelpers.PumpUntil(() => monitor.ManualCalls.Count > 0);
        Assert.All(monitor.ManualCalls, c => Assert.Equal(("hwmon0/pwm1", (byte)128), c));

        cpu.ShowAdvanced = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(cpu.Manual.IsActive);
        Assert.Contains("hwmon0/pwm1", monitor.AutoCalls);
    }

    [AvaloniaFact]
    public void ReadOnlyFan_GetsOnlyTheStateBlock_AndANote()
    {
        var (ctrl, window, _) = Show();
        FanAssignRow gpu = Fan(ctrl, "gpu/pwm1");

        List<Border> blocks = window.Find<Border>()
            .Where(b => b.DataContext == gpu && b.Classes.Contains("block") && b.IsEffectivelyVisible).ToList();
        Assert.Single(blocks);
        Assert.DoesNotContain(window.Find<RangeSlider>(), r => r.DataContext == gpu && r.IsEffectivelyVisible);
        Assert.Contains(window.Find<TextBlock>(), t => t.DataContext == gpu && t.IsEffectivelyVisible
            && t.Text == "Nur Anzeige - Leistungsbereich, Kalibrierung und Testen gibt es nur für steuerbare Lüfter.");
    }

    /// <summary>The row's tone ends up as the chip's class - the colour of the dot comes from Layout.axaml.</summary>
    [AvaloniaFact]
    public void StatusChips_CarryTheirToneAsClass()
    {
        var (ctrl, window, _) = Show();
        Border Chip(FanAssignRow row, string text) => window.Find<Border>().Single(b =>
            b.DataContext == row && b.Classes.Contains("statusChip") && b.IsEffectivelyVisible
            && b.Find<TextBlock>().Any(t => t.Text == text));

        Border calibrated = Chip(Fan(ctrl, "hwmon0/pwm1"), "kalibriert");
        Assert.Contains("ok", calibrated.Classes);
        Assert.DoesNotContain("warn", calibrated.Classes);
        Assert.Contains("muted", Chip(Fan(ctrl, "hwmon0/pwm2"), "nicht kalibriert").Classes);
    }

    /// <summary>A fan under hardware auto reports no power the test could start from: a dash, and full speed on engage.</summary>
    [AvaloniaFact]
    public void HardwareAutoFan_ShowsNoPower_AndManualStartsAtFullSpeed()
    {
        var (ctrl, _, monitor) = Show();
        FanAssignRow front = Fan(ctrl, "hwmon0/pwm2");
        Assert.Null(front.Manual.LivePercent);
        Assert.Equal("-", front.Manual.ShownPercentText);

        front.Manual.ModeIndex = 1;
        UiTestHelpers.PumpUntil(() => monitor.ManualCalls.Count > 0);

        Assert.Equal(("hwmon0/pwm2", (byte)255), monitor.ManualCalls[0]);
        front.ShowAdvanced = false;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void AssignByHand_LocksTheSensorAnotherFanHolds()
    {
        var (ctrl, window, monitor) = Show();
        FanAssignRow cpu = Fan(ctrl, "hwmon0/pwm1");
        cpu.ShowTachChoices = true;
        Dispatcher.UIThread.RunJobs();

        ListBox list = InRow<ListBox>(window, cpu, l => l.Classes.Contains("choiceList"));
        List<ListBoxItem> items = list.Find<ListBoxItem>().ToList();
        Assert.Equal(3, items.Count); // automatic, Fan 1, Fan 2
        Assert.True(items[1].IsEffectivelyEnabled);
        Assert.False(items[2].IsEffectivelyEnabled); // Fan 2 belongs to „Front"
        Assert.Equal("belegt: Front", ((TachChoice)items[2].DataContext!).TakenText);

        Fan(ctrl, "hwmon0/pwm2").Name = "Vorn"; // renaming the holder renames the lock
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("belegt: Vorn", ((TachChoice)items[2].DataContext!).TakenText);

        FanAssignRow front = Fan(ctrl, "hwmon0/pwm2");
        front.ShowTachChoices = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(front.TachChoices[1].IsTaken);

        list.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("hwmon0/pwm1", "hwmon0/fan1"), Assert.Single(monitor.SetTachometerCalls));
        Assert.Equal("Fan 1", cpu.TachName);
        Assert.Equal("belegt: CPU Fan", front.TachChoices[1].TakenText); // the other row follows the pick
    }
}
