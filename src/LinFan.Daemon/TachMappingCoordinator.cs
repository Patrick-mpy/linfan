// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;

namespace LinFan.Daemon;

/// <summary>
/// Koordiniert eine GUI-getriebene <b>automatische Sensor-Kopplung</b>: bestimmt empirisch, welcher
/// Drehzahl-Sensor zum Ziel-Lüfter gehört, indem der <see cref="TachometerMappingService"/> den Ziel-Lüfter
/// hochtreibt (alle anderen steuerbaren drosseln) und die Reaktion misst. Ein eindeutiger Treffer wird als
/// <see cref="FanConfig.RpmSource"/>-Override persistiert.
/// <para>
/// Fail-Safe: identisch zur Identifikation - das Drosseln der anderen Lüfter reduziert die Kühlung, daher
/// läuft im Service ein Temperatur-Watchdog (Übertemp / keine lesbare Temperatur → Abbruch), und der
/// <c>finally</c>-Pfad ruft IMMER <see cref="IFanController.RestoreDefaults"/> und gibt jeden Lüfter per
/// Resume an den Loop zurück - auch bei Abbruch/Shutdown/Exception. Der Haupt-Loop bleibt zweiter Watchdog
/// (bricht diese Aktion im Fail-Safe-Tick ab). Exklusiv mit Kalibrierung und Identifikation.
/// </para>
/// </summary>
internal sealed class TachMappingCoordinator
{
    /// <summary>
    /// Mindest-Abklingzeit nach einem Lauf, bevor ein neuer starten darf - verhindert, dass wiederholte
    /// Kopplungen die anderen Lüfter dauerhaft nahe PWM 0 (weniger Kühlung) halten.
    /// </summary>
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(3);

    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly ILogger _log;
    private readonly Action<string> _suspend;
    private readonly Action<string> _resume;
    private readonly Action<string, string> _onMatched; // (fanId, tachId) → Persistenz des Overrides
    private readonly Func<ISensorBackend, IFanController, TachometerMappingService> _mappingFactory;
    private readonly Func<double>? _failSafeTempC;
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly RunGate _run;
    private DateTimeOffset? _lastRunEndedAt; // Cooldown-Anker, unter der RunGate-Sperre geschützt
    private volatile IpcTachMapping? _status;

    /// <param name="onMatched">Persistiert ein eindeutiges Kopplungs-Ergebnis (fanId, tachId) als RpmSource-Override.</param>
    /// <param name="mappingFactory">Erzeugt den <see cref="TachometerMappingService"/> (injizierbar für Tests, z. B. Null-Delay).</param>
    /// <param name="failSafeTempC">Liefert die aktuelle Temp-Obergrenze für den Watchdog; <c>null</c> ⇒ Vorgabe.</param>
    /// <param name="cooldown">Abklingzeit nach einem Lauf; <c>null</c> ⇒ <see cref="DefaultCooldown"/>.</param>
    /// <param name="now">Uhr (injizierbar für Tests); <c>null</c> ⇒ <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="delay">Wartefunktion für den Cooldown (injizierbar für Tests); <c>null</c> ⇒ <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public TachMappingCoordinator(
        ISensorBackend sensors, IFanController fans, ILogger log,
        Action<string> suspend, Action<string> resume, Action<string, string> onMatched,
        CancellationToken hostToken,
        Func<ISensorBackend, IFanController, TachometerMappingService>? mappingFactory = null,
        Func<double>? failSafeTempC = null, TimeSpan? cooldown = null, Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sensors = sensors;
        _fans = fans;
        _log = log;
        _suspend = suspend;
        _resume = resume;
        _onMatched = onMatched;
        _run = new RunGate(hostToken);
        _mappingFactory = mappingFactory ?? ((s, f) => new TachometerMappingService(s, f));
        _failSafeTempC = failSafeTempC;
        _cooldown = cooldown ?? DefaultCooldown;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Aktueller Kopplungs-Status (für den Snapshot); <c>null</c> = inaktiv/quittiert.</summary>
    public IpcTachMapping? Status => _status;

    /// <summary>Läuft gerade eine Kopplung? (für die Exklusivität mit Kalibrierung/Identifikation).</summary>
    public bool IsRunning => _run.IsRunning;

