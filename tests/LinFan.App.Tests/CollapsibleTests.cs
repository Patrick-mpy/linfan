// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using LinFan.App.Controls;

namespace LinFan.App.Tests;

/// <summary>
/// Layout contract of <see cref="Collapsible"/> - the mechanic the collapse animation rests on: the
/// container claims a growing fraction of the child's full height, so a build that compiles but pops
/// (progress jumping straight to the end value) still fails here.
///
/// Driven through the manually started <see cref="HeadlessUnitTestSession"/>, like
/// <see cref="ControlSubscriptionLifecycleTests"/> - see the note there on why [AvaloniaFact] is out.
/// </summary>
public sealed class CollapsibleTests
{
    private const double ChildHeight = 100;

    private static void OnUiThread(Action action)
    {
        HeadlessUnitTestSession session =
            HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CollapsibleTests).Assembly);
        session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static (StackPanel Host, Window Window) ShownHost()
    {
        var host = new StackPanel();
        var window = new Window { Content = host, Width = 240, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (host, window);
    }

    private static Collapsible Collapsed(bool isOpen) =>
        new() { IsOpen = isOpen, Child = new Border { Height = ChildHeight } };

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs(); // the layout pass is a dispatcher job, so this flushes it
    }

    [Fact]
    public void ClosedOnAttach_ClaimsNoHeightAndStaysOutOfTheTabOrder() => OnUiThread(() =>
    {
        Collapsible collapsible = Collapsed(isOpen: false);
        (StackPanel host, Window window) = ShownHost();

        host.Children.Add(collapsible);
        Layout(window);

        Assert.False(collapsible.IsVisible); // closed content must not stay focusable
        Assert.Equal(0, collapsible.Bounds.Height);
    });

    [Fact]
    public void OpenOnAttach_SnapsToFullHeightWithoutAnimating() => OnUiThread(() =>
    {
        Collapsible collapsible = Collapsed(isOpen: true);
        (StackPanel host, Window window) = ShownHost();

        host.Children.Add(collapsible);
        Layout(window);

        // No clock tick has happened yet: a section that starts open must already be at full height.
        Assert.True(collapsible.IsVisible);
        Assert.Equal(ChildHeight, collapsible.Bounds.Height);
    });

    [Fact]
    public void Opening_GrowsOverTimeInsteadOfPopping() => OnUiThread(() =>
    {
        Collapsible collapsible = Collapsed(isOpen: false);
        (StackPanel host, Window window) = ShownHost();
        host.Children.Add(collapsible);
        Layout(window);

        collapsible.IsOpen = true;
        Layout(window);

        // Visible right away (an invisible control does not lay out), but not yet at full height.
        Assert.True(collapsible.IsVisible);
        Assert.True(collapsible.Bounds.Height < ChildHeight,
            $"opened without animating: {collapsible.Bounds.Height} of {ChildHeight} on the first frame");

        // Opacity mirrors the animated progress, so it - not the layout-rounded height - says "done".
        PumpUntil(window, () => collapsible.Opacity >= 1);

        Assert.Equal(ChildHeight, collapsible.Bounds.Height, 3);
        Assert.Equal(ChildHeight, host.DesiredSize.Height, 3); // the surrounding layout gets the full height
    });

    [Fact]
    public void Closing_ShrinksToZeroAndHidesTheContent() => OnUiThread(() =>
    {
        Collapsible collapsible = Collapsed(isOpen: true);
        (StackPanel host, Window window) = ShownHost();
        host.Children.Add(collapsible);
        Layout(window);

        collapsible.IsOpen = false;
        Layout(window);

        Assert.True(collapsible.IsVisible); // still on screen while it shrinks
        Assert.True(collapsible.Bounds.Height > 0);

        PumpUntil(window, () => !collapsible.IsVisible);

        Assert.False(collapsible.IsVisible);
        Assert.Equal(0, host.DesiredSize.Height); // space fully released again
    });

    /// <summary>
    /// Pumps the headless render timer until <paramref name="done"/> holds. The transition clock runs on
    /// real time, so the pump has to let time pass - forcing ticks back to back advances it by microseconds.
    /// The deadline is generous against the 150 ms transition so a loaded machine cannot make this flaky.
    /// </summary>
    private static void PumpUntil(Window window, Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Layout(window);
        }
    }
}
