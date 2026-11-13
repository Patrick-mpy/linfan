// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Der Regel-Kern: ein <see cref="Tick"/> liest pro geregeltem Lüfter den Quell-Sensor, glättet ihn
/// (<see cref="TemperatureSmoother"/>), wertet die zugeordnete Kurve aus (mit Hysterese), klemmt auf die
/// PWM-Grenzen des Lüfters und setzt den Wert.
/// <para>
/// Vor allem anderen prüft jeder Tick den Temperatur-Watchdog: bei Übertemperatur sofort
/// <see cref="IFanController.RestoreDefaults"/> (Hardware-Auto / Fail-Safe). Im <c>dryRun</c>-Modus
/// wird nur gerechnet, nicht geschrieben (z. B. ohne Root). A dry run restores nothing either: the fail-safe is only
/// reported.
/// </para>
/// <para>
/// The smoothing sits on the curve input only. The watchdog below deliberately reads raw values, so a
/// genuine over-temperature still trips in the very tick it appears, however long the window is.
/// </para>
/// <para>
/// Each tick also records who controls each fan (<see cref="FanAction.State"/>) and the fail-safe state
/// (<see cref="ControlTick.FailSafe"/>), so the GUI shows what the daemon does. That is reporting only: it never
/// changes what is written or when.
/// </para>
/// </summary>
public sealed class ControlLoop
{
    /// <summary>So viele Ticks ohne lesbare Temperatur (bei aktiver Regelung) lösen den Fail-Safe aus.</summary>
    private const int MaxBlindTicks = 3;

    /// <summary>
    /// A temperature sensor that last read within this margin of the fail-safe limit and then turns unreadable may be
    /// hiding an over-temperature: the watchdog skips unreadable sensors and would go on watching only cooler ones.
    /// Sensors that drop out while cool are common and harmless (a Wi-Fi chip in power save, a dGPU in runtime
    /// suspend) and do not count - otherwise they would trip the fail-safe on ordinary laptops.
    /// </summary>
    internal const double LostSensorMarginC = 15.0;

    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly bool _dryRun;
    private readonly TemperatureSmoother _smoother;
    private readonly Dictionary<string, double> _lastAppliedTemp = new();
    private int _blindTicks;

    // The last readable value of each temperature sensor, kept while it is unreadable (loop thread only). Compared with
    // the limit in force, so a limit raised later also releases a sensor that is no longer near it.
    private readonly Dictionary<string, double> _lastReadable = new(StringComparer.Ordinal);

    // Fans the loop has written a PWM to since it last handed them back (loop thread only). Only these are handed
    // back by full speed where a channel has no auto mode; any other fan is not rewritten tick after tick.
    private readonly HashSet<string> _driven = new(StringComparer.Ordinal);

    // Cause of the last fail-safe trip, kept until a tick reads a temperature below the limit again. Reporting
    // only (ControlTick.FailSafe): without a readable temperature nothing takes the fans out of the safe state.
    private FailSafeState _heldFailSafe;

    // Laufzeit-Steuerhoheit pro Lüfter (von außen per IPC-Thread gesetzt, im Tick vom Loop-Thread
    // gelesen → über _gate synchronisiert). Manuell = fester PWM übersteuert die Kurve; suspendiert =
    // ein anderer Besitzer (Kalibrierung) steuert, der Loop fasst den Lüfter nicht an. A suspension keeps the
    // owner's state, so the tick can report who drives the fan.
    private readonly object _gate = new();
    private readonly Dictionary<string, byte> _manualOverride = new();
    private readonly Dictionary<string, FanControlState> _suspended = new();
    private HashSet<string> _previouslyManual = new();
    // Set by Resume, consumed by the next Tick - see there.
    private bool _released;
    // Set by HandBackToAuto, consumed by the next Tick.
    private readonly HashSet<string> _handBack = new(StringComparer.Ordinal);