    public void Start(FanId target)
    {
        var controllable = _fans.DiscoverFans().Where(f => f.CanControl).Select(f => f.Id).ToList();
        if (!controllable.Contains(target))
            return; // Ziel nicht steuerbar - der Aufrufer prüft das ebenfalls

        // The cooldown must NOT drop the request: the GUI cannot see a silent drop and waits out its 60 s
        // timeout, writing the fan off as failed and skipping its calibration too. Wait out the remainder
        // inside the run instead - read under the RunGate lock so it stays consistent with the run end
        // (_lastRunEndedAt).
        TimeSpan cooldownWait = TimeSpan.Zero;
        if (!_run.TryBegin(out CancellationToken token, underLock: () => cooldownWait = RemainingCooldown()))
        {
            // Same reasoning: report a terminal status instead of staying silent, so the GUI fails fast
            // rather than blocking on its timeout.
            _status = Fail(target, TachMappingFailReason.Busy);
            _log.LogWarning("Sensor-Kopplung abgelehnt: es läuft bereits eine ({Fan}).", target.Value);
            return;
        }

        _status = new IpcTachMapping(target.Value, TachMappingPhase.Running, Running: true);
        _log.LogInformation("Sensor-Kopplung: {Fan} → antreiben, reagierenden Tacho suchen{Wait}.", target.Value,
            cooldownWait > TimeSpan.Zero ? $" (nach {cooldownWait.TotalSeconds:0.0}s Cooldown)" : "");
        _run.Attach(RunAsync(target, controllable, cooldownWait, token));
    }

