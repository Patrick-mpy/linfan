// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Automatische Sensor-Kopplung: treibt <b>einen</b> Lüfter hoch (alle anderen steuerbaren auf 0),
/// misst den Drehzahl-Anstieg aller Tacho-Sensoren und ordnet den dominant reagierenden Sensor zu.
/// So wird empirisch bestimmt, welcher Tacho zu welchem Lüfter gehört - robuster als die namensbasierte
/// Backend-Heuristik.
/// <para>
/// Fail-Safe: Das Drosseln der anderen Lüfter reduziert die Kühlung (gefährliche Richtung), daher läuft
/// während des kurzen Antreibens ein Temperatur-Watchdog (Übertemp ODER keine lesbare Temperatur →
/// sofortiger Abbruch), und der <c>finally</c>-Pfad ruft IMMER <see cref="IFanController.RestoreDefaults"/>
/// (alle Lüfter zurück auf Firmware-Auto) - auch bei Abbruch/Exception. Spiegelt das Watchdog-/Restore-
/// Muster des <see cref="CalibrationService"/>; die Suspend/Resume-Kopplung an den Regel-Loop liegt beim
/// aufrufenden Coordinator.
/// </para>
/// </summary>
public sealed class TachometerMappingService
{
    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Wartefunktion (injizierbar für Tests); Standard ist <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public TachometerMappingService(
        ISensorBackend sensors,
        IFanController fans,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sensors = sensors;
        _fans = fans;
        _delay = delay ?? Task.Delay;
    }

    /// <param name="throttleFans">
    /// Steuerbare Lüfter, die zum Messen auf 0 gedrosselt werden (der Ziel-Lüfter wird ausgenommen). Der
    /// Coordinator reicht hier seine <b>suspendierte</b> Menge durch, damit Drossel- und Suspend-Menge
    /// deckungsgleich sind (kein „getrieben, aber nicht suspendiert"-Zweischreiber). <c>null</c> ⇒ selbst
    /// ermitteln (Standalone-/Test-Aufruf).
    /// </param>
    public async Task<TachMappingResult> MapAsync(
        FanId fanId, TachMappingOptions options, CancellationToken ct = default,
        IReadOnlyCollection<FanId>? throttleFans = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _ = _fans.DiscoverFans().FirstOrDefault(f => f.Id == fanId)
            ?? throw new KeyNotFoundException($"Unbekannter Lüfter: {fanId}");
        if (!_fans.CanControl(fanId))
            throw new FanNotControllableException($"Lüfter {fanId} ist nicht steuerbar (Root nötig).");

        // Kandidaten sind alle Drehzahl-Sensoren; gibt es keinen, kann nichts reagieren.
        var rpmSensors = _sensors.DiscoverSensors()
            .Where(s => s.Kind == SensorKind.FanRpm)
            .Select(s => s.Id)
            .ToList();
        if (rpmSensors.Count == 0)
            return new TachMappingResult(fanId, null, TachMappingOutcome.NoResponse, 0);

        // Alle anderen steuerbaren Lüfter drosseln → nur der Ziel-Lüfter ändert sich, klareres Signal.
        var others = (throttleFans ?? _fans.DiscoverFans().Where(f => _fans.CanControl(f.Id)).Select(f => f.Id).ToList())
            .Where(id => id != fanId)
            .ToList();

        int blind = 0;
        try
        {
            // Watchdog BEFORE any intervention: on over-temperature, do not throttle the other fans at all.
            // With a safety margin (StartMarginC): starting just below the limit would spend the whole
            // measurement window without airflow on its way into the watchdog. The abort during the run
            // keeps using the full limit - an already running attempt must not fail on the margin.
            blind = Guard(Math.Max(0, options.FailSafeTempC - options.StartMarginC), blind);
            ct.ThrowIfCancellationRequested();

            _fans.SetMode(fanId, FanMode.Manual);
            foreach (FanId o in others)
            {
                _fans.SetMode(o, FanMode.Manual);
                _fans.SetPwm(o, 0);
            }

            // Baseline: Ziel niedrig, andere niedrig - engmaschig überwacht einpendeln, dann alle Drehzahlen lesen.
            // Dies ist der kühlungsärmste Zustand (alles nahe 0), daher slice-weiser Watchdog wie beim Identify-Hold.
            // Coasting down needs more time than spinning up (see BaselineSettleTime), else the baseline is too high.
            _fans.SetPwm(fanId, 0);
            blind = await SettleAsync(options.BaselineSettleTime, options.FailSafeTempC, blind, ct).ConfigureAwait(false);
            var baseline = rpmSensors.ToDictionary(id => id, ReadRpm);

            // Ziel hochtreiben (andere bleiben niedrig) - einpendeln, dann erneut lesen.
            _fans.SetPwm(fanId, options.DrivePwm);
            blind = await SettleAsync(options.SettleTime, options.FailSafeTempC, blind, ct).ConfigureAwait(false);

            var rises = rpmSensors
                .Select(id => (Id: id, Rise: ReadRpm(id) - baseline[id]))
                .OrderByDescending(x => x.Rise)
                .ToList();
            return Evaluate(fanId, rises, options);
        }
        finally
        {
            _fans.RestoreDefaults(); // Fail-Safe: alle Lüfter zurück auf Firmware-Auto
        }
    }

