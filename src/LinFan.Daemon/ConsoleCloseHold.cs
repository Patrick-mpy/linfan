// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace LinFan.Daemon;

/// <summary>
/// Keeps a Windows console process alive while its shutdown finishes. Closing the console window (the X) raises
/// CTRL_CLOSE, which .NET reports as SIGHUP, and Windows ends the process the moment the handler returns - at the
/// latest 5 s after the event. A handler that only requests the stop returns at once, so the restore that hands the fans
/// back (and the removal of the restore marker) never completes: the fan stays on its manual value and the next daemon
/// run of this boot counts as untrusted. The handler therefore waits here until the shutdown is done.
/// </summary>
internal static class ConsoleCloseHold
{
    /// <summary>
    /// Below Windows' limit of 5 s (measured: a handler that returns after 4.5 s still completes, one that takes 9 s is
    /// cut off at 5 s). The restore itself takes milliseconds; a hold that runs out leaves the marker behind, which
    /// keeps the next run on the untrusted, 100 % path - the restore is never done from the handler thread.
    /// </summary>
    internal static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Blocks the signal handler's thread until <paramref name="finished"/> is set or <paramref name="maxHold"/> has
    /// passed - but only for the signals Windows ends the process for (hangup, i.e. CTRL_CLOSE; SIGTERM covers logoff and
    /// shutdown, which only the CLI registers - the daemon leaves SIGTERM to the host's own lifetime) and only on
    /// Windows: on Unix a handler may return at once, and holding it would delay nothing useful.
    /// </summary>
    /// <returns><c>true</c> when the handler had to wait for the shutdown (a test seam).</returns>
    internal static bool Hold(PosixSignal signal, ManualResetEventSlim finished, TimeSpan maxHold, bool onWindows)
    {
        if (!onWindows || signal is not (PosixSignal.SIGHUP or PosixSignal.SIGTERM))
            return false;
        finished.Wait(maxHold);
        return true;
    }

    /// <summary>The same with the real platform and <see cref="MaxHold"/>.</summary>
    internal static void Hold(PosixSignalContext context, ManualResetEventSlim finished) =>
        Hold(context.Signal, finished, MaxHold, OperatingSystem.IsWindows());
}
