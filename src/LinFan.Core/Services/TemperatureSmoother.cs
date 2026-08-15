// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// Smooths a curve's input temperature over a sliding time window (arithmetic mean of the samples
/// still inside the window). Stateful, one buffer per curve id.
/// <para>
/// Why this exists: some CPUs - AMD's Tctl in particular - report short, steep spikes that come from
/// the boost algorithm, not from the heatsink. A spike blows straight through any sensible hysteresis
/// deadband (a deadband filters <i>amplitude</i>, the problem is <i>duration</i>), so the fans surge
/// and drop back within seconds. Averaging attenuates a single spike by 1/N while sustained load still
/// arrives in full, only delayed by roughly half the window - harmless next to a heatsink's time
/// constant of tens of seconds.
/// </para>
/// <para>
/// Deliberately <b>not</b> an exponential average: an EMA weights the newest sample most and would let
/// far more of the spike through at a comparable window length.
/// </para>
/// <para>
/// Fail-safe: this never touches the over-temperature watchdog, which keeps reading raw values. A
/// <see cref="double.NaN"/> input is passed through untouched and never enters a buffer - otherwise a
/// single unreadable tick would poison every later mean, and a dead sensor could keep the fans running
/// on stale buffer contents. The window is bounded by <see cref="MaxWindowSeconds"/> here as well as in
/// the config, so no caller can make a fan regulate on minutes-old readings.
/// </para>
/// <para>
/// Known and deliberately accepted: the clock is monotonic and does not advance across a system suspend,
/// so readings from before an S3 cycle can survive it. The effect is bounded by the window and the
/// watchdog reads raw, which is why this is not worth a second, jump-prone wall-clock source.
/// </para>
/// </summary>
public sealed class TemperatureSmoother
{
    /// <summary>
    /// Upper bound for the window. Kept well below the point where lag becomes dangerous: with curves
    /// reaching full power in the eighties and the watchdog at 90 °C, a long window would let a fast ramp
    /// hit the fail-safe while the smoothed input still looks idle - turning "calmer fans" into an abort.
    /// Absorbing a spike takes seconds, not half a minute.
    /// </summary>
    public const double MaxWindowSeconds = 15.0;

    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<Sample>> _buffers = new(StringComparer.Ordinal);

    /// <param name="time">
    /// Time source, defaults to <see cref="TimeProvider.System"/>. Samples age out by elapsed time
    /// rather than by count, so a stalled or slowed poll loop cannot average across a long gap.
    /// </param>
    public TemperatureSmoother(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// Feeds one reading and returns the mean over <paramref name="windowSeconds"/>, which is clamped to
    /// <see cref="MaxWindowSeconds"/>. A window of zero (or less, or NaN) is a pass-through: the raw value
    /// is returned and no buffer is touched.
    /// </summary>
    public double Smooth(string curveId, double temperatureC, double windowSeconds)
    {
        ArgumentNullException.ThrowIfNull(curveId);

        if (double.IsNaN(temperatureC))
            return temperatureC;

        // Bound the window here, not only in ConfigSanitizer: a curve stored inside a profile reaches the
        // control loop without passing the sanitizer (ProfileService.Apply swaps the curve list in after
        // sanitizing), so a hand-edited or imported value arrives raw. Unbounded it would regulate a fan on
        // minutes-old readings, and past ~9.2e11 s TimeSpan.FromSeconds throws - which the per-fan catch in
        // ControlLoop would turn into a "Failed" action every tick, leaving the fan stuck on its last PWM.
        double window = Math.Clamp(windowSeconds, 0, MaxWindowSeconds);
        if (!(window > 0))
            return temperatureC;

        if (!_buffers.TryGetValue(curveId, out Queue<Sample>? buffer))
            _buffers[curveId] = buffer = new Queue<Sample>();

        long now = _time.GetTimestamp();
        buffer.Enqueue(new Sample(now, temperatureC));

        // Drop everything older than the window. A gap longer than the window empties the buffer by
        // itself, so the first reading after a pause is the raw value again - no averaging across it.
        var maxAge = TimeSpan.FromSeconds(window);
        while (buffer.Count > 1 && _time.GetElapsedTime(buffer.Peek().Timestamp, now) > maxAge)
            buffer.Dequeue();

        double sum = 0;
        foreach (Sample s in buffer)
            sum += s.Value;
        return sum / buffer.Count;
    }

    /// <summary>
    /// Discards all buffered samples. Call after a configuration change - otherwise a freshly edited
    /// curve would be fed means built from the old one.
    /// </summary>
    public void Reset() => _buffers.Clear();

    private readonly record struct Sample(long Timestamp, double Value);
}
