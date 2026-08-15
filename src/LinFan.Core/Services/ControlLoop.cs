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
/// wird nur gerechnet, nicht geschrieben (z. B. ohne Root).
/// </para>
/// <para>
/// The smoothing sits on the curve input only. The watchdog below deliberately reads raw values, so a
/// genuine over-temperature still trips in the very tick it appears, however long the window is.
/// </para>
/// </summary>
public sealed class ControlLoop
{
    /// <summary>So viele Ticks ohne lesbare Temperatur (bei aktiver Regelung) lösen den Fail-Safe aus.</summary>
    private const int MaxBlindTicks = 3;

    private readonly ISensorBackend _sensors;
    private readonly IFanController _fans;
    private readonly bool _dryRun;
    private readonly TemperatureSmoother _smoother;
    private readonly Dictionary<string, double> _lastAppliedTemp = new();
    private int _blindTicks;

    // Laufzeit-Steuerhoheit pro Lüfter (von außen per IPC-Thread gesetzt, im Tick vom Loop-Thread
    // gelesen → über _gate synchronisiert). Manuell = fester PWM übersteuert die Kurve; suspendiert =
    // ein anderer Besitzer (Kalibrierung) steuert, der Loop fasst den Lüfter nicht an.
    private readonly object _gate = new();
    private readonly Dictionary<string, byte> _manualOverride = new();
    private readonly HashSet<string> _suspended = new();
    private HashSet<string> _previouslyManual = new();

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
    public void Suspend(string fanId)
    {
        lock (_gate)
            _suspended.Add(fanId);
    }

    /// <summary>Gibt einen suspendierten Lüfter wieder an den Loop zurück.</summary>
    public void Resume(string fanId)
    {
        lock (_gate)
            _suspended.Remove(fanId);
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
        HashSet<string> suspended;
        lock (_gate)
        {
            manual = new Dictionary<string, byte>(_manualOverride);
            suspended = new HashSet<string>(_suspended);
        }

        // 1) Watchdog hat Vorrang vor jeder Kurven-/Manual-Logik.
        double hottest = SensorAggregator.Hottest(_sensors);

        // 1a) Übertemperatur → sofort sicherer Zustand.
        if (!double.IsNaN(hottest) && hottest >= config.FailSafeTempC)
            return FailSafe(hottest, $"Übertemperatur {hottest:0.0} °C ≥ {config.FailSafeTempC:0.0} °C");

        // 1b) Wir steuern aktiv (Kurve ODER manueller Override), können aber KEINE Temperatur lesen
        //     (z. B. alle Sensoren EIO/NaN). „Temperatur unbekannt" ist nicht „alles in Ordnung":
        //     nach einigen Ticks blind in den sicheren Zustand, statt einen festen PWM ohne
        //     Überwachung zu halten. Manuell gesteuerte Lüfter zählen ausdrücklich mit.
        bool controlling = !_dryRun && (config.Fans.Any(f => f.AssignedCurveId is not null) || manual.Count > 0);
        if (double.IsNaN(hottest))
        {
            if (controlling && ++_blindTicks >= MaxBlindTicks)
                return FailSafe(hottest, $"keine lesbare Temperatur seit {_blindTicks} Ticks");
        }
        else
        {
            _blindTicks = 0;
        }

        var actions = new List<FanAction>();

        // 2a) Manuelle Overrides (GUI) übersteuern die Kurve - fester PWM (Watchdog hat oben geschützt).
        foreach (var (fanId, pwm) in manual)
        {
            if (suspended.Contains(fanId))
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
                actions.Add(FanAction.Manual(fanId, pwm));
            }
            catch (Exception ex)
            {
                actions.Add(FanAction.Failed(fanId, ex.Message));
            }
        }

        // Lüfter, die seit dem letzten Tick die manuelle Steuerung verlassen haben → Hysterese verwerfen,
        // damit die Kurve sie sofort wieder übernimmt.
        foreach (string fanId in _previouslyManual)
            if (!manual.ContainsKey(fanId))
                _lastAppliedTemp.Remove(fanId);
        _previouslyManual = new HashSet<string>(manual.Keys);

