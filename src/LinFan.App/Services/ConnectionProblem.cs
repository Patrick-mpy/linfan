// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Services;

/// <summary>Why a <see cref="MonitorSnapshot"/> carries no live data from the daemon.</summary>
public enum ConnectionProblem
{
    /// <summary>Connected - there is no problem.</summary>
    None,

    /// <summary>The first connection attempt has no outcome yet; not a failure.</summary>
    Connecting,

    /// <summary>No daemon answered at any endpoint (not running, or the connection dropped).</summary>
    Unreachable,

    /// <summary>The daemon's endpoint exists but this user may not open it (Linux: not in the <c>linfan</c> group yet).</summary>
    AccessDenied,

    /// <summary>
    /// Connected, but the daemon's snapshots cannot be read by this GUI - most likely another version. The connection
    /// stays; the next readable snapshot ends it. Locked like offline: there is nothing current to show or act on.
    /// </summary>
    UnreadableData,
}