    /// <summary>
    /// Cooldown left since the last run ended; <see cref="TimeSpan.Zero"/> once it has elapsed. Capped at the
    /// cooldown itself: the wall clock is not monotonic, and a backwards step (NTP, suspend/resume) would
    /// otherwise yield "cooldown + skew" and block calibration and identification through the RunGate too.
    /// </summary>
    private TimeSpan RemainingCooldown()
    {
        if (_lastRunEndedAt is not { } last)
            return TimeSpan.Zero;
        TimeSpan elapsed = _now() - last;
        return elapsed < _cooldown ? Min(_cooldown - elapsed, _cooldown) : TimeSpan.Zero;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Bricht eine laufende Kopplung ab - oder quittiert (löscht) einen Abschluss-Status.</summary>
    public void Cancel() => _run.Cancel(whenIdle: () => _status = null);

    /// <summary>Bricht ab und wartet auf das Ende (für den Daemon-Shutdown, vor dem finalen RestoreDefaults).</summary>
    public Task StopAsync() => _run.StopAsync();

    private async Task RunAsync(FanId target, IReadOnlyList<FanId> fans, TimeSpan cooldownWait, CancellationToken ct)
    {
        try
        {
            // Wait out the cooldown BEFORE throttling: no hardware has been touched yet and the control loop
            // keeps regulating. Waiting after the suspend would stretch the window with reduced cooling
            // (fail-safe). Deliberately OUTSIDE DriveAsync: everything that touches hardware sits behind an
            // unconditional finally in there - out here there is nothing to restore yet.
            if (cooldownWait > TimeSpan.Zero)
                await _delay(cooldownWait, ct).ConfigureAwait(false);

            TachMappingResult result = await DriveAsync(target, fans, ct).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case TachMappingOutcome.Matched when result.Tachometer is { } tach:
                    _status = new IpcTachMapping(target.Value, TachMappingPhase.Matched, Running: false,
                        MatchedTachId: tach.Value, RiseRpm: result.RiseRpm);
                    _onMatched(target.Value, tach.Value);
                    _log.LogInformation("Sensor-Kopplung: {Fan} → {Tach} (+{Rise} RPM).",
                        target.Value, tach.Value, result.RiseRpm);
                    break;
                case TachMappingOutcome.NoResponse:
                    _status = new IpcTachMapping(target.Value, TachMappingPhase.NoResponse, Running: false,
                        RiseRpm: result.RiseRpm);
                    // Log the measured rises as well (like the ambiguous branch): without them there is no
                    // telling whether the fan really has no tachometer or had merely not coasted down far
                    // enough when measured (inert fan → baseline too high → rise ≈ 0).
                    _log.LogInformation(
                        "Sensor-Kopplung: {Fan} - kein reagierender Tacho (kein Drehzahlsignal); gemessen: {Rises}.",
                        target.Value, DescribeContenders(result, top: 3));
                    break;
                default: // Ambiguous (Matched ohne Sensor kann nicht auftreten)
                    _status = new IpcTachMapping(target.Value, TachMappingPhase.Ambiguous, Running: false,
                        RiseRpm: result.RiseRpm);
                    _log.LogInformation(
                        "Sensor-Kopplung: {Fan} - mehrdeutig ({Contenders}), bitte manuell zuordnen.",
                        target.Value, DescribeContenders(result));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _status = Fail(target, TachMappingFailReason.Canceled);
            _log.LogInformation("Sensor-Kopplung abgebrochen: {Fan}.", target.Value);
        }
        catch (OverTemperatureException ex)
        {
            _status = Fail(target, TachMappingFailReason.OverTemperature, ex.TemperatureC, ex.LimitC);
            _log.LogWarning("Sensor-Kopplung abgebrochen (Übertemperatur): {Fan}.", target.Value);
        }
        catch (NoTemperatureReadingException ex)
        {
            _status = Fail(target, TachMappingFailReason.NoTemperatureReading);
            _log.LogWarning("Sensor-Kopplung abgebrochen (kein Watchdog): {Fan} - {Reason}", target.Value, ex.Message);
        }
        catch (FanNotControllableException ex)
        {
            // „Nicht steuerbar" ist ein Normalzustand → saubere Info ohne Exception-Trace.
            _status = Fail(target, TachMappingFailReason.NotControllable);
            _log.LogInformation("Sensor-Kopplung nicht möglich: {Fan} - {Reason}", target.Value, ex.Message);
        }
        catch (Exception ex)
        {
            _status = Fail(target, TachMappingFailReason.Unknown);
            _log.LogWarning(ex, "Sensor-Kopplung fehlgeschlagen: {Fan}.", target.Value);
        }
        finally
        {
            _run.End(underLock: () => _lastRunEndedAt = _now()); // Cooldown-Fenster ab dem Lauf-Ende
        }
    }

    /// <summary>
    /// Everything that touches hardware, behind an <b>unconditional</b> finally: throttle the other fans,
    /// drive the target, then always hand every fan back to the loop on firmware auto - cancellation,
    /// over-temperature and shutdown included. Kept separate from the cooldown wait on purpose, so the
    /// restore can never end up behind a condition again.
    /// </summary>
    private async Task<TachMappingResult> DriveAsync(FanId target, IReadOnlyList<FanId> fans, CancellationToken ct)
    {
        foreach (FanId f in fans)
            _suspend(f.Value); // take every controllable fan off the tick loop BEFORE the service writes

        try
        {
            var options = new TachMappingOptions
            {
                FailSafeTempC = _failSafeTempC?.Invoke() ?? CalibrationOptions.DefaultFailSafeTempC,
            };
            // Pass the suspended set as the throttle set → throttled and suspended fans are the same set.
            return await _mappingFactory(_sensors, _fans)
                .MapAsync(target, options, ct, fans)
                .ConfigureAwait(false);
        }
        finally
        {
            _fans.RestoreDefaults();   // immediately safe: every fan back on firmware auto (over-temp above all)
            foreach (FanId f in fans)
                _resume(f.Value);      // hand back to the loop → curve/manual control applies again
        }
    }

    /// <summary>Names the strongest responding sensors for the log diagnosis (ambiguous / no signal).</summary>
    private static string DescribeContenders(TachMappingResult result, int top = 2) =>
        result.Rises is { Count: > 0 } rises
            ? string.Join(" ≈ ", rises.Take(top).Select(x => $"{x.Sensor.Value} {x.Rise:+0;-0;0} RPM"))
            : $"{result.RiseRpm:+0;-0;0} RPM";

    private static IpcTachMapping Fail(
        FanId target, TachMappingFailReason reason, double? overTempC = null, double? overLimitC = null) =>
        new(target.Value, TachMappingPhase.Failed, Running: false, FailReason: reason,
            OverTempC: overTempC, OverLimitC: overLimitC);
}
