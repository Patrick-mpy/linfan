// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using LinFan.Hardware.Mac.Smc;

namespace LinFan.Hardware.Mac.Tests;

/// <summary>
/// Plattformneutraler Fake der SMC-Naht (<see cref="ISmc"/>) — ein zustandsbehafteter Key→Wert-Speicher
/// statt echtem IOKit. Damit laufen die Backend-Tests (inkl. Conformance) auf JEDEM OS ohne IOKit/Root.
/// <para>
/// Verhält sich wie der reale <see cref="AppleSmc"/> gegenüber dem Vertrag: unbekannte Keys sind nicht
/// les-/schreibbar, ein Write mit abweichender Byte-Länge scheitert (wie die Firmware-Längenprüfung), und
/// der Zugriff ist <b>thread-sicher</b> (INV-9 hämmert nebenläufig Reads gegen Writes).
/// </para>
/// </summary>
internal sealed class FakeSmc : ISmc
{
    private readonly Dictionary<string, SmcValue> _store = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failWrites = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public bool Opened { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Lässt Writes auf die genannten Keys scheitern (simuliert einen transienten Firmware-Fehler).</summary>
    public FakeSmc FailWritesFor(params string[] keys)
    {
        lock (_lock) foreach (var k in keys) _failWrites.Add(k);
        return this;
    }

    public FakeSmc Set(string key, string type, byte[] data)
    {
        lock (_lock) _store[key] = new SmcValue(type, data);
        return this;
    }

    public FakeSmc SetFloat(string key, float v)
    {
        var b = BitConverter.GetBytes(v);
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        return Set(key, "flt ", b);
    }

    public FakeSmc SetUi8(string key, byte v) => Set(key, "ui8 ", new[] { v });

    public FakeSmc SetUi32BE(string key, uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return Set(key, "ui32", b);
    }

    public FakeSmc SetFpe2(string key, double rpm)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Math.Round(rpm * 4.0));
        return Set(key, "fpe2", b);
    }

    public bool Has(string key) { lock (_lock) return _store.ContainsKey(key); }

    // --- ISmc -----------------------------------------------------------------

    public void Open() => Opened = true;

    public bool TryReadKey(string key, out SmcValue value)
    {
        lock (_lock) return _store.TryGetValue(key, out value);
    }

    public bool TryWriteKey(string key, SmcValue value)
    {
        lock (_lock)
        {
            if (_failWrites.Contains(key)) return false;                       // simulierter Firmware-Fehler
            if (!_store.TryGetValue(key, out var existing)) return false;      // unbekannter Key
            if (existing.Data.Length != value.Data.Length) return false;       // Längenprüfung (wie Firmware)
            _store[key] = value;
            return true;
        }
    }

    public void Dispose() => Disposed = true;
}