    public ControlLoop(ISensorBackend sensors, IFanController fans, bool dryRun = false, TimeProvider? time = null)
    {
        _sensors = sensors;
        _fans = fans;
        _dryRun = dryRun;
        _smoother = new TemperatureSmoother(time);
    }

    /// <summary>
    /// Verwirft den Hysterese-Cache <i>und</i> die Glättungs-Puffer, damit der nächste Tick alle Lüfter neu
    /// bewertet. Nach einer Konfigurationsänderung aufrufen - sonst hielte die Hysterese den alten PWM, bis
    /// die Temperatur zufällig genug driftet, und die neue Kurve würde verzögert greifen (und sie bekäme
    /// Mittelwerte aus der alten zu sehen).
    /// <para>Loop thread only: the caches it clears are not synchronized.</para>
    /// </summary>
    public void ResetFilters()
    {
        _lastAppliedTemp.Clear();
        _smoother.Reset();
    }

    /// <summary>Setzt (oder löscht mit <c>null</c>) einen festen manuellen PWM-Wert für einen Lüfter.</summary>
    public void SetManualOverride(string fanId, byte? pwm)
    {
        lock (_gate)
        {
            if (pwm is { } value) _manualOverride[fanId] = value;
            else _manualOverride.Remove(fanId);
        }
    }

    /// <summary>Verwirft alle manuellen Overrides (z. B. bei GUI-Disconnect oder Fail-Safe).</summary>
    public void ClearAllManualOverrides()
    {
        lock (_gate)
            _manualOverride.Clear();
    }

    /// <summary>Entzieht dem Loop einen Lüfter (ein anderer Besitzer, z. B. Kalibrierung, steuert ihn).</summary>
    /// <param name="fanId">The fan to take off the loop.</param>
    /// <param name="owner">What the owner does with it, reported as the fan's state: one of
    /// <see cref="FanControlState.Calibrating"/>, <see cref="FanControlState.Identifying"/>,
    /// <see cref="FanControlState.IdentifyThrottled"/>, <see cref="FanControlState.Coupling"/>,
    /// <see cref="FanControlState.CouplingThrottled"/>.</param>
    public void Suspend(string fanId, FanControlState owner)
    {
        lock (_gate)
            _suspended[fanId] = owner;
    }

    /// <summary>Gibt einen suspendierten Lüfter wieder an den Loop zurück.</summary>
    public void Resume(string fanId)
    {
        lock (_gate)
            _released |= _suspended.Remove(fanId);
    }

    /// <summary>
    /// Hands these fans back to hardware auto once, in the next tick, whether or not the loop drove them - for fans whose
    /// config entry was removed, which the loop no longer visits. A fan that has a config entry again in that tick, or
    /// that the tick already acts on (manual, suspended, released from manual, orphaned), is left to that path; a
    /// failure is not retried here.
    /// </summary>
    public void HandBackToAuto(IEnumerable<string> fanIds)
    {
        ArgumentNullException.ThrowIfNull(fanIds);
        lock (_gate)
            _handBack.UnionWith(fanIds);
    }

    /// <summary>Aktuell manuell gesteuerte Lüfter (Kopie) - für die Snapshot-Anzeige.</summary>
    public IReadOnlySet<string> ManualFanIds()
    {
        lock (_gate)
            return _manualOverride.Keys.ToHashSet();
    }

    public ControlTick Tick(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 0) Steuerhoheit pro Lüfter thread-sicher kopieren - VOR dem Watchdog, da auch ein rein
        //    manuell gesteuerter Lüfter (ohne Kurve) überwacht werden muss.
        Dictionary<string, byte> manual;
        Dictionary<string, FanControlState> suspended;
        bool released;
        string[] handBack;
        lock (_gate)
        {
            manual = new Dictionary<string, byte>(_manualOverride);
            suspended = new Dictionary<string, FanControlState>(_suspended);
            released = _released;
            _released = false;
            handBack = _handBack.ToArray();
            _handBack.Clear();
        }