        // 3) Pro geregeltem Lüfter die Kurve anwenden (außer manuell/suspendiert). Jeder Lüfter ist gegen
        //    die anderen isoliert: ein unerwarteter Fehler bei einem Kanal degradiert zu FanAction.Failed
        //    für DIESEN Lüfter, statt den ganzen Tick (und damit die Regelung aller übrigen) abzureißen.
        //    curveInputs caches the smoothed input per curve for this tick - see CurveInput.
        var curveInputs = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var fan in config.Fans)
        {
            try
            {
                ApplyFanCurve(fan, config, manual, suspended, actions, curveInputs);
            }
            catch (Exception ex)
            {
                actions.Add(FanAction.Failed(fan.FanId, ex.Message));
            }
        }

        return new ControlTick(FailSafeTriggered: false, hottest, actions);
    }

    /// <summary>
    /// Wendet die zugeordnete Kurve auf einen einzelnen Lüfter an (Auto-Fallback / Hysterese / Clamp / Write).
    /// Aus <see cref="Tick"/> ausgelagert, damit jeder Lüfter dort in einem eigenen try/catch isoliert läuft.
    /// </summary>
    private void ApplyFanCurve(
        FanConfig fan, AppConfig config, IReadOnlyDictionary<string, byte> manual,
        IReadOnlySet<string> suspended, List<FanAction> actions, Dictionary<string, double> curveInputs)
    {
        if (suspended.Contains(fan.FanId))
        {
            actions.Add(FanAction.Skipped(fan.FanId, "kalibriert"));
            return;
        }
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
            FallBackToAuto(fan.FanId, "Kurve deaktiviert → Auto", actions);
            return;
        }

        double temp = CurveInput(curve, curveInputs);
        if (double.IsNaN(temp))
        {
            string srcs = curve.SourceSensorIds.Count == 0 ? "-" : string.Join(", ", curve.SourceSensorIds);
            actions.Add(FanAction.Skipped(fan.FanId, $"Sensor {srcs} n/a"));
            return;
        }

        // Hysterese: kleine Schwankungen ignorieren, um Pendeln zu vermeiden.
        if (_lastAppliedTemp.TryGetValue(fan.FanId, out double last)
            && Math.Abs(temp - last) < curve.HysteresisC)
        {
            actions.Add(FanAction.Held(fan.FanId, temp, LastPwm(fan)));
            return;
        }

        double percent = CurveEngine.Evaluate(new Curve(curve.Name, curve.Points, curve.InterpolationMode), temp);
        byte pwm = ClampToFan(CurveEngine.PercentToPwm(percent), fan);

        if (_dryRun)
        {
            _lastAppliedTemp[fan.FanId] = temp;
            actions.Add(FanAction.DryRun(fan.FanId, temp, pwm));
            return;
        }

        try
        {
            _fans.SetPwm(new FanId(fan.FanId), pwm);
            _lastAppliedTemp[fan.FanId] = temp;
            actions.Add(FanAction.Applied(fan.FanId, temp, pwm));
        }
        catch (Exception ex)
        {
            actions.Add(FanAction.Failed(fan.FanId, ex.Message));
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
    private double CurveInput(CurveConfig curve, Dictionary<string, double> cache)
    {
        if (cache.TryGetValue(curve.Id, out double cached))
            return cached;

        double raw = SensorAggregator.Aggregate(curve.SourceSensorIds, _sensors, curve.Aggregation);
        double value = double.IsNaN(raw) ? raw : _smoother.Smooth(curve.Id, raw, curve.SmoothingSeconds);
        cache[curve.Id] = value;
        return value;
    }

    private ControlTick FailSafe(double hottest, string reason)
    {
        _fans.RestoreDefaults();
        _lastAppliedTemp.Clear();
        _smoother.Reset(); // wie jeder „vergiss den Regelzustand"-Pfad: nach dem Fail-Safe nicht über Werte von davor mitteln
        _blindTicks = 0;
        lock (_gate)
            _manualOverride.Clear(); // nach Fail-Safe nicht automatisch in den Manual-Zustand zurück
        return new ControlTick(FailSafeTriggered: true, hottest, Array.Empty<FanAction>(), reason);
    }

    /// <summary>
    /// Stellt einen Lüfter aktiv auf Hardware-Auto (Firmware regelt) und verwirft seinen Hysterese-Cache,
    /// damit eine spätere (Wieder-)Zuordnung sofort greift. Idempotent je Tick (selbstheilend). Gemeinsam
    /// genutzt für „ohne Kurve" und „Kurve deaktiviert" - beide dürfen den Lüfter nicht eingefroren lassen.
    /// </summary>
    private void FallBackToAuto(string fanId, string reason, List<FanAction> actions)
    {
        _lastAppliedTemp.Remove(fanId);

        // Read-only-Kanäle (kein Auto-Modus, z. B. GPU-Tacho) jeden Tick auf Auto stellen zu wollen würfe
        // pro Tick eine Backend-Exception → Failed → warn-Flut. „Nicht steuerbar" ist ein regulärer Zustand:
        // still überspringen statt vergeblich zu schreiben.
        if (!CanControlSafe(fanId))
        {
            actions.Add(FanAction.Skipped(fanId, "read-only"));
            return;
        }
        if (_dryRun)
        {
            actions.Add(FanAction.Skipped(fanId, reason));
            return;
        }
        try
        {
            _fans.SetMode(new FanId(fanId), FanMode.Auto);
            actions.Add(FanAction.Skipped(fanId, reason));
        }
        catch (Exception ex)
        {
            actions.Add(FanAction.Failed(fanId, ex.Message));
        }
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
}
