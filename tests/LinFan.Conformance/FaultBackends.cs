// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Conformance;

/// <summary>
/// Kleine, fokussierte Fehler-Injektoren für die Negativ-/Risiko-Tests der Conformance-Suite. Sie
/// beweisen, dass die Suite Vertragsverletzungen tatsächlich <b>fängt</b> (statt sie nur zu beschreiben).
/// Bewusst keine generische Fault-Framework-Maschinerie — jeder Typ verletzt genau eine Garantie.
/// </summary>
public static class FaultBackends
{
    /// <summary>
    /// Konstruiert ein Referenz-Backend mit zwei steuerbaren Kanälen, von denen einer beim Schreiben wirft —
    /// die Basis für INV-2 (best-effort über mehrere Kanäle, ein Write-Fehler stoppt die übrigen nicht).
    /// </summary>
    public static (ISensorBackend Sensors, WriteFailingFanController Fans) WithOneFailingWrite()
    {
        var reference = new ConformanceReferenceBackend();
        reference.AddFan("ok", canControl: true);
        reference.AddFan("broken", canControl: true);
        var fans = new WriteFailingFanController(reference, failingId: new FanId("broken"));
        return (reference, fans);
    }
}

/// <summary>
/// Dekoriert einen Fan-Controller so, dass genau ein Kanal bei jedem <b>Schreib</b>zugriff
/// (<see cref="SetMode"/>/<see cref="SetPwm"/>) wirft — als ob die Hardware/sysfs-Datei für diesen Kanal
/// gerade EIO liefert. <see cref="RestoreDefaults"/> selbst bleibt vertragstreu (best-effort, kein Throw):
/// Der Decorator schluckt den Fehler des kaputten Kanals genauso, wie es ein echtes Backend täte.
/// </summary>
public sealed class WriteFailingFanController : IFanController
{
    private readonly IFanController _inner;
    private readonly FanId _failingId;

    public WriteFailingFanController(IFanController inner, FanId failingId)
    {
        _inner = inner;
        _failingId = failingId;
    }

    public IReadOnlyList<FanDescriptor> DiscoverFans() => _inner.DiscoverFans();
    public bool CanControl(FanId id) => _inner.CanControl(id);
    public FanMode GetMode(FanId id) => _inner.GetMode(id);
    public byte GetPwm(FanId id) => _inner.GetPwm(id);

    public void SetMode(FanId id, FanMode mode)
    {
        if (id == _failingId) throw new IOException($"injizierter Write-Fehler für {id}");
        _inner.SetMode(id, mode);
    }

    public void SetPwm(FanId id, byte value)
    {
        if (id == _failingId) throw new IOException($"injizierter Write-Fehler für {id}");
        _inner.SetPwm(id, value);
    }

    public void RestoreDefaults()
    {
        // Best-effort: jeden Kanal versuchen, Fehler je Kanal schlucken — der gesunde Kanal landet sicher.
        foreach (var fan in _inner.DiscoverFans())
        {
            if (!fan.CanControl) continue;
            try { _inner.SetMode(fan.Id, FanMode.Auto); }
            catch { /* dieser Kanal nicht erreichbar — übrige nicht blockieren */ }
        }
    }

    public void Dispose()
    {
        try { RestoreDefaults(); } catch { /* Shutdown darf nie werfen */ }
        _inner.Dispose();
    }
}

/// <summary>
/// Absichtlich VERTRAGSWIDRIGES Backend: <see cref="RestoreDefaults"/> stellt den bei Discovery erfassten
/// Zustand wieder her statt den kühlungs-sicheren — exakt der gefährliche Fall, vor dem der Vertrag warnt.
/// Existiert nur, um zu beweisen, dass INV-1 ihn reißt (Negativ-Test).
/// </summary>
public sealed class DiscoveryStateRestoreBackend : IFanController
{
    private readonly object _gate = new();
    private readonly Dictionary<FanId, FanDescriptor> _fans = new();
    private readonly Dictionary<FanId, (FanMode Mode, byte Pwm)> _discoveryState = new();
    private readonly Dictionary<FanId, (FanMode Mode, byte Pwm)> _current = new();

    public void AddFan(string id, byte initialPwm, FanMode initialMode = FanMode.Manual)
    {
        var fid = new FanId(id);
        lock (_gate)
        {
            _fans[fid] = new FanDescriptor(fid, id, CanControl: true, Tachometer: null, Source: id);
            _discoveryState[fid] = (initialMode, initialPwm); // „Ausgangszustand" — hier bewusst niedrig
            _current[fid] = (initialMode, initialPwm);
        }
    }

    public IReadOnlyList<FanDescriptor> DiscoverFans()
    {
        lock (_gate) return _fans.Values.ToArray();
    }

    public bool CanControl(FanId id) => true;
    public FanMode GetMode(FanId id) { lock (_gate) return _current[id].Mode; }
    public byte GetPwm(FanId id) { lock (_gate) return _current[id].Pwm; }
    public void SetMode(FanId id, FanMode mode) { lock (_gate) _current[id] = (mode, _current[id].Pwm); }