        // An owner hands fans back on firmware auto - calibration even restores the fans it never took. The
        // hysteresis would keep a curve from writing again until the temperature drifts, so forget all of it.
        if (released)
            _lastAppliedTemp.Clear();

        // 1) Watchdog hat Vorrang vor jeder Kurven-/Manual-Logik.
        var readings = new TickReadings(_sensors);
        double hottest = SensorAggregator.Hottest(readings);
        // Before the over-temperature return: a sensor's reading in the tick that trips must be remembered too.
        string? lost = LostNearLimitSensor(readings, config.FailSafeTempC);

        // 1a) Übertemperatur → sofort sicherer Zustand.
        if (!double.IsNaN(hottest) && hottest >= config.FailSafeTempC)
            return FailSafe(hottest, FailSafeState.OverTemperature,
                $"Übertemperatur {hottest:0.0} °C ≥ {config.FailSafeTempC:0.0} °C");

        // 1b) Wir steuern aktiv (Kurve ODER manueller Override), können aber KEINE Temperatur lesen
        //     (z. B. alle Sensoren EIO/NaN). „Temperatur unbekannt" ist nicht „alles in Ordnung":
        //     nach einigen Ticks blind in den sicheren Zustand, statt einen festen PWM ohne
        //     Überwachung zu halten. Manuell gesteuerte Lüfter zählen ausdrücklich mit.
        //     A fan that has just left manual control, or any fan the loop drove and has not handed back yet, counts
        //     too: until it is back on the firmware (or at full speed), it may still sit at a PWM the loop wrote.
        //     So does a fan another owner drives (calibration, identification, coupling): their own watchdogs only see
        //     "no temperature at all", not a hot sensor that went missing.
        //     A sensor lost near the limit counts as blind as well. Once the fail-safe has tripped - for whatever
        //     cause - it holds for as long as the loop stays blind: the curves may still see other sensors and would
        //     drive the fans (and a new manual value would be written) in the ticks between two trips.
        bool controlling = !_dryRun
            && (config.Fans.Any(f => f.AssignedCurveId is not null) || manual.Count > 0 || suspended.Count > 0
                || _previouslyManual.Count > 0 || _driven.Count > 0);
        if (double.IsNaN(hottest) || lost is not null)
        {
            bool holding = _heldFailSafe != FailSafeState.None;
            if (controlling && (++_blindTicks >= MaxBlindTicks || holding))
                return FailSafe(hottest, FailSafeState.Blind, lost is null
                    ? $"keine lesbare Temperatur seit {_blindTicks} Ticks"
                    : $"Sensor {lost} nicht mehr lesbar, zuletzt nahe der Grenze");
        }
        else
        {
            _blindTicks = 0;
            _heldFailSafe = FailSafeState.None;
        }

        // Still blind since a trip: the fans the loop skips below sit in the safe state, not on a held curve value.
        // Not reported while the loop controls nothing (no curve, no manual fan, dry run) - the watchdog then has
        // nothing to guard.
        FailSafeState failSafe = controlling ? _heldFailSafe : FailSafeState.None;

        var actions = new List<FanAction>();

        // 2) Fans another owner drives (calibration, identification, coupling): the loop keeps its hands off.
        //    Recorded for every suspended fan, also one without a config entry (identify throttles them all).
        foreach (var (fanId, owner) in suspended)
            actions.Add(FanAction.Skipped(fanId, owner, owner.ToString()));

        // 2a) Manuelle Overrides (GUI) übersteuern die Kurve - fester PWM (Watchdog hat oben geschützt).
        foreach (var (fanId, pwm) in manual)
        {
            if (suspended.ContainsKey(fanId))
                continue;
            _lastAppliedTemp.Remove(fanId);

            if (_dryRun)
            {
                actions.Add(FanAction.Manual(fanId, pwm));
                continue;
            }
            try
            {
                _fans.SetPwm(new FanId(fanId), pwm);
                _driven.Add(fanId);
                actions.Add(FanAction.Manual(fanId, pwm));
            }
            catch (Exception ex)
            {
                actions.Add(FanAction.Failed(fanId, ex.Message));
            }
        }

