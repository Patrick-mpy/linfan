// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.Input;
using LinFan.App.Controllers;
using Xunit;

namespace LinFan.App.Tests;

public sealed class TransientStatusTests
{
    /// <summary>
    /// Runs <paramref name="test"/> without a synchronization context, so the auto-hide continuation runs right inside
    /// <see cref="ManualClock.Advance"/> instead of being posted to the test runner's context.
    /// </summary>
    private static void Inline(Action test)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            test();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void SetWithAction_CarriesTheAction_AndFadesOutAfterSevenSeconds() => Inline(() =>
    {
        var clock = new ManualClock();
        var status = new TransientStatus(clock: clock);
        var undo = new RelayCommand(() => { });

        status.SetWithAction("Kurve „A“ gelöscht.", "Rückgängig", undo);

        Assert.True(status.HasAction);
        Assert.Same(undo, status.ActionCommand);
        Assert.Equal("Rückgängig", status.ActionLabel);
        Assert.False(status.IsError);

        clock.Advance(TimeSpan.FromSeconds(6.9));
        Assert.Equal("Kurve „A“ gelöscht.", status.Text); // longer than a plain message: time to reach the button

        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal("", status.Text);
        Assert.False(status.HasAction); // a hidden toast leaves no orphaned action behind
        Assert.Equal("", status.ActionLabel);
    });

    [Fact]
    public void PlainMessage_FadesOutAfterItsOwnInterval() => Inline(() =>
    {
        var clock = new ManualClock();
        var status = new TransientStatus(clock: clock);

        status.Set("Gespeichert.", autoHide: true);
        clock.Advance(TimeSpan.FromSeconds(3.9));
        Assert.Equal("Gespeichert.", status.Text);
        clock.Advance(TimeSpan.FromSeconds(0.2));

        Assert.Equal("", status.Text);
    });

    [Fact]
    public void Set_DropsAPreviousAction_AndItsTimer() => Inline(() =>
    {
        var clock = new ManualClock();
        var status = new TransientStatus(clock: clock);
        status.SetWithAction("Profil „B“ gelöscht.", "Rückgängig", new RelayCommand(() => { }));

        status.Set("Fehler.", isError: true); // errors stay
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.False(status.HasAction);
        Assert.Equal("Fehler.", status.Text);
    });

    [Fact]
    public void Dismiss_ClearsTextAndAction()
    {
        var status = new TransientStatus();
        status.SetWithAction("Kurve „A“ gelöscht.", "Rückgängig", new RelayCommand(() => { }));

        status.DismissCommand.Execute(null);

        Assert.Equal("", status.Text);
        Assert.False(status.HasAction);
    }

    [Fact]
    public void Icon_FollowsTheMessage_AndFadesOutWithIt() => Inline(() =>
    {
        var clock = new ManualClock();
        var status = new TransientStatus(clock: clock);

        status.SetWithAction("Auf „B“ gewechselt.", "Rückgängig", new RelayCommand(() => { }), StatusIcon.Swap);
        Assert.Equal(StatusIcon.Swap, status.Icon);

        status.Set("Gespeichert.", autoHide: true); // the next message brings its own symbol
        Assert.Equal(StatusIcon.Default, status.Icon);

        status.SetWithAction("Auf „A“ gewechselt.", "Rückgängig", new RelayCommand(() => { }), StatusIcon.Swap);
        clock.Advance(TransientStatus.ActionAutoHide);
        Assert.Equal("", status.Text);
        Assert.Equal(StatusIcon.Default, status.Icon);
    });
}
