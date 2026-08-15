// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.Versioning;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Hardware.Windows.Lhm;

namespace LinFan.Hardware.Windows;

/// <summary>
/// Windows-Backend auf Basis von <c>LibreHardwareMonitorLib</c> (Super-I/O-Chips am Mainboard, CPU/GPU).
/// Liest Temperaturen/Drehzahlen und steuert PWM über LHMs <c>Control.SetSoftware(percent)</c>; das
/// Firmware-Auto-Ziel ist <c>Control.SetDefault()</c>.
/// <para>
/// PWM-Einheit: Der Vertrag ist byte-basiert (0..255), LHM ist prozentbasiert (0..100). Das Backend mappt
/// intern verlustbehaftet (<see cref="ToPercent"/>/<see cref="ToByte"/>) - daher hat die Conformance-Suite
/// für Windows eine kleine Round-Trip-Toleranz.
/// </para>
/// <para>
/// Thread-Sicherheit: LHM ist <b>nicht</b> thread-sicher, und <c>ReadValue</c> läuft im Daemon NICHT durch
/// das Fan-Lock (siehe <c>ISensorBackend</c>-Doc). Deshalb serialisiert ein eigenes <see cref="_gate"/>
/// JEDEN LHM-Zugriff (Sensor-Reads, Fan-Writes, der Update-Sweep) gemeinsam (INV-9).
/// </para>
/// <para>Braucht Admin-Rechte (Kernel-Treiber). Lesen wie Schreiben gehen über dieselbe Instanz.</para>
/// <para>
/// Id-Stabilität: Sensor-/Lüfter-Ids sind LHMs <c>Identifier</c> (z. B. <c>/lpc/nct6797d/0/control/1</c>
/// = Bus / Chip / Instanz-Index / Kanal). Die sind über Reboots stabil und durch den Instanz-Index auch
/// bei doppeltem Chip kollisionsfrei - anders als die Linux-<c>hwmonN</c>-Nummerierung. Deshalb
/// implementiert dieses Backend <see cref="ILegacyIdMap"/> bewusst <b>nicht</b>: es gibt keine instabilen
/// Alt-Ids zu migrieren.
/// </para>
/// </summary>
public sealed class WindowsLhmBackend : ISensorBackend, IFanController, IBackendDiagnostics
{
    /// <summary>
    /// Diagnose für den häufigsten Windows-Fehlerfall: Ein anderes Monitoring-/Lüftertool hält den
    /// Sensor-Kerneltreiber (WinRing0) exklusiv, sodass LHM nur noch die treiberfrei (per NVAPI/ADL)
    /// lesbare GPU sieht. Hedged - dieselbe „nur-GPU"-Signatur entsteht auch bei einem schlicht nicht
    /// unterstützten Super-I/O-Chip; daher ein Verdacht, keine Diagnose-Gewissheit.
    /// </summary>
    private const string GpuOnlyWarning =
        "Es wurden nur GPU-Sensoren erkannt (kein Mainboard-/CPU-Chip). Wahrscheinlich belegt ein anderes "
        + "Monitoring-/Lüftertool (z. B. Armoury Crate, FanControl, HWiNFO) den Sensortreiber exklusiv - "
        + "solche Programme beenden und den LinFan-Dienst neu starten. Gegentest: LibreHardwareMonitor als Administrator.";

    /// <summary>Serialisiert JEDEN <see cref="_lhm"/>-Zugriff (Reads, Writes, Update) - LHM ist nicht thread-sicher.</summary>
    private readonly object _gate = new();

    private readonly ILhmComputer _lhm;
    private readonly Dictionary<SensorId, SensorChannel> _sensors = new();
    private readonly Dictionary<FanId, ControlChannel> _fans = new();

    // Einmal beim Scan materialisierte, sortierte Deskriptor-Listen: die Kanal-Menge ändert sich nach dem
    // Scan nicht mehr (RefreshLocked frischt nur Werte auf), daher darf der Hot-Path (Snapshot-/Regel-Tick
    // ruft Discover* jeden Tick) sie wiederverwenden statt neu aufzubauen und zu sortieren. Unveränderlich.
    private SensorDescriptor[] _sensorDescriptors = Array.Empty<SensorDescriptor>();
    private FanDescriptor[] _fanDescriptors = Array.Empty<FanDescriptor>();

    /// <summary>Mindestabstand zwischen zwei LHM-Sweeps - ein warmer Sweep kostet (Spike: ~5 ms), kein Sweep pro Read.</summary>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(500);
    private readonly Stopwatch _sinceUpdate = Stopwatch.StartNew();

    private bool _disposed;

