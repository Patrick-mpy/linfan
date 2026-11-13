// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// <see cref="CalibrationPlausibility"/> reports a calibrated fan whose speed lastingly does not fit its calibration -
/// only from steady readings while the curve or the user drives it, and never from a reading the calibration cannot
/// speak to. One reading per second, like a tick.
/// </summary>
public class CalibrationPlausibilityTests
{
    private const string FanId = "hwmon7/pwm1";
    private const string Tach = "hwmon7/fan1";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Stands up to 32, starts at 64, then 200 RPM more per 32 PWM; 255 is never sampled.</summary>
    private static FanCalibration Calibration(string? tachometer = Tach) => new()
    {
        StartPwm = 64,
        MinRpm = 800,
        MaxRpm = 1800,
        Tachometer = tachometer,
        Samples =
        [
            new(0, 0), new(32, 0), new(64, 800), new(96, 1000), new(128, 1200), new(160, 1400), new(192, 1600),
            new(224, 1800),
        ],
    };

    private sealed class Rig
    {
        public FakeTimeProvider Clock { get; } = new();
        public CalibrationPlausibility Check { get; }

        public Rig() => Check = new CalibrationPlausibility(Interval, time: Clock);

        /// <summary>One tick's reading of the fan, then a second passes.</summary>
        public CalibrationMismatch? Tick(
            int pwm, double? rpm, FanControlState state = FanControlState.Curve, FanCalibration? calibration = null,
            string? tachometer = Tach, bool modeKnown = true, bool dryRun = false, bool failSafe = false,
            bool uncalibrated = false)
        {
            var fan = new FanObservation(
                FanId, state, pwm, modeKnown, rpm, tachometer, uncalibrated ? null : calibration ?? Calibration());
            IReadOnlyDictionary<string, CalibrationMismatch> found = Check.Check([fan], dryRun, failSafe);
            Clock.Advance(Interval);
            return found.TryGetValue(FanId, out CalibrationMismatch m) ? m : null;
        }

        /// <summary>Ticks <paramref name="seconds"/> times and returns every result in order.</summary>
        public List<CalibrationMismatch?> Run(
            int seconds, int pwm, double? rpm, FanControlState state = FanControlState.Curve,
            FanCalibration? calibration = null, string? tachometer = Tach) =>
            Enumerable.Range(0, seconds).Select(_ => Tick(pwm, rpm, state, calibration, tachometer)).ToList();
    }

    // --- Steady state and the minute ------------------------------------------------------------------------------

    [Fact]
    public void ASteadySlowFan_IsReported_AfterSettlingAndAMinuteOfMismatch()
    {
        var rig = new Rig();

        // PWM 128 should turn at 1200; the readings at 0..9 s settle, 10..69 s mismatch.
        List<CalibrationMismatch?> results = rig.Run(70, pwm: 128, rpm: 600);

        Assert.All(results.Take(70), r => Assert.Null(r));
        Assert.Equal(CalibrationMismatch.Slower, rig.Tick(128, 600));
    }

    [Fact]
    public void AChangingPwm_IsNeverSteady_SoNeverReported()
    {
        var rig = new Rig();

        for (int i = 0; i < 300; i++)
            Assert.Null(rig.Tick(i % 2 == 0 ? 128 : 132, 600));
    }

    [Fact]
    public void APwmWithinThreeOfWhereItSettled_StaysSteady()
    {
        var rig = new Rig();

        for (int i = 0; i < 70; i++)
            Assert.Null(rig.Tick(i % 2 == 0 ? 128 : 131, 600));

        Assert.Equal(CalibrationMismatch.Slower, rig.Tick(125, 600));
    }

    [Fact]
    public void OnePlausibleSteadyReading_StartsTheMinuteOver()
    {
        var rig = new Rig();
        rig.Run(50, pwm: 128, rpm: 600);   // 0..49 s: settled at 10 s, mismatching since
        Assert.Null(rig.Tick(128, 1200));  // 50 s: one plausible reading

        List<CalibrationMismatch?> results = rig.Run(61, pwm: 128, rpm: 600); // 51..111 s

        Assert.All(results.Take(60), r => Assert.Null(r));
        Assert.Equal(CalibrationMismatch.Slower, results[^1]); // 111 s: a minute after the mismatch came back
    }

    [Fact]
    public void AGapOfMoreThanTwoIntervals_StartsTheRunOver()
    {
        var rig = new Rig();
        rig.Run(60, pwm: 128, rpm: 600);   // 0..59 s
        rig.Clock.Advance(TimeSpan.FromSeconds(1.5)); // the next reading comes 2.5 s after the last

        List<CalibrationMismatch?> results = rig.Run(70, pwm: 128, rpm: 600);

        Assert.All(results, r => Assert.Null(r)); // settling again, then a fresh minute
        Assert.Equal(CalibrationMismatch.Slower, rig.Tick(128, 600));
    }

