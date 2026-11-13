// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.App.Views;

namespace LinFan.App.UiTests;

/// <summary>The version footer under the settings menu, fed by the real window's snapshot loop.</summary>
public class VersionFooterUiTests
{
    private const int TabSettings = 2;

    private static (MainController Ctrl, MainWindow Window, FakeLiveMonitor Monitor) Show(string? daemonVersion)
    {
        var monitor = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot() with { DaemonVersion = daemonVersion });
        var ctrl = UiTestHelpers.NewController(monitor);
        var window = new MainWindow { DataContext = ctrl };
        window.Show();
        UiTestHelpers.PumpUntil(() => UiTestHelpers.Applied(ctrl));
        Assert.Single(window.Find<TabControl>()).SelectedIndex = TabSettings;
        UiTestHelpers.PumpUntil(() => window.Find<Control>().Any(c => c.Name == "AppVersionText"));
        return (ctrl, window, monitor);
    }

    private static Control Named(MainWindow window, string name) => window.Find<Control>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void AnotherServiceBuild_ShowsItsLine_UntilTheServiceRunsThisBuild()
    {
        var (ctrl, window, monitor) = Show("0.0.1+0123456789");

        Assert.Equal(ctrl.Version.AppLabel, ((SelectableTextBlock)Named(window, "AppVersionText")).Text);
        Assert.True(Named(window, "RepositoryLink").IsEffectivelyVisible);
        Assert.True(Named(window, "ServiceVersionMismatch").IsEffectivelyVisible);

        monitor.Current = monitor.Current with { DaemonVersion = AppVersion.InformationalRaw };
        UiTestHelpers.PumpUntil(() => !ctrl.Version.ShowServiceMismatch);

        Assert.False(Named(window, "ServiceVersionMismatch").IsEffectivelyVisible);
    }
}
