// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging;

namespace LinFan.Daemon;

/// <summary>
/// Koordiniert eine GUI-getriebene Lüfter-<b>Identifikation</b>: den Ziel-Lüfter kurz auf 100 % drehen
/// und ALLE anderen steuerbaren Lüfter drosseln (PWM 0), damit klar erkennbar ist, welcher physische
/// Lüfter zu einem Kanal gehört (Hochdrehen statt Stoppen — wegen des Hardware-Drehzahl-Floors lässt
/// sich nicht herunterregeln). Spiegelt das Suspend→treiben→Resume-Muster der Kalibrierung.
/// <para>
/// Fail-Safe: Anders als ein reiner Spin-up reduziert das Drosseln der <em>anderen</em> Lüfter die
/// Kühlung — treibt also in die gefährliche Richtung. Deshalb (1) läuft während des kurzen Hold ein
/// Temperatur-Watchdog (Übertemp ODER keine lesbare Temperatur → sofortiger Abbruch), (2) ist die Dauer
/// eng begrenzt, und (3) ruft der <c>finally</c>-Pfad IMMER <see cref="IFanController.RestoreDefaults"/>
/// (alle Lüfter sofort auf Hardware-Auto) und gibt jeden Lüfter per Resume an den Loop zurück — auch bei
/// Abbruch/Shutdown/Exception. Der Haupt-Loop bleibt als zweiter Watchdog aktiv (er tickt weiter, kann
/// selbst Fail-Safe auslösen und bricht diese Aktion im Fail-Safe-Tick ab).
/// </para>
/// </summary>
internal sealed class IdentifyCoordinator
{
    /// <summary>Standard-Dauer des Identifikations-Pulses.</summary>
    private static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Mindest-Abklingzeit nach dem Ende eines Laufs, bevor ein neuer starten darf. Verhindert, dass
    /// aufeinanderfolgende Identifikationen die <em>anderen</em> Lüfter dauerhaft nahe PWM 0 (weniger
    /// Kühlung) halten — die Firmware regelt in der Pause wieder normal.
    /// </summary>
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(3);

    /// <summary>Prüf-/Watchdog-Intervall während des Hold.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>So viele Prüfpunkte ohne lesbare Temperatur brechen ab (kein Watchdog möglich).</summary>
    private const int MaxBlindGuards = 4;

    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly ILogger _log;
    private readonly Action<string> _suspend;
    private readonly Action<string> _resume;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double>? _failSafeTempC;
    private readonly TimeSpan _hold;
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTimeOffset> _now;

    private readonly RunGate _run;
    private DateTimeOffset? _lastRunEndedAt; // Cooldown-Anker, unter der RunGate-Sperre geschützt
    private volatile IpcIdentify? _status;

