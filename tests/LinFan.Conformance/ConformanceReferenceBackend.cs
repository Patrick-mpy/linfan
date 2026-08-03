// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Conformance;

/// <summary>
/// In-Memory-Referenzimplementierung des Backend-Vertrags (<see cref="ISensorBackend"/> +
/// <see cref="IFanController"/>). Sie modelliert das vom Vertrag geforderte Verhalten korrekt und dient
/// dreifach:
/// <list type="bullet">
///   <item>als deterministisches, hardwarefreies Subjekt für <see cref="BackendConformanceTests"/> (CI),</item>
///   <item>als ausführbare Doku des Vertrags,</item>
///   <item>als Vorlage für den Autor des künftigen Windows-Backends.</item>
/// </list>
/// Bewusst minimal und thread-sicher (ein Gate über den gesamten veränderlichen Zustand), weil
/// <c>ReadValue</c> laut Vertrag nebenläufig zu Fan-Writes sicher sein muss.
/// </summary>
public sealed class ConformanceReferenceBackend : ISensorBackend, IFanController
{
    private readonly object _gate = new();
    private readonly List<SensorDescriptor> _sensors = new();
    private readonly Dictionary<SensorId, double> _values = new();
    private readonly Dictionary<FanId, FanState> _fans = new();
    private bool _disposed;

    private sealed class FanState
    {
        public required FanDescriptor Descriptor { get; init; }

        /// <summary>
        /// Ob der Kanal einen Hardware-Auto-Modus kennt. Ein Kanal ohne Auto (real: ein PWM-Knoten ohne
        /// <c>pwmN_enable</c>) fällt bei <see cref="RestoreDefaults"/> auf Volllast 255 statt auf Auto.
        /// </summary>
        public bool HasAutoMode { get; init; } = true;

        public FanMode Mode { get; set; } = FanMode.Auto;
        public byte Pwm { get; set; }
    }

    // --- Aufbau (Test-Hooks) --------------------------------------------------

    /// <summary>Fügt einen Sensor mit festem Wert hinzu. <see cref="double.NaN"/> modelliert „gerade nicht lesbar".</summary>
    public void AddSensor(string id, SensorKind kind, double value, string? name = null, string unit = "")
    {
        var sid = new SensorId(id);
        lock (_gate)
        {
            _sensors.Add(new SensorDescriptor(sid, name ?? id, kind, unit, id));
            _values[sid] = value;
        }
    }

    /// <summary>Fügt einen Lüfter hinzu. Ein steuerbarer startet in Auto (kühlungs-sicher).</summary>
    public void AddFan(string id, bool canControl, SensorId? tachometer = null, string? name = null)
    {
        var fid = new FanId(id);
        lock (_gate)
        {
            var descriptor = new FanDescriptor(fid, name ?? id, canControl, tachometer, id);
            _fans[fid] = new FanState { Descriptor = descriptor };
        }
    }

    /// <summary>
    /// Fügt einen steuerbaren Lüfter <b>ohne Hardware-Auto-Modus</b> hinzu (real: ein PWM-Knoten ohne
    /// <c>pwmN_enable</c>). Sein kühlungs-sicherer Zustand ist Volllast 255, nicht Auto — er startet daher
    /// in Manual/255 und kehrt bei <see cref="RestoreDefaults"/> dorthin zurück. Modelliert den 255-Zweig
    /// von INV-1 (<c>mode==Auto || pwm==255</c>).
    /// </summary>
    public void AddFullLoadOnlyFan(string id, SensorId? tachometer = null, string? name = null)
    {
        var fid = new FanId(id);
        lock (_gate)
        {
            var descriptor = new FanDescriptor(fid, name ?? id, CanControl: true, tachometer, id);
            _fans[fid] = new FanState { Descriptor = descriptor, HasAutoMode = false, Mode = FanMode.Manual, Pwm = 255 };
        }
    }

    // --- ISensorBackend -------------------------------------------------------

    public IReadOnlyList<SensorDescriptor> DiscoverSensors()
    {
        lock (_gate) return _sensors.ToArray();
    }

    public double ReadValue(SensorId id)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(id, out double v))
                throw new KeyNotFoundException($"Unbekannter Sensor: {id}");
            return v; // kann NaN sein — das ist „kein Wert", kein Fehler
        }
    }

    // --- IFanController -------------------------------------------------------

    public IReadOnlyList<FanDescriptor> DiscoverFans()
    {
        lock (_gate) return _fans.Values.Select(f => f.Descriptor).ToArray();
    }

    public bool CanControl(FanId id)
    {
        lock (_gate) return Fan(id).Descriptor.CanControl;
    }

    public FanMode GetMode(FanId id)
    {
        lock (_gate) return Fan(id).Mode; // bekannter Kanal: nie Throw
    }

    public void SetMode(FanId id, FanMode mode)
    {
        lock (_gate)
        {
            var fan = Fan(id);
            GuardControllable(fan);
            fan.Mode = mode;
        }
    }

    public byte GetPwm(FanId id)
    {
        lock (_gate) return Fan(id).Pwm; // bekannter Kanal: nie Throw
    }

    public void SetPwm(FanId id, byte value)
    {
        lock (_gate)
        {
            var fan = Fan(id);
            GuardControllable(fan);
            fan.Mode = FanMode.Manual; // selbsttätig auf Manual, ohne vorheriges SetMode
            fan.Pwm = value;
        }
    }

    public void RestoreDefaults()
    {
        lock (_gate)
        {
            // Best-effort über ALLE Kanäle in den kühlungs-sicheren Zustand — unabhängig vom Discovery-Zustand,
            // wirft nicht, idempotent. Steuerbare Kanäle mit Auto → Auto; ein Kanal ohne Auto-Modus fällt
            // ersatzweise auf Volllast 255 (genau der reale Linux-Fallback für Knoten ohne pwmN_enable).
            foreach (var fan in _fans.Values)
            {
                if (!fan.Descriptor.CanControl)
                    continue;

                if (fan.HasAutoMode)
                    fan.Mode = FanMode.Auto;
                else
                {
                    fan.Mode = FanMode.Manual;
                    fan.Pwm = 255;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            RestoreDefaults();
        }
    }

    private FanState Fan(FanId id) =>
        _fans.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Unbekannter Lüfter: {id}");

    private static void GuardControllable(FanState fan)
    {
        if (!fan.Descriptor.CanControl)
            throw new NotSupportedException($"Lüfter {fan.Descriptor.Id} ist nicht steuerbar.");
    }
}
