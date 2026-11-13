// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Onboarding-Kalibrierung eines Lüfters: rampt den PWM-Rohwert in Schritten hoch, misst nach
/// jeder Stufe die Drehzahl und bestimmt daraus Anlaufpunkt und Drehzahlbereich.
/// <para>
/// Während der Rampe läuft ein Temperatur-Watchdog; bei Übertemperatur wird abgebrochen und der
/// Ausgangszustand wiederhergestellt (keine Rampe ohne aktiven Watchdog).
/// </para>
/// </summary>
public sealed class CalibrationService
{
    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Wartefunktion (injizierbar für Tests); Standard ist <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public CalibrationService(
        ISensorBackend sensors,
        IFanController fans,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sensors = sensors;
        _fans = fans;
        _delay = delay ?? Task.Delay;
    }

    public async Task<FanCalibration> CalibrateAsync(
        FanId fanId, CalibrationOptions options, CancellationToken ct = default,
        IProgress<CalibrationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var fan = _fans.DiscoverFans().FirstOrDefault(f => f.Id == fanId)
            ?? throw new KeyNotFoundException($"Unbekannter Lüfter: {fanId}");
        if (!_fans.CanControl(fanId))
            throw new FanNotControllableException($"Lüfter {fanId} ist nicht steuerbar (Root nötig).");
        SensorId tach = MeasuredWith(fan, options.Couplings);

        int step = Math.Clamp(options.StepSize, 1, 255);
        var samples = new List<CalibrationSample>();
        int blind = 0;

        try
        {
            ct.ThrowIfCancellationRequested();
            blind = Guard(options.FailSafeTempC, blind); // check before the first write, as before every step
            _fans.SetMode(fanId, FanMode.Manual);
            _fans.SetPwm(fanId, 0);
            blind = await RunOutAsync(tach, options, blind, ct).ConfigureAwait(false);
            for (int pwm = 0; pwm <= 255; pwm += step)
            {
                ct.ThrowIfCancellationRequested();
                blind = Guard(options.FailSafeTempC, blind);

                byte value = (byte)Math.Min(pwm, 255);
                _fans.SetPwm(fanId, value);
                await _delay(options.SettleTime, ct).ConfigureAwait(false);

                blind = Guard(options.FailSafeTempC, blind);
                int rpm = ReadRpm(tach);
                samples.Add(new CalibrationSample(value, rpm));
                progress?.Report(new CalibrationProgress(value, rpm));
            }
        }
        finally
        {
            _fans.RestoreDefaults(); // Fail-Safe: nie im Manual-Zustand zurücklassen
        }

        return Build(samples, tach);
    }

    /// <summary>
    /// The speed sensor to measure with (<see cref="FanTachometers"/>), detected right now - one sensor, one fan: a
    /// pairing another fan is coupled to would measure that fan, and a sensor that is gone would only read 0.
    /// </summary>
    private SensorId MeasuredWith(FanDescriptor fan, IReadOnlyList<FanConfig> couplings)
    {
        if (new FanTachometers(couplings).Of(fan) is not { } tach)
            throw new NoTachometerException(fan.Tachometer is { } paired
                ? $"Lüfter {fan.Id}: {paired.Value} ist einem anderen Lüfter zugeordnet - kein eigenes Tachosignal, Kalibrierung nicht möglich."
                : $"Lüfter {fan.Id} hat kein Tachosignal - Kalibrierung nicht möglich.");
        if (!_sensors.DiscoverSensors().Any(s => s.Kind == SensorKind.FanRpm && s.Id == tach))
            throw new NoTachometerException(
                $"Drehzahl-Sensor {tach.Value} von Lüfter {fan.Id} ist nicht erkannt - Kalibrierung nicht möglich.");
        return tach;
    }

