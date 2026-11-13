// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Compares each calibrated fan's measured speed with the speed its stored calibration gives for the same PWM, and reports
/// a lasting mismatch (<see cref="CalibrationMismatch"/>). It is the only way to notice a case fan swapped on the same
/// header, whose device does not change, and it covers a replaced fan that stands at the old start point. A notice only:
/// it takes what a tick already read and writes nothing, so nothing here changes how a fan is driven.
/// <para>
/// A reading counts only while the curve or the user drives the fan at a steady PWM, at or above the PWM where the
/// calibration first saw it turn, outside a dry run and the fail-safe. A mismatch has to hold in such readings for
/// <see cref="MismatchTime"/>. Once reported it stays until the fan's calibration or speed sensor changes, or the process
/// ends, so it does not come and go with the PWM.
/// </para>
/// <para>
/// The tolerances are wide on purpose: fans differ from their calibration with dust, temperature and age, and a notice
/// that comes and goes would teach the user to ignore it.
/// </para>
/// Not thread-safe: one caller, once per tick.
/// </summary>
public sealed class CalibrationPlausibility
{
    /// <summary>How far the read-back PWM may move and still count as one steady setting.</summary>
    private const int PwmTolerance = 3;

    /// <summary>Measured / expected speed below this is <see cref="CalibrationMismatch.Slower"/>.</summary>
    private const double MinRatio = 0.7;

    /// <summary>Measured / expected speed above this is <see cref="CalibrationMismatch.Faster"/>.</summary>
    private const double MaxRatio = 1.3;

    /// <summary>
    /// A smaller difference is never <see cref="CalibrationMismatch.Slower"/> or <see cref="CalibrationMismatch.Faster"/>:
    /// near the start point a ratio is large for a few hundred RPM.
    /// </summary>
    private const double MinDeviationRpm = 300;

