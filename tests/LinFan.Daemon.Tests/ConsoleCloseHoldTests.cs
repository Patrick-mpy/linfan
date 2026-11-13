// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using LinFan.Daemon;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// A closed console window must not end the process before the fans are handed back: on Windows the signal handler is
/// held until the shutdown has finished, everywhere else (and for every other signal) it returns at once.
/// </summary>
public class ConsoleCloseHoldTests
{
    [Theory]
    [InlineData(PosixSignal.SIGHUP)]
    [InlineData(PosixSignal.SIGTERM)]
    public async Task Hold_OnWindows_WaitsUntilTheShutdownHasFinished(PosixSignal signal)
    {
        var finished = new ManualResetEventSlim();
        var waiting = Task.Run(() => ConsoleCloseHold.Hold(signal, finished, TimeSpan.FromSeconds(30), onWindows: true));

        await Task.Delay(200);
        Assert.False(waiting.IsCompleted, "the handler returned before the shutdown finished");
        finished.Set();

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Hold_OnWindows_ReturnsWhenTheHoldRunsOut()
    {
        var finished = new ManualResetEventSlim(); // never set: a restore that hangs must not hold the handler forever
        var clock = Stopwatch.StartNew();

        bool held = ConsoleCloseHold.Hold(PosixSignal.SIGHUP, finished, TimeSpan.FromMilliseconds(150), onWindows: true);

        Assert.True(held);
        Assert.InRange(clock.ElapsedMilliseconds, 100, 5000);
    }

    [Fact]
    public void Hold_OnWindows_ReturnsAtOnceWhenTheShutdownIsAlreadyDone()
    {
        var finished = new ManualResetEventSlim(initialState: true);

        Assert.True(ConsoleCloseHold.Hold(PosixSignal.SIGHUP, finished, TimeSpan.FromSeconds(30), onWindows: true));
    }

    [Fact]
    public void Hold_OffWindows_NeverWaits()
    {
        var finished = new ManualResetEventSlim(); // never set: a wait would run into the timeout below

        Assert.False(ConsoleCloseHold.Hold(PosixSignal.SIGHUP, finished, TimeSpan.FromSeconds(30), onWindows: false));
    }

    [Theory]
    [InlineData(PosixSignal.SIGINT)]
    [InlineData(PosixSignal.SIGQUIT)]
    public void Hold_ForSignalsThatDoNotEndTheProcess_NeverWaits(PosixSignal signal)
    {
        var finished = new ManualResetEventSlim();

        Assert.False(ConsoleCloseHold.Hold(signal, finished, TimeSpan.FromSeconds(30), onWindows: true));
    }

    [Fact]
    public void MaxHold_StaysBelowWindowsLimitOfFiveSeconds() =>
        Assert.True(ConsoleCloseHold.MaxHold < TimeSpan.FromSeconds(5));
}