    /// <param name="delay">Wartefunktion (injizierbar für Tests, z. B. Null-Delay); Standard <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="failSafeTempC">Liefert die aktuelle Temp-Obergrenze für den Watchdog; <c>null</c> ⇒ Vorgabe.</param>
    /// <param name="hold">Pulsdauer; <c>null</c> ⇒ <see cref="DefaultHold"/>.</param>
    /// <param name="cooldown">Abklingzeit nach einem Lauf; <c>null</c> ⇒ <see cref="DefaultCooldown"/>.</param>
    /// <param name="now">Uhr (injizierbar für Tests); <c>null</c> ⇒ <see cref="DateTimeOffset.UtcNow"/>.</param>
    public IdentifyCoordinator(
        ISensorBackend sensors, IFanController fans, ILogger log,
        Action<string> suspend, Action<string> resume, CancellationToken hostToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? failSafeTempC = null, TimeSpan? hold = null,
        TimeSpan? cooldown = null, Func<DateTimeOffset>? now = null)
    {
        _sensors = sensors;
        _fans = fans;
        _log = log;
        _suspend = suspend;
        _resume = resume;
        _run = new RunGate(hostToken);
        _delay = delay ?? Task.Delay;
        _failSafeTempC = failSafeTempC;
        _hold = hold ?? DefaultHold;
        _cooldown = cooldown ?? DefaultCooldown;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Aktueller Identifikations-Status (für den Snapshot); <c>null</c> = inaktiv.</summary>
    public IpcIdentify? Status => _status;

    /// <summary>Läuft gerade eine Identifikation? (für die Exklusivität mit der Kalibrierung).</summary>
    public bool IsRunning => _run.IsRunning;

    public void Start(FanId target)
    {
        var controllable = _fans.DiscoverFans().Where(f => f.CanControl).Select(f => f.Id).ToList();
        if (!controllable.Contains(target))
            return; // Ziel nicht steuerbar — der Aufrufer prüft das ebenfalls

        // Cooldown wird unter der Gate-Sperre geprüft — atomar mit dem Lauf-Beginn und dem Setzen von
        // _lastRunEndedAt im finally des Vorlaufs.
        if (!_run.TryBegin(out CancellationToken token, canStart: CooldownElapsed))
            return; // läuft bereits ODER Cooldown noch aktiv

        _status = new IpcIdentify(target.Value, Running: true);
        _log.LogInformation("Identifikation: {Fan} → Puls 100 %, andere gedrosselt.", target.Value);
        _run.Attach(RunAsync(target, controllable, token));
    }

    /// <summary>
    /// Ist der Cooldown seit dem letzten Lauf abgelaufen? Läuft unter der RunGate-Sperre. Zu früh →
    /// ablehnen, damit ein wiederholter Aufruf die anderen Lüfter nicht dauerhaft nahe PWM 0 (weniger
    /// Kühlung) hält — die Firmware regelt in der Pause wieder normal.
    /// </summary>
    private bool CooldownElapsed()
    {
        if (_lastRunEndedAt is { } last && _now() - last < _cooldown)
        {
            _log.LogDebug("Identifikation abgelehnt: Cooldown aktiv (noch {Remaining:0.0}s).",
                (_cooldown - (_now() - last)).TotalSeconds);
            return false;
        }
        return true;
    }

    /// <summary>Bricht eine laufende Identifikation ab (Fail-Safe-Tick, Shutdown).</summary>
    public void Cancel() => _run.Cancel();

    /// <summary>Bricht ab und wartet auf das Ende (für den Daemon-Shutdown, vor dem finalen RestoreDefaults).</summary>
    public Task StopAsync() => _run.StopAsync();

    private async Task RunAsync(FanId target, IReadOnlyList<FanId> fans, CancellationToken ct)
    {
        foreach (FanId f in fans)
            _suspend(f.Value); // alle betroffenen Lüfter dem Tick-Loop entziehen, BEVOR wir schreiben

        try
        {
            double limit = _failSafeTempC?.Invoke() ?? CalibrationOptions.DefaultFailSafeTempC;
            int blind = Guard(limit, 0); // nicht drosseln, wenn es jetzt schon zu heiß ist

            foreach (FanId f in fans)
                _fans.SetPwm(f, f == target ? (byte)255 : (byte)0); // Ziel hoch, alle anderen runter

            TimeSpan remaining = _hold;
            while (remaining > TimeSpan.Zero)
            {
                ct.ThrowIfCancellationRequested();
                blind = Guard(limit, blind);
                TimeSpan slice = remaining < PollInterval ? remaining : PollInterval;
                await _delay(slice, ct).ConfigureAwait(false);
                remaining -= slice;
            }

            _status = null; // fertig — kein Status mehr, Button wieder frei
            _log.LogInformation("Identifikation beendet: {Fan}.", target.Value);
        }
        catch (OperationCanceledException)
        {
            _status = null;
        }
        catch (OverTemperatureException ex)
        {
            _status = new IpcIdentify(target.Value, Running: false, FailReason: IdentifyFailReason.OverTemperature,
                OverTempC: ex.TemperatureC, OverLimitC: ex.LimitC);
            _log.LogWarning("Identifikation abgebrochen (Übertemperatur): {Fan}.", target.Value);
        }
        catch (NoTemperatureReadingException ex)
        {
            _status = new IpcIdentify(target.Value, Running: false, FailReason: IdentifyFailReason.NoTemperatureReading);
            _log.LogWarning("Identifikation abgebrochen (kein Watchdog): {Fan} — {Reason}", target.Value, ex.Message);
        }
        catch (Exception ex)
        {
            _status = new IpcIdentify(target.Value, Running: false, FailReason: IdentifyFailReason.Unknown);
            _log.LogWarning(ex, "Identifikation fehlgeschlagen: {Fan}.", target.Value);
        }
        finally
        {
            _fans.RestoreDefaults();          // sofort sicher: alle Lüfter auf Hardware-Auto (v. a. bei Übertemp)
            foreach (FanId f in fans)
                _resume(f.Value);             // an den Loop zurückgeben → Kurve/Manuell greift wieder
            _run.End(underLock: () => _lastRunEndedAt = _now()); // Cooldown-Fenster ab dem Lauf-Ende
        }
    }

    /// <summary>
    /// Temp-Watchdog für den Puls: Übertemperatur → sofort abbrechen; keine lesbare Temperatur → nach
    /// einigen Prüfpunkten abbrechen (eine Aktion, die Kühlung drosselt, ist ohne Watchdog unzulässig).
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
                    "Keine lesbare Temperatur während der Identifikation — abgebrochen (kein Watchdog).");
            return blindGuards + 1;
        }
        return 0;
    }
}
