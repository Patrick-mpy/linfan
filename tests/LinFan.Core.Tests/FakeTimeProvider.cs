// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Tests;

/// <summary>
/// Hand-advanced time source for the smoothing tests. Keeps them free of wall-clock timing - and saves
/// pulling in Microsoft.Extensions.TimeProvider.Testing for a dozen lines.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private long _timestamp;

    /// <summary>One tick = one microsecond; large enough for sub-second steps, small enough not to overflow.</summary>
    public override long TimestampFrequency => 1_000_000;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by) => _timestamp += (long)(by.TotalSeconds * TimestampFrequency);

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