    /// <inheritdoc/>
    public string? StartupWarning { get; private set; }

    /// <summary>Realer Ctor: nur auf Windows zulässig (Kernel-Treiber); erzeugt den LHM-Adapter.</summary>
    public WindowsLhmBackend()
        : this(CreateRealComputerGuarded())
    {
    }

    /// <summary>
    /// Test-/Inject-Ctor: nimmt eine beliebige <see cref="ILhmComputer"/>-Naht. Bewusst plattformneutral -
    /// berührt weder <c>new Computer()</c> noch einen Windows-Guard, damit die Conformance-Tests auf Linux laufen.
    /// </summary>
    internal WindowsLhmBackend(ILhmComputer lhm)
    {
        _lhm = lhm;
        lock (_gate)
        {
            _lhm.Open();
            _lhm.Update();      // ein initialer Sweep, damit Discovery echte Sensor-Typen/Controls sieht
            Scan();
        }
    }

    [SupportedOSPlatform("windows")]
    private static ILhmComputer CreateRealComputer() => new LhmComputerAdapter();

    private static ILhmComputer CreateRealComputerGuarded()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsLhmBackend läuft nur unter Windows.");
        return CreateRealComputer();
    }

    // --- ISensorBackend -------------------------------------------------------

    public IReadOnlyList<SensorDescriptor> DiscoverSensors() => _sensorDescriptors;

    public double ReadValue(SensorId id)
    {
        if (!_sensors.TryGetValue(id, out var ch))
            throw new KeyNotFoundException($"Unbekannter Sensor: {id}");

        // Vertrag: für eine bekannte id nie werfen - ein gerade nicht lesbarer Kanal liefert NaN („kein Wert").
        lock (_gate)
        {
            try
            {
                RefreshLocked();
                return ToValue(ch.Sensor.Value);
            }
            catch
            {
                return double.NaN;
            }
        }
    }

    /// <summary>LHM-<c>Value</c> (<c>float?</c>) → Vertragswert: <c>null</c> ⇒ „kein Wert" = <see cref="double.NaN"/>.</summary>
    private static double ToValue(float? value) => value ?? double.NaN;

    // --- IFanController -------------------------------------------------------

    public IReadOnlyList<FanDescriptor> DiscoverFans() => _fanDescriptors;

    public bool CanControl(FanId id) => Fan(id).CanControl;

    public FanMode GetMode(FanId id)
    {
        var fan = Fan(id);
        if (fan.Sensor.Control is not { } control)
            return FanMode.Auto; // read-only Kanal: kein Steuermodus → sicherer Default

        // Vertrag: für eine bekannte id nie werfen - ein nicht ermittelbarer Modus fällt auf Auto zurück.
        lock (_gate)
        {
            try
            {
                return control.Mode == LhmControlMode.Software ? FanMode.Manual : FanMode.Auto;
            }
            catch
            {
                return FanMode.Auto;
            }
        }
    }

    public void SetMode(FanId id, FanMode mode)
    {
        var fan = Fan(id);
        if (fan.Sensor.Control is not { } control)
            throw new NotSupportedException($"Lüfter {id} ist nicht steuerbar.");

        lock (_gate)
        {
            if (mode == FanMode.Auto)
            {
                control.SetDefault();
            }
            else
            {
                // LHM kennt kein wertfreies „Manual": Software-Modus existiert nur MIT Stellwert. Wir halten
                // daher den aktuellen Stellwert und schalten so auf Software - der Wert ändert sich nicht.
                control.SetSoftware(control.SoftwareValue);
            }
        }
    }

    public byte GetPwm(FanId id)
    {
        var fan = Fan(id);
        if (fan.Sensor.Control is not { } control)
            return 0; // read-only Kanal: kein Stellwert lesbar → Default 0

        // Vertrag: für eine bekannte id nie werfen - ein nicht lesbarer Wert fällt auf 0 zurück.
        lock (_gate)
        {
            try
            {
                return ToByte(control.SoftwareValue);
            }
            catch
            {
                return 0;
            }
        }
    }

    public void SetPwm(FanId id, byte value)
    {
        var fan = Fan(id);
        if (fan.Sensor.Control is not { } control)
            throw new NotSupportedException($"Lüfter {id} ist nicht steuerbar.");

        // SetSoftware schaltet selbsttätig auf Software-Modus (= Manual) - kein vorheriges SetMode nötig.
        lock (_gate)
            control.SetSoftware(ToPercent(value));
    }

    public void RestoreDefaults()
    {
        // Der sichere Zustand ist Firmware-Auto über SetDefault() (LHM regelt nicht weiter, die Hardware
        // übernimmt). Best-effort über alle steuerbaren Kanäle; ein scheiternder Kanal stoppt die übrigen
        // nicht, der Aufruf wirft nie und ist nach Dispose wiederholbar. Bleibt SetDefault wirkungslos oder
        // wirft es, greift ein Volllast-Fallback (analog zum Linux-255-Fallback) - siehe RestoreFanToSafeLocked.
        lock (_gate)
            RestoreDefaultsLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            RestoreDefaultsLocked();
            try
            {
                _lhm.Dispose();
            }
            catch
            {
                // best-effort: ein scheiterndes Dispose darf den Shutdown-Pfad nicht reißen.
            }
        }
    }

    // --- PWM-Mapping (testbar) ------------------------------------------------

    /// <summary>Byte 0..255 → Prozent 0..100 (LHM-Stellwert). Verlustbehaftet - siehe Round-Trip-Toleranz.</summary>
    internal static int ToPercent(byte value) => (int)Math.Round(value * 100.0 / 255.0);

    /// <summary>Prozent 0..100 → Byte 0..255 (Vertragswert). Inverse von <see cref="ToPercent"/> (gerundet).</summary>
    internal static byte ToByte(double percent) =>
        (byte)Math.Clamp((int)Math.Round(percent * 255.0 / 100.0), 0, 255);

    // --- Discovery & I/O ------------------------------------------------------

    private ControlChannel Fan(FanId id) =>
        _fans.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Unbekannter Lüfter: {id}");

    private void RefreshLocked()
    {
        if (_sinceUpdate.Elapsed < UpdateInterval)
            return;
        _lhm.Update();
        _sinceUpdate.Restart();
    }

    private void RestoreDefaultsLocked()
    {
        foreach (var fan in _fans.Values)
            RestoreFanToSafeLocked(fan);
    }

    /// <summary>
    /// Führt EINEN Kanal in den sicheren Zustand (Aufrufer hält <see cref="_gate"/>): erst Firmware-Auto per
    /// <c>SetDefault()</c>, dann <b>verifizieren</b>. Bleibt der Kanal danach im Software-Modus oder wirft
    /// <c>SetDefault</c> - d. h. das Board/der Treiber übernimmt die Auto-Umschaltung nicht - wird in die
    /// sichere Richtung erzwungen: Volllast (100 %, maximale Kühlung), analog zum Linux-255-Fallback. So
    /// bleibt ein Lüfter nie ungeregelt bei einem niedrigen Software-Wert hängen. Best-effort, wirft nie.
    /// </summary>
    private static void RestoreFanToSafeLocked(ControlChannel fan)
    {
        if (fan.Sensor.Control is not { } control)
            return; // read-only Kanal: kein Stellwert, nichts zu tun

        try
        {
            control.SetDefault();
            if (control.Mode != LhmControlMode.Software)
                return; // verifiziert: Firmware-Auto erreicht
        }
        catch
        {
            // SetDefault nicht durchgekommen → Volllast-Fallback unten
        }

        // Fallback: SetDefault war wirkungslos oder hat geworfen → in die sichere Richtung (Volllast).
        try { control.SetSoftware(100); }
        catch { /* auch der Fallback best-effort; die übrigen Kanäle nicht blockieren */ }
    }

    /// <summary>Liest die flache Sensorliste einmal ein und baut die Sensor-/Fan-Kataloge auf.</summary>
    private void Scan()
    {
        var all = _lhm.EnumerateSensors();

        // Fan-RPM-Sensoren je Hardware vorhalten, um Controls per Namens-Index zu paaren.
        var fanSensorsByHardware = all
            .Where(s => s.Type == LhmSensorType.Fan)
            .GroupBy(s => s.HardwareName)
            .ToDictionary(g => g.Key, g => g.ToArray());

        // Erst die auslesbaren Sensoren (Temp/Fan) katalogisieren, dann die Controls - so liegen beim
        // Tachometer-Pairing alle Fan-Sensor-Ids bereits im Katalog (unabhängig von der LHM-Reihenfolge).
        foreach (var sensor in all)
        {
            switch (sensor.Type)
            {
                case LhmSensorType.Temperature:
                    AddSensor(sensor, SensorKind.Temperature, "°C");
                    break;

                case LhmSensorType.Fan:
                    AddSensor(sensor, SensorKind.FanRpm, "RPM");
                    break;
            }
        }

        foreach (var sensor in all.Where(s => s.Type == LhmSensorType.Control))
            AddFan(sensor, fanSensorsByHardware);

        // Deskriptoren einmal sortiert materialisieren (siehe Feld-Kommentar) - danach ist die Kanal-Menge fix.
        _sensorDescriptors = _sensors.Values
            .Select(c => new SensorDescriptor(c.Id, c.Name, c.Kind, c.Unit, c.Source))
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Id.Value, StringComparer.Ordinal)
            .ToArray();
        _fanDescriptors = _fans.Values
            .Select(f => new FanDescriptor(f.Id, f.Name, f.CanControl, f.Tachometer, f.Source))
            .OrderBy(d => d.Id.Value, StringComparer.Ordinal)
            .ToArray();

        DetectGpuOnly(all);
    }

    /// <summary>
    /// Setzt die Start-Warnung, wenn <b>ausschließlich</b> GPU-Kanäle gefunden wurden (typische Signatur
    /// eines Treiber-Konflikts). <c>Any() &amp;&amp; All(...)</c>: Bei komplett leerer Discovery (gar nichts
    /// gefunden) wäre <c>All</c> vakuar-wahr - das ist ein anderer Fall und darf NICHT als „nur GPU" gelten.
    /// </summary>
    private void DetectGpuOnly(IReadOnlyList<ILhmSensor> all)
    {
        var channels = all
            .Where(s => s.Type is LhmSensorType.Temperature or LhmSensorType.Fan or LhmSensorType.Control)
            .ToArray();

        bool gpuOnly = channels.Length > 0 && channels.All(s => IsGpu(s.HardwareType));
        StartupWarning = gpuOnly ? GpuOnlyWarning : null;
    }

    private static bool IsGpu(LhmHardwareType type) =>
        type is LhmHardwareType.GpuNvidia or LhmHardwareType.GpuAmd or LhmHardwareType.GpuIntel;

    private void AddSensor(ILhmSensor sensor, SensorKind kind, string unit)
    {
        var id = new SensorId(sensor.Identifier);
        string name = DisplayName(sensor);
        _sensors[id] = new SensorChannel(id, name, kind, unit, sensor.Identifier, sensor);
    }

    private void AddFan(ILhmSensor control, IReadOnlyDictionary<string, ILhmSensor[]> fanSensorsByHardware)
    {
        var id = new FanId(control.Identifier);
        bool canControl = control.Control is not null;
        SensorId? tach = MatchTachometer(control, fanSensorsByHardware);
        _fans[id] = new ControlChannel(id, DisplayName(control), canControl, tach, control.Identifier, control);
    }

    /// <summary>
    /// Paart einen Control-Sensor mit dem RPM-Sensor derselben Hardware, der denselben Namens-Index trägt
    /// (z. B. Control «Fan #2» ↔ Fan «Fan #2»). Bei Mehrdeutigkeit oder ohne eindeutigen Treffer: <c>null</c>
    /// - lieber kein Tacho als ein falsch gepaarter. Nur wenn der gepaarte Fan-Sensor auch als Sensor im
    /// Katalog liegt, wird seine Id verlinkt.
    /// </summary>
    private SensorId? MatchTachometer(
        ILhmSensor control, IReadOnlyDictionary<string, ILhmSensor[]> fanSensorsByHardware)
    {
        if (!fanSensorsByHardware.TryGetValue(control.HardwareName, out var fans))
            return null;

        int? controlIndex = TrailingIndex(control.Name);
        if (controlIndex is null)
            return null;

        var matches = fans.Where(f => TrailingIndex(f.Name) == controlIndex).ToArray();
        if (matches.Length != 1)
            return null; // 0 Treffer oder mehrdeutig → nicht raten

        var tachId = new SensorId(matches[0].Identifier);
        return _sensors.ContainsKey(tachId) ? tachId : null;
    }

    /// <summary>Letzte Ziffernfolge in einem Namen (z. B. «Fan #2» → 2, «Fan Control #3» → 3), sonst <c>null</c>.</summary>
    private static int? TrailingIndex(string name)
    {
        int end = name.Length;
        while (end > 0 && !char.IsDigit(name[end - 1]))
            end--;
        if (end == 0)
            return null;
        int start = end;
        while (start > 0 && char.IsDigit(name[start - 1]))
            start--;
        return int.TryParse(name.AsSpan(start, end - start), out int value) ? value : null;
    }

    private static string DisplayName(ILhmSensor sensor) => $"{sensor.HardwareName} {sensor.Name}";

    private readonly record struct SensorChannel(
        SensorId Id, string Name, SensorKind Kind, string Unit, string Source, ILhmSensor Sensor);

    private sealed record ControlChannel(
        FanId Id, string Name, bool CanControl, SensorId? Tachometer, string Source, ILhmSensor Sensor);
}
