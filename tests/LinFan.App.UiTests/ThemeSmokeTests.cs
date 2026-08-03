// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Views;

namespace LinFan.App.UiTests;

/// <summary>
/// Sichert den Live-Theme-Wechsel: das Umschalten von <see cref="ThemeVariant"/> löst die DynamicResource-
/// Tokens neu auf, ohne zu werfen — sowohl im Hauptfenster als auch im selbstgezeichneten
/// <see cref="CurveChart"/> (der seine Pinsel bei <c>ActualThemeVariantChanged</c> neu auflöst).
/// </summary>
public class ThemeSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_survives_theme_switch()
    {
        var controller = new MainController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var window = new MainWindow { DataContext = controller };
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);

        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Default })
        {
            Application.Current!.RequestedThemeVariant = variant;
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(window.Content);
        // Fenster bewusst NICHT schließen (würde Geometrie in die echte ui.json schreiben).
    }

    [AvaloniaFact]
    public void CurveChart_survives_theme_switch()
    {
        var chart = new CurveChart();
        var window = new Window { Content = chart };
        window.Show();
        Dispatcher.UIThread.RunJobs(); // OnAttachedToVisualTree → erstes Auflösen der Pinsel

        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(chart, window.Content);
    }
}
