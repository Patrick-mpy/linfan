// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Views;

namespace LinFan.App.UiTests;

/// <summary>
/// Regression: the chevron of a collapsible header used to carry <c>RenderTransformOrigin="0.5,0.5"</c>,
/// meant as "centre" in WPF terms. Avalonia parses that as 0.5 <b>pixels</b> from the top-left (relative
/// units need "50%,50%"), so the icon flipped around its own corner, swung out of the 14px header row and
/// was cut off by the ToggleButton, which clips its content. Measured against the production XAML.
/// </summary>
public class ChevronTransformTests
{
    [AvaloniaFact]
    public void CurveEditorChevron_IsNotClipped_WhenTheSectionIsExpanded()
    {
        var controller = new MainController(new FakeLiveMonitor(UiTestHelpers.SampleSnapshot()));
        var window = new MainWindow { DataContext = controller };
        window.Show();
        UiTestHelpers.PumpUntil(() => controller.Editor.IsReady);

        window.Find<TabControl>().Single().SelectedIndex = 1; // Kurven-Tab
        Dispatcher.UIThread.RunJobs();

        PathIcon chevron = Assert.Single(
            window.Find<PathIcon>(), p => p.Classes.Contains("chevron") && p.IsEffectivelyVisible);
        Rect collapsed = ClipOf(chevron);

        controller.Editor.SelectedCurve!.ShowPoints = true;
        SettleAnimation(() => chevron.RenderTransform is TransformOperations t && t.Value.M22 <= -1 + 1e-6);

        // Same visible rectangle as before the flip: the icon stays inside its own 11x11 box.
        AssertSameRect(collapsed, ClipOf(chevron));
    }

    /// <summary>
    /// Compares with a 0.01px tolerance rather than exactly: the flip is a real-time animation whose final
    /// value can land a fraction of a pixel short under load. The bug this guards moved the clip by several
    /// pixels, so the bound still catches it.
    /// </summary>
    private static void AssertSameRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 2);
        Assert.Equal(expected.Y, actual.Y, 2);
        Assert.Equal(expected.Width, actual.Width, 2);
        Assert.Equal(expected.Height, actual.Height, 2);
    }

    private static Rect ClipOf(Visual visual)
    {
        var bounds = visual.GetTransformedBounds()
            ?? throw new InvalidOperationException("control is not rendered");
        return bounds.Clip;
    }

    /// <summary>
    /// Runs the transition to its end. The animation clock ticks on real time, so the pump has to let time
    /// pass - forcing render timer ticks back to back advances it by microseconds (see CollapsibleTests).
    /// </summary>
    private static void SettleAnimation(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
