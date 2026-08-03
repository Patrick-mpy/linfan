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
        // Das explizit zugeordnete RpmSource-Override gewinnt vor dem Backend-Guess (manuelle/auto Kopplung).
        if ((options.TachometerOverride ?? fan.Tachometer) is not { } tach)
            throw new NoTachometerException($"Lüfter {fanId} hat kein Tachosignal — Kalibrierung nicht möglich.");

        int step = Math.Clamp(options.StepSize, 1, 255);
        var samples = new List<CalibrationSample>();
        int blind = 0;

        try
        {
            _fans.SetMode(fanId, FanMode.Manual);
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

        return Build(samples, options.SpinThresholdRpm);
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
                    "Keine lesbare Temperatur während der Kalibrierung — abgebrochen (kein Watchdog).");
            return blindGuards + 1;
        }
        return 0;
    }

    private int ReadRpm(SensorId tach)
    {
        double v = _sensors.ReadValue(tach);
        return double.IsNaN(v) ? 0 : (int)Math.Round(v);
    }

    private static FanCalibration Build(List<CalibrationSample> samples, int spinThreshold)
    {
        var spinning = samples.Where(s => s.Rpm >= spinThreshold).ToList();
        byte startPwm = spinning.Count > 0 ? spinning.OrderBy(s => s.Pwm).First().Pwm : (byte)255;

        return new FanCalibration
        {
            StartPwm = startPwm,
            MinRpm = spinning.Count > 0 ? spinning.Min(s => s.Rpm) : 0,
            MaxRpm = samples.Count > 0 ? samples.Max(s => s.Rpm) : 0,
            Samples = samples,
        };
    }
}
