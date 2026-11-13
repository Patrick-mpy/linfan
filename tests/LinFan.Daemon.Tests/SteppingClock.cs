// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Daemon.Tests;

/// <summary>
/// A time source that moves on by <paramref name="step"/> each time it is read - for a check that reads it once per tick,
/// one step per tick, independent of how fast the test's ticks really run.
/// </summary>
internal sealed class SteppingClock(TimeSpan step) : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => 1_000_000;

    public override long GetTimestamp() =>
        Interlocked.Add(ref _timestamp, (long)(step.TotalSeconds * TimestampFrequency));
}