        // Lüfter, die seit dem letzten Tick die manuelle Steuerung verlassen haben → Hysterese verwerfen,
        // damit die Kurve sie sofort wieder übernimmt. A fan without a config entry has no curve to take over and
        // step 3 never visits it: put it back on firmware auto here, or it would keep its last manual PWM. If that
        // fails it stays tracked and is retried next tick.
        var tracked = new HashSet<string>(manual.Keys);
        foreach (string fanId in _previouslyManual)
        {
            if (manual.ContainsKey(fanId))
                continue;
            _lastAppliedTemp.Remove(fanId);
            if (!suspended.ContainsKey(fanId) && !HasConfigEntry(config, fanId)
                && !FallBackToAuto(fanId, "manuell beendet, ohne Konfiguration → Auto", actions))
                tracked.Add(fanId);
        }

        // A fan the loop drove that has lost its config entry (e.g. after a reload from file) would keep its last
        // PWM: step 3 never visits it again. Hand it back once; until that succeeds it stays in _driven and is
        // retried. Fans released from manual control above are skipped here, so none is handled twice.
        List<string>? orphans = null;
        foreach (string fanId in _driven)
        {
            if (!manual.ContainsKey(fanId) && !suspended.ContainsKey(fanId) && !_previouslyManual.Contains(fanId)
                && !HasConfigEntry(config, fanId))
                (orphans ??= new List<string>()).Add(fanId);
        }
        if (orphans is not null)
        {
            foreach (string fanId in orphans)
                FallBackToAuto(fanId, "nicht mehr in der Konfiguration → Auto", actions);
        }
        _previouslyManual = tracked;

        // Fans whose entry was removed on purpose: the orphan check above only reaches fans the loop drove since their
        // last hand-back. These get one hand-back either way, unless the tick already acted on them.
        foreach (string fanId in handBack)
        {
            if (!HasConfigEntry(config, fanId) && !actions.Any(a => a.FanId == fanId))
                FallBackToAuto(fanId, "aus der Konfiguration entfernt → Auto", actions);
        }