    /// <summary>
    /// Wertet die Drehzahl-Anstiege aus: stärkster Sensor gewinnt, muss aber (1) über <c>MinRiseRpm</c> und
    /// (2) um <c>DominanceFactor</c> über dem zweitstärksten liegen - sonst keine Reaktion bzw. mehrdeutig.
    /// </summary>
    private static TachMappingResult Evaluate(
        FanId fanId, IReadOnlyList<(SensorId Id, int Rise)> rises, TachMappingOptions options)
    {
        var best = rises[0];
        if (best.Rise < options.MinRiseRpm)
            return new TachMappingResult(fanId, null, TachMappingOutcome.NoResponse, best.Rise, rises);

        int second = rises.Count > 1 ? Math.Max(0, rises[1].Rise) : 0;
        if (second > 0 && best.Rise < second * options.DominanceFactor)
            return new TachMappingResult(fanId, null, TachMappingOutcome.Ambiguous, best.Rise, rises);

        return new TachMappingResult(fanId, best.Id, TachMappingOutcome.Matched, best.Rise, rises);
    }

    /// <summary>Watchdog-/Abbruch-Intervall während der Settle-Fenster (wie der IdentifyCoordinator-Hold).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Wartet <paramref name="total"/> in kleinen Slices und prüft in jedem den Temperatur-Watchdog und den
    /// Abbruch - damit ein Temperaturspike während des (kühlungsreduzierten) Einpendelns sofort greift, nicht
    /// erst am Fensterende. Ein finaler Guard läuft direkt vor dem Messen. Gibt den Blind-Zähler fort.
    /// </summary>
    private async Task<int> SettleAsync(TimeSpan total, double limitC, int blind, CancellationToken ct)
    {
        TimeSpan remaining = total;
        while (remaining > TimeSpan.Zero)
        {
            ct.ThrowIfCancellationRequested();
            blind = Guard(limitC, blind);
            TimeSpan slice = remaining < PollInterval ? remaining : PollInterval;
            await _delay(slice, ct).ConfigureAwait(false);
            remaining -= slice;
        }
        ct.ThrowIfCancellationRequested();
        return Guard(limitC, blind);
    }

    /// <summary>So viele Prüfpunkte ohne lesbare Temperatur brechen ab (kein Watchdog möglich).</summary>
    private const int MaxBlindGuards = 4;

    /// <summary>
    /// Temperatur-Watchdog (wie beim <see cref="CalibrationService"/>): Übertemperatur → sofort abbrechen;
    /// keine lesbare Temperatur → nach einigen Prüfpunkten abbrechen (kein Antreiben ohne Watchdog).
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
                    "Keine lesbare Temperatur während der Sensor-Kopplung - abgebrochen (kein Watchdog).");
            return blindGuards + 1;
        }
        return 0;
    }

    private int ReadRpm(SensorId id)
    {
        double v = _sensors.ReadValue(id);
        return double.IsNaN(v) ? 0 : (int)Math.Round(v);
    }
}
