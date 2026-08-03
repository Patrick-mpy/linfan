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
/// Fail-Safe: identisch zur Identifikation — das Drosseln der anderen Lüfter reduziert die Kühlung, daher
/// läuft im Service ein Temperatur-Watchdog (Übertemp / keine lesbare Temperatur → Abbruch), und der
/// <c>finally</c>-Pfad ruft IMMER <see cref="IFanController.RestoreDefaults"/> und gibt jeden Lüfter per
/// Resume an den Loop zurück — auch bei Abbruch/Shutdown/Exception. Der Haupt-Loop bleibt zweiter Watchdog
/// (bricht diese Aktion im Fail-Safe-Tick ab). Exklusiv mit Kalibrierung und Identifikation.
/// </para>
/// </summary>
internal sealed class TachMappingCoordinator
{
    /// <summary>
    /// Mindest-Abklingzeit nach einem Lauf, bevor ein neuer starten darf — verhindert, dass wiederholte
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

    private readonly RunGate _run;
    private DateTimeOffset? _lastRunEndedAt; // Cooldown-Anker, unter der RunGate-Sperre geschützt
    private volatile IpcTachMapping? _status;

    /// <param name="onMatched">Persistiert ein eindeutiges Kopplungs-Ergebnis (fanId, tachId) als RpmSource-Override.</param>
    /// <param name="mappingFactory">Erzeugt den <see cref="TachometerMappingService"/> (injizierbar für Tests, z. B. Null-Delay).</param>
    /// <param name="failSafeTempC">Liefert die aktuelle Temp-Obergrenze für den Watchdog; <c>null</c> ⇒ Vorgabe.</param>
    /// <param name="cooldown">Abklingzeit nach einem Lauf; <c>null</c> ⇒ <see cref="DefaultCooldown"/>.</param>
    /// <param name="now">Uhr (injizierbar für Tests); <c>null</c> ⇒ <see cref="DateTimeOffset.UtcNow"/>.</param>
    public TachMappingCoordinator(
        ISensorBackend sensors, IFanController fans, ILogger log,
        Action<string> suspend, Action<string> resume, Action<string, string> onMatched,
        CancellationToken hostToken,
        Func<ISensorBackend, IFanController, TachometerMappingService>? mappingFactory = null,
        Func<double>? failSafeTempC = null, TimeSpan? cooldown = null, Func<DateTimeOffset>? now = null)
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
    }

    /// <summary>Aktueller Kopplungs-Status (für den Snapshot); <c>null</c> = inaktiv/quittiert.</summary>
    public IpcTachMapping? Status => _status;

    /// <summary>Läuft gerade eine Kopplung? (für die Exklusivität mit Kalibrierung/Identifikation).</summary>
    public bool IsRunning => _run.IsRunning;

    public void Start(FanId target)
    {
        var controllable = _fans.DiscoverFans().Where(f => f.CanControl).Select(f => f.Id).ToList();
        if (!controllable.Contains(target))
            return; // Ziel nicht steuerbar — der Aufrufer prüft das ebenfalls

        if (!_run.TryBegin(out CancellationToken token, canStart: CooldownElapsed))
            return; // läuft bereits ODER Cooldown noch aktiv

        _status = new IpcTachMapping(target.Value, TachMappingPhase.Running, Running: true);
        _log.LogInformation("Sensor-Kopplung: {Fan} → antreiben, reagierenden Tacho suchen.", target.Value);
        _run.Attach(RunAsync(target, controllable, token));
    }

    private bool CooldownElapsed()
    {
        if (_lastRunEndedAt is { } last && _now() - last < _cooldown)
        {
            _log.LogDebug("Sensor-Kopplung abgelehnt: Cooldown aktiv (noch {Remaining:0.0}s).",
                (_cooldown - (_now() - last)).TotalSeconds);
            return false;
        }
        return true;
    }

    /// <summary>Bricht eine laufende Kopplung ab — oder quittiert (löscht) einen Abschluss-Status.</summary>
    public void Cancel() => _run.Cancel(whenIdle: () => _status = null);

    /// <summary>Bricht ab und wartet auf das Ende (für den Daemon-Shutdown, vor dem finalen RestoreDefaults).</summary>
    public Task StopAsync() => _run.StopAsync();

    private async Task RunAsync(FanId target, IReadOnlyList<FanId> fans, CancellationToken ct)
    {
        foreach (FanId f in fans)
            _suspend(f.Value); // alle steuerbaren Lüfter dem Tick-Loop entziehen, BEVOR der Service schreibt

        try
        {
            var options = new TachMappingOptions
            {
                FailSafeTempC = _failSafeTempC?.Invoke() ?? CalibrationOptions.DefaultFailSafeTempC,
            };
            // Die suspendierte Menge als Drossel-Menge durchreichen → Drossel- und Suspend-Menge deckungsgleich.
            TachMappingResult result = await _mappingFactory(_sensors, _fans)
                .MapAsync(target, options, ct, fans)
                .ConfigureAwait(false);

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
                    _log.LogInformation("Sensor-Kopplung: {Fan} — kein reagierender Tacho (kein Drehzahlsignal).",
                        target.Value);
                    break;
                default: // Ambiguous (Matched ohne Sensor kann nicht auftreten)
                    _status = new IpcTachMapping(target.Value, TachMappingPhase.Ambiguous, Running: false,
                        RiseRpm: result.RiseRpm);
                    _log.LogInformation(
                        "Sensor-Kopplung: {Fan} — mehrdeutig ({Contenders}), bitte manuell zuordnen.",
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
            _log.LogWarning("Sensor-Kopplung abgebrochen (kein Watchdog): {Fan} — {Reason}", target.Value, ex.Message);
        }
        catch (FanNotControllableException ex)
        {
            // „Nicht steuerbar" ist ein Normalzustand → saubere Info ohne Exception-Trace.
            _status = Fail(target, TachMappingFailReason.NotControllable);
            _log.LogInformation("Sensor-Kopplung nicht möglich: {Fan} — {Reason}", target.Value, ex.Message);
        }
        catch (Exception ex)
        {
            _status = Fail(target, TachMappingFailReason.Unknown);
            _log.LogWarning(ex, "Sensor-Kopplung fehlgeschlagen: {Fan}.", target.Value);
        }
        finally
        {
            _fans.RestoreDefaults();          // sofort sicher: alle Lüfter auf Hardware-Auto (v. a. bei Übertemp)
            foreach (FanId f in fans)
                _resume(f.Value);             // an den Loop zurückgeben → Kurve/Manuell greift wieder
            _run.End(underLock: () => _lastRunEndedAt = _now()); // Cooldown-Fenster ab dem Lauf-Ende
        }
    }

    /// <summary>Benennt die am stärksten reagierenden Sensoren (Top 2) für die „mehrdeutig"-Diagnose im Log.</summary>
    private static string DescribeContenders(TachMappingResult result) =>
        result.Rises is { Count: > 0 } rises
            ? string.Join(" ≈ ", rises.Take(2).Select(x => $"{x.Sensor.Value} +{x.Rise} RPM"))
            : $"+{result.RiseRpm} RPM";

    private static IpcTachMapping Fail(
        FanId target, TachMappingFailReason reason, double? overTempC = null, double? overLimitC = null) =>
        new(target.Value, TachMappingPhase.Failed, Running: false, FailReason: reason,
            OverTempC: overTempC, OverLimitC: overLimitC);
}
