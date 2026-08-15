// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;

namespace LinFan.Daemon;

/// <summary>
/// Koordiniert eine GUI-getriebene Kalibrierung (immer nur eine gleichzeitig). Entzieht den Lüfter
/// dem <see cref="ControlLoop"/> (Suspend), lässt den <see cref="CalibrationService"/> rampen, meldet
/// Fortschritt für den Snapshot und übergibt das Ergebnis an den Daemon (Persistenz im Tick-Loop).
/// <para>
/// Fail-Safe: Das Kalibrier-Token ist an das Daemon-Shutdown-Token gekoppelt; <see cref="StopAsync"/>
/// bricht ab und wartet, bevor der Daemon sein abschließendes RestoreDefaults ausführt - so kann die
/// Rampe nach dem RestoreDefaults nicht weiterlaufen. Suspend/Resume sind symmetrisch im
/// <c>RunAsync</c>-finally, sodass kein Lüfter suspendiert hängen bleibt.
/// </para>
/// </summary>
internal sealed class CalibrationCoordinator
{
    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly ILogger _log;
    private readonly Action<string> _suspend;
    private readonly Action<string> _resume;
    private readonly Action<string, FanCalibration> _onResult;
    private readonly Func<ISensorBackend, IFanController, CalibrationService> _calibrationFactory;
    private readonly Func<double>? _failSafeTempC;
    private readonly Func<FanId, SensorId?>? _tachometerOverride;

    private readonly RunGate _run;
    private volatile IpcCalibration? _status;

    /// <param name="calibrationFactory">
    /// Erzeugt den <see cref="CalibrationService"/> (injizierbar für Tests, z. B. mit Null-Delay);
    /// Standard ist <c>new CalibrationService(sensors, fans)</c> mit echtem <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// </param>
    /// <param name="failSafeTempC">
    /// Liefert die aktuell konfigurierte Temperatur-Obergrenze für den Kalibrier-Watchdog (wird bei jedem
    /// Start ausgewertet, damit Config-Änderungen greifen). <c>null</c> ⇒ Vorgabe
    /// (<see cref="CalibrationOptions.DefaultFailSafeTempC"/>).
    /// </param>
    /// <param name="tachometerOverride">
    /// Liefert den explizit zugeordneten Drehzahl-Sensor eines Lüfters (aus <see cref="FanConfig.RpmSource"/>),
    /// der den Backend-Guess für die RPM-Messung übersteuert; bei jedem Start ausgewertet. <c>null</c> bzw.
    /// Rückgabe <c>null</c> ⇒ den Tacho des <c>FanDescriptor</c> verwenden.
    /// </param>
    public CalibrationCoordinator(
        ISensorBackend sensors, IFanController fans, ILogger log,
        Action<string> suspend, Action<string> resume, Action<string, FanCalibration> onResult,
        CancellationToken hostToken,
        Func<ISensorBackend, IFanController, CalibrationService>? calibrationFactory = null,
        Func<double>? failSafeTempC = null,
        Func<FanId, SensorId?>? tachometerOverride = null)
    {
        _sensors = sensors;
        _fans = fans;
        _log = log;
        _suspend = suspend;
        _resume = resume;
        _onResult = onResult;
        _run = new RunGate(hostToken);
        _calibrationFactory = calibrationFactory ?? ((s, f) => new CalibrationService(s, f));
        _failSafeTempC = failSafeTempC;
        _tachometerOverride = tachometerOverride;
    }

    /// <summary>Aktueller Kalibrier-Status (für den Snapshot); <c>null</c> = inaktiv/quittiert.</summary>
    public IpcCalibration? Status => _status;

    /// <summary>
    /// Läuft gerade eine Kalibrierung? Liest den echten Lauf-Zustand (nicht das Snapshot-Feld
    /// <see cref="Status"/>, das schon <c>Running: false</c> meldet, während der Lüfter im <c>finally</c>
    /// noch resumed wird). Für die Exklusivität mit der Identifikation maßgeblich.
    /// </summary>
    public bool IsRunning => _run.IsRunning;

    public void Start(FanId fanId)
    {
        if (!_run.TryBegin(out CancellationToken token))
            return; // es läuft bereits eine Kalibrierung

        _status = new IpcCalibration(
            fanId.Value, CalibrationPhase.Starting, 0, 0, Running: true, Done: false, StartPwm: null, FailReason: null);
        _log.LogInformation("Kalibrierung gestartet: {Fan}", fanId.Value);
        _run.Attach(RunAsync(fanId, token));
    }