    public void SetPwm(FanId id, byte value)
    {
        lock (_gate) _current[id] = (FanMode.Manual, value);
    }

    public void RestoreDefaults()
    {
        // FALSCH (Negativ-Test): zurück auf den Discovery-Zustand, der niedrig/Manual sein kann.
        lock (_gate)
            foreach (var (id, state) in _discoveryState)
                _current[id] = state;
    }

    public void Dispose() => RestoreDefaults();
}

/// <summary>
/// Absichtlich NICHT thread-sicheres Backend: <see cref="ReadValue"/> und die Fan-Writes teilen sich einen
/// ungeschützten <see cref="List{T}"/>. <see cref="ReadValue"/> mutiert ihn (add/remove), während
/// <see cref="SetPwm"/>/<see cref="RestoreDefaults"/> ihn ohne Lock per <c>foreach</c> durchlaufen — unter
/// Nebenläufigkeit reißt das verlässlich mit <see cref="InvalidOperationException"/> („Collection was modified").
/// <para>
/// Modelliert genau das Windows-Risiko, vor dem INV-9 warnt: <c>ReadValue</c> läuft NICHT durch das Fan-Lock
/// (der <c>SynchronizedFanController</c> schützt nur die Fan-Writes), muss also für sich nebenläufig zu den
/// Writes sicher sein. Existiert nur, um zu beweisen, dass INV-9 ein racy Backend tatsächlich fängt (Negativ-Test).
/// </para>
/// </summary>
public sealed class RacyFanController : ISensorBackend, IFanController
{
    // BEWUSST OHNE Lock geteilt zwischen Lese- (ReadValue) und Schreibpfad (SetPwm/RestoreDefaults).
    private readonly List<int> _shared = new() { 0 };
    private readonly SensorId _sensorId = new("racy/temp");
    private readonly FanId _fanId = new("racy/fan");

    public IReadOnlyList<SensorDescriptor> DiscoverSensors() =>
        new[] { new SensorDescriptor(_sensorId, "racy", SensorKind.Temperature, "°C", "racy/temp") };

    public double ReadValue(SensorId id)
    {
        // Mutiert die geteilte Liste — kollidiert mit dem foreach im Schreibpfad.
        _shared.Add(_shared.Count);
        if (_shared.Count > 1)
            _shared.RemoveAt(_shared.Count - 1);
        return 42.0;
    }

    public IReadOnlyList<FanDescriptor> DiscoverFans() =>
        new[] { new FanDescriptor(_fanId, "racy", CanControl: true, Tachometer: null, Source: "racy/fan") };

    public bool CanControl(FanId id) => true;
    public FanMode GetMode(FanId id) => FanMode.Auto;
    public byte GetPwm(FanId id) => 0;
    public void SetMode(FanId id, FanMode mode) { }

    public void SetPwm(FanId id, byte value)
    {
        // Iteriert die geteilte Liste ohne Lock — wirft, sobald ReadValue parallel mutiert.
        int sum = 0;
        foreach (var x in _shared) sum += x;
        _ = sum;
    }

    public void RestoreDefaults()
    {
        int sum = 0;
        foreach (var x in _shared) sum += x;
        _ = sum;
    }

    public void Dispose() { }
}

/// <summary>
/// Backend, dessen Aufrufe künstlich blockieren (über <see cref="Latency"/>) — modelliert ein langsames
/// natives API (z. B. ein blockierender SMC-/LHM-Call). Beweist, dass die Latenz-Schranke (INV-7) ein
/// blockierendes Backend als Fehler erkennt. Verzögert nur die Aufrufe, die der Latenz-Test misst.
/// </summary>
public sealed class SlowBackend : ISensorBackend, IFanController
{
    private readonly ConformanceReferenceBackend _inner = new();

    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    public SlowBackend()
    {
        _inner.AddSensor("t", SensorKind.Temperature, 42.0);
        _inner.AddFan("f", canControl: true);
    }

    private void Stall()
    {
        if (Latency <= TimeSpan.Zero) return;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Latency) { /* busy-wait: blockiert den Tick-Thread, wie ein hängender HW-Call */ }
    }

    public IReadOnlyList<SensorDescriptor> DiscoverSensors() { Stall(); return _inner.DiscoverSensors(); }
    public double ReadValue(SensorId id) { Stall(); return _inner.ReadValue(id); }
    public IReadOnlyList<FanDescriptor> DiscoverFans() { Stall(); return _inner.DiscoverFans(); }
    public bool CanControl(FanId id) { Stall(); return _inner.CanControl(id); }
    public FanMode GetMode(FanId id) { Stall(); return _inner.GetMode(id); }
    public void SetMode(FanId id, FanMode mode) { Stall(); _inner.SetMode(id, mode); }
    public byte GetPwm(FanId id) { Stall(); return _inner.GetPwm(id); }
    public void SetPwm(FanId id, byte value) { Stall(); _inner.SetPwm(id, value); }
    public void RestoreDefaults() { Stall(); _inner.RestoreDefaults(); }
    public void Dispose() => _inner.Dispose();
}
