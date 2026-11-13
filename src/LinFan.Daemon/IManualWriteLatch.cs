// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Daemon;

/// <summary>
/// One-way switch on the daemon's fan controller for shutdown: once engaged, only writes towards the safe
/// state (hardware auto) reach the hardware. Opt-in by type test, like <c>IRestoreDiagnostics</c>, so test fakes
/// need not implement it.
/// </summary>
internal interface IManualWriteLatch
{
    /// <summary>Rejects every later write that would put a fan into manual mode. Cannot be undone.</summary>
    void BlockManualWrites();
}