    /// <summary>Bricht eine laufende Kalibrierung ab - oder quittiert (löscht) einen Abschluss-Status.</summary>
    public void Cancel() => _run.Cancel(whenIdle: () => _status = null);

    /// <summary>Bricht eine laufende Kalibrierung ab und wartet auf ihr Ende (für den Daemon-Shutdown).</summary>
    public Task StopAsync() => _run.StopAsync();

    private async Task RunAsync(FanId fanId, CancellationToken ct)
    {
        _suspend(fanId.Value); // Lüfter dem Tick-Loop entziehen, bevor die Rampe schreibt
        var progress = new Progress<CalibrationProgress>(p =>
        {
            IpcCalibration? s = _status;
            if (s is not null)
                _status = s with { Phase = CalibrationPhase.Measuring, CurrentPwm = p.Pwm, CurrentRpm = p.Rpm };
        });

        try
        {
            // Watchdog-Obergrenze aus der Live-Config (nicht fest 90 °C), bei jedem Start frisch ausgewertet;
            // ebenso das RpmSource-Override (explizit zugeordneter Tacho gewinnt vor dem Backend-Guess).
            var options = new CalibrationOptions
            {
                FailSafeTempC = _failSafeTempC?.Invoke() ?? CalibrationOptions.DefaultFailSafeTempC,
                TachometerOverride = _tachometerOverride?.Invoke(fanId),
            };
            FanCalibration result = await _calibrationFactory(_sensors, _fans)
                .CalibrateAsync(fanId, options, ct, progress)
                .ConfigureAwait(false);

            _status = new IpcCalibration(fanId.Value, CalibrationPhase.Done, result.StartPwm, result.MaxRpm,
                Running: false, Done: true, result.StartPwm, FailReason: null);
            _onResult(fanId.Value, result);
            _log.LogInformation("Kalibrierung fertig: {Fan} · Anlauf pwm={Pwm} · {Min}-{Max} RPM",
                fanId.Value, result.StartPwm, result.MinRpm, result.MaxRpm);
        }
        catch (OperationCanceledException)
        {
            _status = Fail(fanId, CalibrationFailReason.Canceled);
            _log.LogInformation("Kalibrierung abgebrochen: {Fan}", fanId.Value);
        }
        catch (OverTemperatureException ex)
        {
            _status = Fail(fanId, CalibrationFailReason.OverTemperature, ex.TemperatureC, ex.LimitC);
            _log.LogWarning("Kalibrierung abgebrochen (Übertemperatur): {Fan}", fanId.Value);
        }
        catch (FanNotControllableException ex)
        {
            // „Nicht steuerbar" ist ein Normalzustand (read-only / ohne Rechte), kein Fehler →
            // saubere Info ohne Exception-Trace statt warn + Cross-Build-Stacktrace.
            _status = Fail(fanId, CalibrationFailReason.NotControllable);
            _log.LogInformation("Kalibrierung nicht möglich: {Fan} - {Reason}", fanId.Value, ex.Message);
        }
        catch (NoTachometerException ex)
        {
            // „Kein Tacho" ist ein Normalzustand (read-only ohne Drehzahl-Feedback), kein Fehler →
            // saubere Info ohne Exception-Trace statt warn + Cross-Build-Stacktrace.
            _status = Fail(fanId, CalibrationFailReason.NoTacho);
            _log.LogInformation("Kalibrierung nicht möglich: {Fan} - {Reason}", fanId.Value, ex.Message);
        }
        catch (NoTemperatureReadingException ex)
        {
            // Fail-Safe-Abbruch (kein Watchdog möglich) - bekannte Ursache, daher Warnung ohne Trace.
            _status = Fail(fanId, CalibrationFailReason.NoTemperatureReading);
            _log.LogWarning("Kalibrierung abgebrochen (kein Watchdog): {Fan} - {Reason}", fanId.Value, ex.Message);
        }
        catch (Exception ex)
        {
            _status = Fail(fanId, CalibrationFailReason.Unknown);
            _log.LogWarning(ex, "Kalibrierung fehlgeschlagen: {Fan}", fanId.Value);
        }
        finally
        {
            _resume(fanId.Value); // Lüfter IMMER wieder an den Tick-Loop zurückgeben
            _run.End();
        }
    }

    private static IpcCalibration Fail(
        FanId fanId, CalibrationFailReason reason, double? overTempC = null, double? overLimitC = null) =>
        new(fanId.Value, CalibrationPhase.Failed, 0, 0, Running: false, Done: false,
            StartPwm: null, FailReason: reason, OverTempC: overTempC, OverLimitC: overLimitC);
}