    [Fact]
    public void AGapOfTwoIntervals_KeepsTheRun()
    {
        var rig = new Rig();
        rig.Run(60, pwm: 128, rpm: 600);   // 0..59 s
        rig.Clock.Advance(Interval);        // one tick without a reading

        List<CalibrationMismatch?> results = rig.Run(10, pwm: 128, rpm: 600); // 61..70 s

        Assert.Equal(CalibrationMismatch.Slower, results[^1]);
    }

    [Fact]
    public void ReadingsThatDoNotCount_PauseTheRun_AndALongPauseStartsItOver()
    {
        var rig = new Rig();
        rig.Run(40, pwm: 128, rpm: 600);
        for (int i = 0; i < 5; i++)
            Assert.Null(rig.Tick(128, 600, dryRun: true));

        List<CalibrationMismatch?> results = rig.Run(70, pwm: 128, rpm: 600);

        Assert.All(results, r => Assert.Null(r));
        Assert.Equal(CalibrationMismatch.Slower, rig.Tick(128, 600));
    }

    // --- What is not judged -------------------------------------------------------------------------------------------

    public static TheoryData<string> NotJudged() =>
    [
        "dryRun", "failSafe", "modeUnknown", "timedOut", "noRpm", "nanRpm", "infiniteRpm", "negativeRpm", "noCalibration",
        "emptySeries", "oneTurningSample", "seriesOutOfOrder", "duplicatePwm",
    ];

