// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Tests;

/// <summary>
/// A clock that only moves when the test says so - for the undo history's coalescing window and the status toast's
/// auto-hide. Timers (and with them <c>Task.Delay(delay, clock, token)</c>) fire inside <see cref="Advance"/>, on the
/// test's thread.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    // The monotonic clock moves with Advance as well - code that measures elapsed time reads this one.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target = _now + by;
        while (_timers.Where(t => t.DueAt <= target).MinBy(t => t.DueAt) is { } next)
        {
            _now = next.DueAt!.Value;
            next.Fire();
        }
        _now = target;
    }

    public void AdvanceMs(int milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _clock._now + dueTime;
            _period = period;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : DueAt + _period;
            _callback(_state);
        }

        public void Dispose()
        {
            DueAt = null;
            _clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
