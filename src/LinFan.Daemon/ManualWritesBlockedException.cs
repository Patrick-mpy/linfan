// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Daemon;

/// <summary>
/// A write the <see cref="IManualWriteLatch"/> turned away: the daemon is shutting down. Expected for a coordinator
/// still running then, so it is logged without a trace rather than as a failure.
/// </summary>
internal sealed class ManualWritesBlockedException(string message) : InvalidOperationException(message);