    /// <summary>How long the PWM has to hold before the speed counts - a fan takes a few seconds to settle.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    /// <summary>How long a mismatch has to hold in steady readings, without a plausible one between, to be reported.</summary>
    private static readonly TimeSpan MismatchTime = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _maxGap;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);

    /// <param name="pollInterval">The tick interval: readings more than two intervals apart are no continuous run.</param>
    /// <param name="time">Monotonic time source, defaults to <see cref="TimeProvider.System"/>.</param>
    public CalibrationPlausibility(TimeSpan pollInterval, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        _maxGap = 2 * pollInterval;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Takes one tick's readings of every fan and returns the fans whose calibration does not fit; a fan without a
    /// finding is absent. <paramref name="dryRun"/> and <paramref name="failSafe"/> pause the speed comparison: a dry run
    /// reports the curve's state without writing it, and the fail-safe hands the fans to the firmware.
    /// </summary>
    public IReadOnlyDictionary<string, CalibrationMismatch> Check(
        IReadOnlyList<FanObservation> fans, bool dryRun, bool failSafe)
    {
        ArgumentNullException.ThrowIfNull(fans);
        long now = _time.GetTimestamp();
        var found = new Dictionary<string, CalibrationMismatch>(StringComparer.Ordinal);
        foreach (FanObservation fan in fans)
        {
            if (Observe(fan, now, paused: dryRun || failSafe) is { } mismatch)
                found[fan.FanId] = mismatch;
        }
        return found;
    }

    private CalibrationMismatch? Observe(FanObservation fan, long now, bool paused)
    {
        if (!_tracks.TryGetValue(fan.FanId, out Track? track) || !track.Fits(fan.Calibration, fan.Tachometer))
            _tracks[fan.FanId] = track = new Track(fan.Calibration, fan.Tachometer);
        if (track.Reported is { } reported)
            return reported;

        // Needs no reading, so it holds whoever drives the fan. Without a sensor now there is nothing to compare with -
        // and nothing a new calibration could measure with either.
        if (fan.Calibration?.Tachometer is { } measuredWith && fan.Tachometer is { } readFrom
            && !string.Equals(measuredWith, readFrom, StringComparison.Ordinal))
            return track.Reported = CalibrationMismatch.OtherSensor;

        if (paused || !fan.ModeKnown || !Driven(fan.State)
            || fan.Rpm is not { } rpm || !double.IsFinite(rpm) || rpm < 0
            || track.ExpectedRpm(fan.Pwm) is not { } expected)
            return null;

        if (track.LastSeen is not { } last || _time.GetElapsedTime(last, now) > _maxGap
            || Math.Abs(fan.Pwm - track.SteadyPwm) > PwmTolerance)
            track.Restart(fan.Pwm, now);
        track.LastSeen = now;
        if (_time.GetElapsedTime(track.SteadySince, now) < SettleTime)
            return null;

        CalibrationMismatch? mismatch = Judge(rpm, expected);
        if (mismatch != track.Pending)
            (track.Pending, track.PendingSince) = (mismatch, now);
        return mismatch is not null && _time.GetElapsedTime(track.PendingSince, now) >= MismatchTime
            ? track.Reported = mismatch
            : null;
    }

    // Hardware auto, the device actions and the fail-safe set speeds the calibration says nothing about.
    private static bool Driven(FanControlState state) =>
        state is FanControlState.Curve or FanControlState.CurveHolding or FanControlState.Manual;

    /// <summary>
    /// What a steady reading says, <c>null</c> = plausible. <paramref name="expected"/> is never below the spin threshold
    /// (see <see cref="Track.ExpectedRpm"/>), so a fan below it stands where it should turn.
    /// </summary>
    private static CalibrationMismatch? Judge(double rpm, double expected)
    {
        if (rpm < CalibrationService.SpinThresholdRpm)
            return CalibrationMismatch.Stalls;
        if (Math.Abs(rpm - expected) < MinDeviationRpm)
            return null;
        double ratio = rpm / expected;
        return ratio < MinRatio ? CalibrationMismatch.Slower
            : ratio > MaxRatio ? CalibrationMismatch.Faster
            : null;
    }

    /// <summary>One fan's run of readings, for the calibration and speed sensor it started with.</summary>
    private sealed class Track
    {
        private readonly FanCalibration? _calibration;
        private readonly string? _tachometer;
        private readonly CalibrationSample[]? _curve;

        public Track(FanCalibration? calibration, string? tachometer)
        {
            _calibration = calibration;
            _tachometer = tachometer;
            _curve = calibration is null ? null : CurveOf(calibration.Samples);
        }

        public CalibrationMismatch? Reported { get; set; }
        public long? LastSeen { get; set; }
        public int SteadyPwm { get; private set; }
        public long SteadySince { get; private set; }
        public CalibrationMismatch? Pending { get; set; }
        public long PendingSince { get; set; }

        /// <summary>A new steady setting starts: nothing measured before it counts.</summary>
        public void Restart(int pwm, long now)
        {
            (SteadyPwm, SteadySince) = (pwm, now);
            Pending = null;
        }

        public bool Fits(FanCalibration? calibration, string? tachometer) =>
            string.Equals(tachometer, _tachometer, StringComparison.Ordinal) && SameValue(calibration, _calibration);

        // A reload or an import makes new objects of an unchanged calibration, and record equality compares the series by
        // reference - so the series by its samples, everything else by the record.
        private static bool SameValue(FanCalibration? a, FanCalibration? b) =>
            ReferenceEquals(a, b)
            || (a is not null && b is not null && a with { Samples = b.Samples } == b && a.Samples.SequenceEqual(b.Samples));

        /// <summary>
        /// The points the expected speed is read from: the driven samples that turn. A standing one after the start is a
        /// tach dropout, as the calibration itself treats it. <c>null</c> with fewer than two, or out of ramp order (a hand
        /// edit) - then there is no curve to compare with.
        /// </summary>
        private static CalibrationSample[]? CurveOf(IReadOnlyList<CalibrationSample> samples)
        {
            CalibrationSample[] points = CalibrationService.DrivenSamples(samples)
                .Where(s => s.Rpm >= CalibrationService.SpinThresholdRpm)
                .ToArray();
            if (points.Length < 2)
                return null;
            for (int i = 1; i < points.Length; i++)
            {
                if (points[i].Pwm <= points[i - 1].Pwm)
                    return null;
            }
            return points;
        }

        /// <summary>
        /// The speed the calibration gives for <paramref name="pwm"/>, interpolated linearly. <c>null</c> below the first
        /// turning sample: there a fan stands or turns depending on whether it was turning before. Past the last sample
        /// the last segment goes on, but never below the last sample's speed.
        /// </summary>
        public double? ExpectedRpm(int pwm)
        {
            if (_curve is not { } points || pwm < points[0].Pwm)
                return null;
            int i = 1;
            while (i < points.Length - 1 && pwm > points[i].Pwm)
                i++;
            (CalibrationSample lo, CalibrationSample hi) = (points[i - 1], points[i]);
            double rpm = lo.Rpm + ((double)(hi.Rpm - lo.Rpm) * (pwm - lo.Pwm) / (hi.Pwm - lo.Pwm));
            return pwm > hi.Pwm ? Math.Max(rpm, hi.Rpm) : rpm;
        }
    }
}