    /// <summary>Watchdog and cancellation interval while the fan runs out (like the tach coupling's settle windows).</summary>
    private static readonly TimeSpan RunOutSlice = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Lets a fan still running from its hardware-auto speed stop at PWM 0 before the first sample, for at most
    /// <see cref="CalibrationOptions.RunOutTime"/> of waiting (reads come on top), checking the watchdog and the
    /// cancellation every slice. Otherwise the ramp could catch it still turning at a PWM it holds but would not start
    /// from, and it would read as starting at 0. A fan still turning after that is held by the hardware (a floor).
    /// Returns the blind counter.
    /// </summary>
    private async Task<int> RunOutAsync(SensorId tach, CalibrationOptions options, int blind, CancellationToken ct)
    {
        TimeSpan waited = TimeSpan.Zero;
        while (waited < options.RunOutTime && ReadRpm(tach) >= SpinThresholdRpm)
        {
            ct.ThrowIfCancellationRequested();
            blind = Guard(options.FailSafeTempC, blind);
            await _delay(RunOutSlice, ct).ConfigureAwait(false);
            waited += RunOutSlice;
        }
        return blind;
    }

    /// <summary>So viele Prüfpunkte ohne lesbare Temperatur brechen die Rampe ab (kein Watchdog möglich).</summary>
    private const int MaxBlindGuards = 4;

    /// <summary>
    /// Temperatur-Watchdog für die Rampe: Übertemperatur → sofort abbrechen; keine lesbare Temperatur
    /// → nach einigen Prüfpunkten abbrechen (eine Rampe ohne Watchdog ist nicht zulässig).
    /// Gibt den fortgeschriebenen Blind-Zähler zurück.
    /// </summary>
    private int Guard(double limitC, int blindGuards)
    {
        double hottest = SensorAggregator.Hottest(_sensors);
        if (!double.IsNaN(hottest) && hottest >= limitC)
            throw new OverTemperatureException(hottest, limitC);

        if (double.IsNaN(hottest))
        {
            if (blindGuards + 1 >= MaxBlindGuards)
                throw new NoTemperatureReadingException(
                    "Keine lesbare Temperatur während der Kalibrierung - abgebrochen (kein Watchdog).");
            return blindGuards + 1;
        }
        return 0;
    }

    private int ReadRpm(SensorId tach)
    {
        double v = _sensors.ReadValue(tach);
        return double.IsNaN(v) ? 0 : (int)Math.Round(v);
    }

    /// <summary>From this speed on a fan counts as turning - for the start point and wherever a series is read.</summary>
    public const int SpinThresholdRpm = 100;

    /// <summary>
    /// The samples that are the fan's answer to the PWM: from the first standing one on (below
    /// <see cref="SpinThresholdRpm"/>). A fan the run-out phase could not stop in time still reads as spinning at the
    /// start of the ramp; the samples before the first standing one are that run-out. A fan that never stands (a
    /// hardware floor) answers from the first sample on. Returns <paramref name="samples"/> itself when nothing is left
    /// out.
    /// </summary>
    public static IReadOnlyList<CalibrationSample> DrivenSamples(IReadOnlyList<CalibrationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Rpm < SpinThresholdRpm)
                return i == 0 ? samples : samples.Skip(i).ToList();
        }
        return samples;
    }

    /// <summary>
    /// The start point is the first spinning sample of the <see cref="DrivenSamples"/>; the run-out before them counts
    /// for neither the start point nor the speed range. A reading of 0 counts as standing, so a tach dropout can only
    /// move the start point up - louder, never below where the fan starts.
    /// </summary>
    private static FanCalibration Build(List<CalibrationSample> samples, SensorId tach)
    {
        IReadOnlyList<CalibrationSample> driven = DrivenSamples(samples);
        // The ramp runs upwards, so the first spinning sample is the lowest PWM that starts the fan.
        var spinning = driven.Where(s => s.Rpm >= SpinThresholdRpm).ToList();

        return new FanCalibration
        {
            StartPwm = spinning.Count > 0 ? spinning[0].Pwm : FanCalibration.NoSafeStart,
            MinRpm = spinning.Count > 0 ? spinning.Min(s => s.Rpm) : 0,
            MaxRpm = driven.Count > 0 ? driven.Max(s => s.Rpm) : 0,
            Samples = samples,
            Tachometer = tach.Value,
        };
    }
}
