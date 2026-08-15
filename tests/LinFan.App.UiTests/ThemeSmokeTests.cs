// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Views;

namespace LinFan.App.UiTests;

/// <summary>
/// Sichert den Live-Theme-Wechsel: das Umschalten von <see cref="ThemeVariant"/> löst die DynamicResource-
/// Tokens neu auf, ohne zu werfen - im Hauptfenster, in einem modalen Dialog und im selbstgezeichneten
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

    /// <summary>
    /// Ein Dialog fordert selbst keine Variante an - er muss die der <see cref="Application"/> erben und
    /// seinen Hintergrund entsprechend neu auflösen. Die native Titelleiste hängt am selben
    /// <c>ActualThemeVariant</c>, ist headless aber nicht prüfbar (kein HWND) - siehe WindowFrameTheme.
    /// </summary>
    [AvaloniaFact]
    public void ConfirmDialog_follows_the_application_theme()
    {
        ThemeVariant? before = Application.Current!.RequestedThemeVariant;
        try
        {
            var dialog = new ConfirmDialog("Titel", "Meldung", "OK");
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            Color BackgroundIn(ThemeVariant variant)
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(variant, dialog.ActualThemeVariant);
                return Assert.IsAssignableFrom<ISolidColorBrush>(dialog.Background).Color;
            }

            Assert.NotEqual(BackgroundIn(ThemeVariant.Light), BackgroundIn(ThemeVariant.Dark));
            dialog.Close();
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = before;
        }
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