        // 3) Pro geregeltem Lüfter die Kurve anwenden (außer manuell/suspendiert). Jeder Lüfter ist gegen
        //    die anderen isoliert: ein unerwarteter Fehler bei einem Kanal degradiert zu FanAction.Failed
        //    für DIESEN Lüfter, statt den ganzen Tick (und damit die Regelung aller übrigen) abzureißen.
        //    curveInputs caches the smoothed input per curve for this tick - see CurveInput.
        var curveInputs = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var fan in config.Fans)
        {
            try
            {
                ApplyFanCurve(fan, config, manual, suspended, actions, curveInputs, readings, failSafe);
            }
            catch (Exception ex)
            {
                actions.Add(FanAction.Failed(fan.FanId, ex.Message));
            }
        }

        return new ControlTick(FailSafeTriggered: false, hottest, actions, FailSafe: failSafe);
    }

    /// <summary>
    /// Wendet die zugeordnete Kurve auf einen einzelnen Lüfter an (Auto-Fallback / Hysterese / Clamp / Write).
    /// Aus <see cref="Tick"/> ausgelagert, damit jeder Lüfter dort in einem eigenen try/catch isoliert läuft.
    /// </summary>
    private void ApplyFanCurve(
        FanConfig fan, AppConfig config, IReadOnlyDictionary<string, byte> manual,
        IReadOnlyDictionary<string, FanControlState> suspended, List<FanAction> actions,
        Dictionary<string, double> curveInputs, ISensorBackend readings, FailSafeState failSafe)
    {
        if (suspended.ContainsKey(fan.FanId))
            return; // already recorded with its owner in Tick
        if (manual.ContainsKey(fan.FanId))
            return; // bereits manuell gesetzt

        // Ohne Kurven-Zuordnung den Lüfter NICHT einfrieren (reines Überspringen hielte einen zuvor
        // gesetzten Manuell-/Kurven-PWM), sondern aktiv auf Hardware-Auto stellen - die Firmware regelt.
        if (fan.AssignedCurveId is null)
        {
            FallBackToAuto(fan.FanId, "ohne Kurve → Auto", actions);
            return;
        }

        var curve = config.Curves.FirstOrDefault(c => c.Id == fan.AssignedCurveId);
        if (curve is null)
        {
            // A dangling id (e.g. after a profile switch removed the curve) must behave like "no
            // curve": skipping would freeze the fan at its last written PWM. This keeps the
            // invariant in ONE place instead of in every config writer.
            FallBackToAuto(fan.FanId, $"Kurve {fan.AssignedCurveId} fehlt → Auto", actions);
            return;
        }

        // Deaktivierte Kurve: ebenfalls auf Hardware-Auto stellen statt den letzten PWM zu halten.
        if (!curve.Enabled)
        {
            FallBackToAuto(fan.FanId, "Kurve deaktiviert → Auto", actions, curve.Id);
            return;
        }

        double temp = CurveInput(curve, curveInputs, readings);
        if (double.IsNaN(temp))
        {
            string srcs = curve.SourceSensorIds.Count == 0 ? "-" : string.Join(", ", curve.SourceSensorIds);
            if (failSafe != FailSafeState.None)
                actions.Add(FanAction.Skipped(fan.FanId, FanControlState.FailSafe, $"Sensor {srcs} n/a"));
            else if (_lastAppliedTemp.ContainsKey(fan.FanId))
                actions.Add(FanAction.Skipped(fan.FanId, FanControlState.CurveHolding, $"Sensor {srcs} n/a", curve.Id));
            else
                // The curve has not driven the fan since it was last reset (manual release, profile switch, config
                // change, coordinator release): holding would keep whatever the fan has - a manual 0 included - for as
                // long as the source stays unreadable, unnoticed while other sensors keep the watchdog seeing.
                FallBackToAuto(fan.FanId, $"Sensor {srcs} n/a → Auto", actions, curve.Id);
            return;
        }

        // Hysterese: kleine Schwankungen ignorieren, um Pendeln zu vermeiden.
        if (_lastAppliedTemp.TryGetValue(fan.FanId, out double last)
            && Math.Abs(temp - last) < curve.HysteresisC)
        {
            actions.Add(FanAction.Held(fan.FanId, curve.Id, temp, LastPwm(fan)));
            return;
        }

        double percent = CurveEngine.Evaluate(new Curve(curve.Name, curve.Points, curve.InterpolationMode), temp);
        byte pwm = ClampToFan(CurveEngine.PercentToPwm(percent), fan);

        if (_dryRun)
        {
            _lastAppliedTemp[fan.FanId] = temp;
            actions.Add(FanAction.DryRun(fan.FanId, curve.Id, temp, pwm));
            return;
        }

        try
        {
            _fans.SetPwm(new FanId(fan.FanId), pwm);
            _driven.Add(fan.FanId);
            _lastAppliedTemp[fan.FanId] = temp;
            actions.Add(FanAction.Applied(fan.FanId, curve.Id, temp, pwm));
        }
        catch (Exception ex)
        {
            actions.Add(FanAction.Failed(fan.FanId, ex.Message, curve.Id));
        }
    }

    /// <summary>
    /// The temperature a curve is evaluated at: its sources aggregated, then smoothed over the curve's
    /// window. Computed once per curve per tick and cached in <paramref name="cache"/> - several fans may
    /// share a curve, and the smoother must not be fed twice per tick (nor may the fans disagree about the
    /// value in the same tick).
    /// <para>
    /// An unreadable input (<see cref="double.NaN"/>) never reaches the smoother and is handed straight
    /// back: the caller then skips the fan exactly as before, instead of regulating on what is left in the
    /// buffer. "Temperature unknown" must not be smoothed into "temperature fine".
    /// </para>
    /// <para>
    /// A curve whose fans are all manual or suspended is not evaluated at all, so its sample series thins
    /// out for that stretch and the next mean leans on what is left. Accepted: sample ageing bounds it to
    /// one window, and feeding curves nobody is regulating would mean reading sensors for nothing.
    /// </para>
    /// </summary>
    private double CurveInput(CurveConfig curve, Dictionary<string, double> cache, ISensorBackend readings)
    {
        if (cache.TryGetValue(curve.Id, out double cached))
            return cached;

        double raw = SensorAggregator.Aggregate(curve.SourceSensorIds, readings, curve.Aggregation);
        double value = double.IsNaN(raw) ? raw : _smoother.Smooth(curve.Id, raw, curve.SmoothingSeconds);
        cache[curve.Id] = value;
        return value;
    }

    /// <summary>
    /// Remembers each temperature sensor's last readable value and returns a sensor that is unreadable now although it
    /// last read within <see cref="LostSensorMarginC"/> of <paramref name="limit"/>, or <c>null</c>. The value is kept
    /// while the sensor is unreadable, so the loss keeps counting until it reads again.
    /// </summary>
    private string? LostNearLimitSensor(ISensorBackend readings, double limit)
    {
        IReadOnlyList<SensorDescriptor> sensors;
        try { sensors = readings.DiscoverSensors(); }
        catch { return null; } // the watchdog already sees no temperature at all then

        string? lost = null;
        foreach (SensorDescriptor sensor in sensors)
        {
            if (sensor.Kind != SensorKind.Temperature)
                continue;
            string id = sensor.Id.Value;
            double value;
            try { value = readings.ReadValue(sensor.Id); }
            catch { value = double.NaN; }

            if (!double.IsNaN(value))
                _lastReadable[id] = value;
            else if (_lastReadable.TryGetValue(id, out double last) && last >= limit - LostSensorMarginC)
                lost ??= id;
        }
        return lost;
    }

    private ControlTick FailSafe(double hottest, FailSafeState cause, string reason)
    {
        _heldFailSafe = cause;
        if (!_dryRun) // a dry run writes nothing - the fans are whoever else's, a live daemon's included
            _fans.RestoreDefaults();
        _driven.Clear(); // RestoreDefaults has handed every fan back
        _lastAppliedTemp.Clear();
        _smoother.Reset(); // wie jeder „vergiss den Regelzustand"-Pfad: nach dem Fail-Safe nicht über Werte von davor mitteln
        _blindTicks = 0;
        lock (_gate)
            _manualOverride.Clear(); // nach Fail-Safe nicht automatisch in den Manual-Zustand zurück
        return new ControlTick(FailSafeTriggered: true, hottest, Array.Empty<FanAction>(), reason, cause);
    }

    /// <summary>
    /// Stellt einen Lüfter aktiv auf Hardware-Auto (Firmware regelt) und verwirft seinen Hysterese-Cache,
    /// damit eine spätere (Wieder-)Zuordnung sofort greift. Idempotent je Tick (selbstheilend). Gemeinsam
    /// genutzt für „ohne Kurve" und „Kurve deaktiviert" - beide dürfen den Lüfter nicht eingefroren lassen.
    /// <paramref name="curveId"/> names the assigned curve that cannot drive the fan (switched off, or no readable
    /// source), so the snapshot can tell it from "no curve".
    /// A channel without an auto mode (<see cref="NotSupportedException"/>; no <c>pwmN_enable</c>, e.g. dell_smm_hwmon)
    /// is handed back the way <see cref="IFanController.RestoreDefaults"/> does it - full speed - once, and only if
    /// the loop drove it. After any other failure to set auto the fan gets full speed too, but stays tracked, so
    /// auto is retried next tick. Returns <c>false</c> while the fan is not handed back yet.
    /// </summary>
    private bool FallBackToAuto(string fanId, string reason, List<FanAction> actions, string? curveId = null)
    {
        _lastAppliedTemp.Remove(fanId);

        // Read-only-Kanäle (kein Auto-Modus, z. B. GPU-Tacho) jeden Tick auf Auto stellen zu wollen würfe
        // pro Tick eine Backend-Exception → Failed → warn-Flut. „Nicht steuerbar" ist ein regulärer Zustand:
        // still überspringen statt vergeblich zu schreiben.
        if (!CanControlSafe(fanId))
        {
            actions.Add(FanAction.Skipped(fanId, FanControlState.NotControllable, "read-only"));
            return true;
        }
        if (_dryRun)
        {
            actions.Add(FanAction.Skipped(fanId, FanControlState.HardwareAuto, reason, curveId));
            return true;
        }
        var id = new FanId(fanId);
        try
        {
            _fans.SetMode(id, FanMode.Auto);
            _driven.Remove(fanId);
            actions.Add(FanAction.Skipped(fanId, FanControlState.HardwareAuto, reason, curveId));
            return true;
        }
        catch (Exception modeError)
        {
            // Nothing to hand back: the loop has not written the fan since it was last handed back - by this method or by
            // a RestoreDefaults (daemon start, fail-safe, coordinators), which on such a channel already set full speed.
            // Writing it again every tick would only fight whatever controls it now.
            if (!_driven.Contains(fanId))
            {
                actions.Add(FanAction.Skipped(
                    fanId, FanControlState.HardwareAuto, $"{reason} ({modeError.Message})", curveId));
                return true;
            }

            // Driven by the loop: left alone it would keep its last PWM - after a manual release possibly 0.
            bool noAutoMode = modeError is NotSupportedException;
            try
            {
                _fans.SetPwm(id, byte.MaxValue);
                if (noAutoMode)
                    _driven.Remove(fanId); // for good; a transient failure keeps it tracked and retries auto
                actions.Add(FanAction.FullSpeed(fanId, modeError.Message, curveId));
                return noAutoMode;
            }
            catch (Exception ex)
            {
                actions.Add(FanAction.Failed(fanId, ex.Message));
                return false;
            }
        }
    }

    private static bool HasConfigEntry(AppConfig config, string fanId)
    {
        foreach (FanConfig fan in config.Fans)
        {
            if (fan.FanId == fanId)
                return true;
        }
        return false;
    }

    private bool CanControlSafe(string fanId)
    {
        try { return _fans.CanControl(new FanId(fanId)); }
        catch { return false; } // unbekannter/fehlerhafter Kanal → nicht steuerbar
    }

    private static byte ClampToFan(byte pwm, FanConfig fan)
    {
        byte min = fan.MinPwm;
        byte max = fan.MaxPwm >= min ? fan.MaxPwm : min;
        return (byte)Math.Clamp(pwm, min, max);
    }

    private byte LastPwm(FanConfig fan)
    {
        try { return _fans.GetPwm(new FanId(fan.FanId)); }
        catch { return 0; }
    }

    /// <summary>
    /// One reading per sensor and tick, shared by the watchdog and every curve that uses the sensor. A hwmon read
    /// can take a millisecond, and curves may list many sources - several curves often the same ones - so reading
    /// per use let a large config stretch the tick, and with it the watchdog's cadence.
    /// </summary>
    private sealed class TickReadings : ISensorBackend
    {
        private readonly ISensorBackend _inner;
        private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

        public TickReadings(ISensorBackend inner) => _inner = inner;

        public IReadOnlyList<SensorDescriptor> DiscoverSensors() => _inner.DiscoverSensors();

        public double ReadValue(SensorId id)
        {
            // A throw (an id the backend does not know) is not cached - it costs no hardware access anyway.
            if (!_values.TryGetValue(id.Value, out double value))
                _values[id.Value] = value = _inner.ReadValue(id);
            return value;
        }

        public void Dispose() { }
    }
}