    [Theory]
    [MemberData(nameof(NotJudged))]
    public void AReadingTheCalibrationCannotSpeakTo_IsNeverJudged(string reading)
    {
        var rig = new Rig();
        FanCalibration oneTurning = Calibration() with { Samples = [new(0, 0), new(32, 0), new(64, 800)] };
        FanCalibration outOfOrder = Calibration() with { Samples = [new(0, 0), new(128, 1200), new(96, 1000)] };
        // Two speeds at one PWM; read at 128, past them, the last segment would have no width.
        FanCalibration duplicatePwm = Calibration() with { Samples = [new(0, 0), new(32, 0), new(64, 800), new(96, 1000), new(96, 1100)] };

        for (int i = 0; i < 200; i++)
        {
            CalibrationMismatch? result = reading switch
            {
                "dryRun" => rig.Tick(128, 600, dryRun: true),
                "failSafe" => rig.Tick(128, 600, failSafe: true),
                "modeUnknown" => rig.Tick(128, 600, modeKnown: false),
                "timedOut" => rig.Tick(0, 0, modeKnown: false), // the snapshot's PWM 0 after a gate timeout
                "noRpm" => rig.Tick(128, null),
                "nanRpm" => rig.Tick(128, double.NaN),
                "infiniteRpm" => rig.Tick(128, double.PositiveInfinity),
                "negativeRpm" => rig.Tick(128, -1),
                "noCalibration" => rig.Tick(128, 600, uncalibrated: true),
                "emptySeries" => rig.Tick(128, 600, calibration: Calibration() with { Samples = [] }),
                "oneTurningSample" => rig.Tick(128, 0, calibration: oneTurning),
                "seriesOutOfOrder" => rig.Tick(128, 600, calibration: outOfOrder),
                "duplicatePwm" => rig.Tick(128, 1100, calibration: duplicatePwm),
                _ => throw new ArgumentOutOfRangeException(nameof(reading)),
            };
            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData(FanControlState.HardwareAuto)]
    [InlineData(FanControlState.Calibrating)]
    [InlineData(FanControlState.Identifying)]
    [InlineData(FanControlState.IdentifyThrottled)]
    [InlineData(FanControlState.Coupling)]
    [InlineData(FanControlState.CouplingThrottled)]
    [InlineData(FanControlState.FailSafe)]
    [InlineData(FanControlState.Failed)]
    [InlineData(FanControlState.NotControllable)]
    public void AFanNeitherTheCurveNorTheUserDrives_IsNeverJudged(FanControlState state)
    {
        var rig = new Rig();

        Assert.All(rig.Run(200, pwm: 128, rpm: 600, state: state), r => Assert.Null(r));
    }

    [Theory]
    [InlineData(FanControlState.Curve)]
    [InlineData(FanControlState.CurveHolding)]
    [InlineData(FanControlState.Manual)]
    public void AFanTheCurveOrTheUserDrives_IsJudged(FanControlState state)
    {
        var rig = new Rig();

        Assert.Equal(CalibrationMismatch.Slower, rig.Run(71, pwm: 128, rpm: 600, state: state)[^1]);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(32)]
    [InlineData(0)]
    public void BelowTheFirstTurningSample_AStandingFanIsNoFinding(int pwm)
    {
        // Between the last standing and the first turning sample a fan stands or turns depending on where it came from.
        var rig = new Rig();

        Assert.All(rig.Run(200, pwm, rpm: 0), r => Assert.Null(r));
    }

    [Fact]
    public void AtTheStartPoint_AFanThatStands_Stalls()
    {
        // A replaced fan held at the old fan's start point does not start there.
        var rig = new Rig();

        List<CalibrationMismatch?> results = rig.Run(71, pwm: 64, rpm: 0);

        Assert.All(results.Take(70), r => Assert.Null(r));
        Assert.Equal(CalibrationMismatch.Stalls, results[^1]);
    }

    [Fact]
    public void Stalls_NeedsNoMinimumDifference()
    {
        // The calibration saw only 200 RPM at the start: standing is 150 RPM off, below the 300 RPM floor of Slower.
        FanCalibration slowStart = Calibration() with
        {
            Samples = [new(0, 0), new(32, 0), new(64, 200), new(96, 500), new(128, 800)],
        };
        var rig = new Rig();

        Assert.Equal(CalibrationMismatch.Stalls, rig.Run(71, pwm: 64, rpm: 50, calibration: slowStart)[^1]);
    }

    // --- Expected speed and tolerances --------------------------------------------------------------------------------

    [Theory]
    [InlineData(128, 841, null)]                          // 1200 expected: ratio 0.701
    [InlineData(128, 839, CalibrationMismatch.Slower)]    // ratio 0.699
    [InlineData(128, 1559, null)]                         // ratio 1.299
    [InlineData(128, 1561, CalibrationMismatch.Faster)]   // ratio 1.301
    [InlineData(144, 1300, null)]                         // between two samples: 1300 expected
    [InlineData(144, 1300 * 0.69, CalibrationMismatch.Slower)]
    [InlineData(64, 550, null)]                           // 800 expected: ratio 0.69, but only 250 RPM off
    [InlineData(64, 490, CalibrationMismatch.Slower)]     // 310 RPM off
    [InlineData(64, 1090, null)]                          // ratio 1.36, but only 290 RPM off
    [InlineData(64, 1110, CalibrationMismatch.Faster)]    // 310 RPM off
    public void ASteadyReading_IsJudgedAgainstTheInterpolatedSpeed(int pwm, double rpm, CalibrationMismatch? expected)
    {
        var rig = new Rig();

        Assert.Equal(expected, rig.Run(71, pwm, rpm)[^1]);
    }

    [Fact]
    public void AboveTheLastSample_TheLastSegmentGoesOn()
    {
        // 255 lies past the last sample (224: 1800, 200 RPM per 32 PWM): about 1994 RPM expected. 1270 is within 0.7 of
        // the last sample's 1800 but not of that.
        var rig = new Rig();

        Assert.Equal(CalibrationMismatch.Slower, rig.Run(71, pwm: 255, rpm: 1270)[^1]);
        Assert.Null(new Rig().Run(71, pwm: 255, rpm: 1990)[^1]);
    }

    [Fact]
    public void AboveTheLastSample_TheSpeedIsAtLeastTheLastSamples()
    {
        // A falling last segment (1900 → 1800) would give about 1703 at 255; the last sample's 1800 is the floor.
        FanCalibration falling = Calibration() with
        {
            Samples = [new(0, 0), new(32, 0), new(64, 800), new(192, 1900), new(224, 1800)],
        };
        var rig = new Rig();

        Assert.Equal(CalibrationMismatch.Slower, rig.Run(71, pwm: 255, rpm: 1250, calibration: falling)[^1]);
    }

    [Fact]
    public void RunOutSamples_AreNotPartOfTheCurve()
    {
        // Still coasting at 0 and 32 from the auto speed, standing at 64, starting at 96.
        FanCalibration coasting = Calibration() with
        {
            Samples = [new(0, 1500), new(32, 900), new(64, 0), new(96, 1000), new(128, 1200)],
        };
        var rig = new Rig();

        Assert.All(rig.Run(200, pwm: 40, rpm: 0, calibration: coasting), r => Assert.Null(r));
    }

    [Fact]
    public void ATachDropoutAboveTheStart_IsNotPartOfTheCurve()
    {
        // The reading at 128 dropped to 0 during the calibration; between 96 and 160 the fan turns at about 1200.
        FanCalibration dropout = Calibration() with
        {
            Samples = [new(0, 0), new(32, 0), new(64, 800), new(96, 1000), new(128, 0), new(160, 1400)],
        };
        var rig = new Rig();

        Assert.All(rig.Run(200, pwm: 128, rpm: 1200, calibration: dropout), r => Assert.Null(r));
    }

    // --- Another speed sensor -----------------------------------------------------------------------------------------

    [Fact]
    public void ACalibrationMeasuredWithAnotherSensor_IsReportedAtOnce()
    {
        var rig = new Rig();

        Assert.Equal(CalibrationMismatch.OtherSensor, rig.Tick(128, 1200, tachometer: "hwmon7/fan3"));
        // It needs no reading: whoever drives the fan, it holds.
        Assert.Equal(CalibrationMismatch.OtherSensor, rig.Tick(128, null, FanControlState.HardwareAuto, tachometer: "hwmon7/fan3", dryRun: true));
    }

    [Fact]
    public void ACalibrationThatRecordedNoSensor_IsComparedAnyway()
    {
        var rig = new Rig();

        Assert.Equal(CalibrationMismatch.Slower,
            rig.Run(71, pwm: 128, rpm: 600, calibration: Calibration(tachometer: null), tachometer: "hwmon7/fan3")[^1]);
    }

    [Fact]
    public void AFanWithoutASensorNow_IsNoOtherSensorFinding()
    {
        var rig = new Rig();

        Assert.All(rig.Run(200, pwm: 128, rpm: null, tachometer: null), r => Assert.Null(r));
    }

    // --- The latch ----------------------------------------------------------------------------------------------------

    private static Rig Reported()
    {
        var rig = new Rig();
        Assert.Equal(CalibrationMismatch.Slower, rig.Run(71, pwm: 128, rpm: 600)[^1]);
        return rig;
    }

    [Fact]
    public void AReportedMismatch_Stays_ThroughPlausibleReadingsAndOtherStates()
    {
        Rig rig = Reported();

        Assert.All(rig.Run(100, pwm: 128, rpm: 1200), r => Assert.Equal(CalibrationMismatch.Slower, r));
        Assert.All(rig.Run(10, pwm: 200, rpm: 600, state: FanControlState.HardwareAuto),
            r => Assert.Equal(CalibrationMismatch.Slower, r));
        Assert.Equal(CalibrationMismatch.Slower, rig.Tick(128, 1200, failSafe: true));
    }

    [Fact]
    public void AnEqualCalibrationFromAReload_KeepsTheFinding()
    {
        Rig rig = Reported();
        FanCalibration reloaded = Calibration() with { Samples = Calibration().Samples.ToArray() };

        Assert.Equal(CalibrationMismatch.Slower, rig.Tick(128, 1200, calibration: reloaded));
    }

    [Fact]
    public void AChangedCalibration_ClearsTheFinding_AndStartsOver()
    {
        Rig rig = Reported();
        FanCalibration recalibrated = Calibration() with
        {
            Samples = [new(0, 0), new(32, 0), new(64, 800), new(96, 1000), new(128, 1250), new(160, 1400)],
        };

        List<CalibrationMismatch?> results = rig.Run(71, pwm: 128, rpm: 600, calibration: recalibrated);

        Assert.All(results.Take(70), r => Assert.Null(r));
        Assert.Equal(CalibrationMismatch.Slower, results[^1]);
    }

    [Fact]
    public void AnotherSpeedSensor_ClearsTheFinding()
    {
        var rig = new Rig();
        FanCalibration unrecorded = Calibration(tachometer: null);
        Assert.Equal(CalibrationMismatch.Slower, rig.Run(71, pwm: 128, rpm: 600, calibration: unrecorded)[^1]);

        Assert.Null(rig.Tick(128, 1200, calibration: unrecorded, tachometer: "hwmon7/fan3"));
    }

    [Fact]
    public void TheFindingsAreKeptPerFan()
    {
        var clock = new FakeTimeProvider();
        var check = new CalibrationPlausibility(Interval, time: clock);
        IReadOnlyDictionary<string, CalibrationMismatch> found = new Dictionary<string, CalibrationMismatch>();
        for (int i = 0; i < 71; i++)
        {
            found = check.Check(
            [
                new FanObservation("a", FanControlState.Curve, 128, true, 600, Tach, Calibration()),
                new FanObservation("b", FanControlState.Curve, 128, true, 1200, "hwmon7/fan2", Calibration("hwmon7/fan2")),
            ], dryRun: false, failSafe: false);
            clock.Advance(Interval);
        }

        Assert.Equal(CalibrationMismatch.Slower, Assert.Single(found, f => f.Key == "a").Value);
        Assert.DoesNotContain("b", found.Keys);
    }

    [Fact]
    public void ThePollInterval_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalibrationPlausibility(TimeSpan.Zero));
    }
}
