// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>
/// A live monitor whose next connection attempt the user can bring forward ("Reconnect"). Kept apart from
/// <see cref="ILiveMonitor"/> so monitors without a connection (test fakes) need not implement it.
/// </summary>
public interface IReconnectable
{
    /// <summary>
    /// Ends the retry delay and starts the next connection attempt now. While an attempt is already running, the
    /// request just waits for it - repeated clicks start no extra attempts. The task completes once
    /// <see cref="ILiveMonitor.Read"/> reflects the attempt's outcome and tells whether it connected: right away
    /// <c>true</c> if already connected, <c>false</c> once the monitor has stopped.
    /// </summary>
    Task<bool> RequestReconnectAsync();
}
