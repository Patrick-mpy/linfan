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
/// bei doppeltem Chip kollisionsfrei - anders als die Linux-<c>hwmonN</c>-Nummerierung.
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

    /// <summary>
    /// Warning for untrusted defaults (see <see cref="_untrustedDefaults"/>). User-facing, so German like
    /// <see cref="GpuOnlyWarning"/>. Fast Startup hibernates the kernel instead of shutting down and may not count
    /// as a new boot, hence the explicit "Neustart". Deleting the markers is only safe with no LinFan process
    /// running: a running one would lose its own protection.
    /// </summary>
    private const string UntrustedDefaultsWarning =
        "Ein anderer LinFan-Lauf hat in dieser Windows-Sitzung Lüfter gesteuert und nicht sauber zurückgegeben "
        + "(Absturz, harter Abbruch oder gleichzeitiger zweiter Prozess). Bis zum nächsten Neustart des Rechners "
        + "(Neustart, nicht Herunterfahren mit Schnellstart) gehen die Lüfter bei jeder Rückgabe (Start, "
        + "Übertemperatur, Beenden) auf 100 % statt auf Firmware-Auto, weil die gespeicherten Ausgangswerte nicht "
        + "vertrauenswürdig sind.";

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

    /// <summary>Crash marker of this process (<c>null</c> = feature off) - see <see cref="LhmRestoreMarker"/>.</summary>
    private readonly LhmRestoreMarker? _marker;

    /// <summary>
    /// Another LinFan process of this boot has (or had) its hands on the fans, so LHM's saved "default" may be its
    /// manual value: every restore skips <c>SetDefault()</c> and forces 100 %, and the LHM computer is never closed
    /// (its <c>Close()</c> writes that saved state back). Decided when this process first touches the hardware,
    /// re-checked after each software write; once set it stays for the lifetime of the process. Guarded by
    /// <see cref="_gate"/>.
    /// </summary>
    private volatile bool _untrustedDefaults; // also read without the gate (skip a needless scan)

    /// <summary>This process has touched the hardware (write or restore) with the marker feature on.</summary>
    private bool _armed;

    // LHM saves a channel's register state at its first SetSoftware and again at the first one after each
    // SetDefault. Only such a write needs the marker re-scan (a foreign writer may have gone before the save), so
    // steady curve writes never touch the file system under _gate. Both sets are guarded by _gate.
    /// <summary>Channels whose next software write makes LHM save the register state (none yet / after SetDefault).</summary>
    private readonly HashSet<FanId> _resaveOnNextWrite = new();

    /// <summary>
    /// Channels whose SetDefault() was ineffective or threw during a restore, so they got the 100 % fallback - the
    /// write at which LHM saved their "default", which therefore is no verified firmware state. Restores and
    /// SetMode(Auto) keep them on 100 % until the next software write on the channel (which also re-checks the
    /// markers) makes SetDefault() trusted again.
    /// </summary>
    private readonly HashSet<FanId> _fullSpeedUntilNextWrite = new();

    /// <summary>Runtime warning to raise once the caller has left <see cref="_gate"/>.</summary>
    private string? _pendingRuntimeWarning;

    /// <inheritdoc/>
    public string? StartupWarning { get; private set; }

    /// <inheritdoc/>
    public event Action<string>? RuntimeWarning;

    /// <summary>Realer Ctor: nur auf Windows zulässig (Kernel-Treiber); erzeugt den LHM-Adapter.</summary>
    /// <param name="restoreMarkerDirectory">
    /// Folder for the per-process crash markers; <c>null</c> disables the feature, e.g. for a dry-run instance,
    /// which must neither judge nor touch the markers of a live daemon.
    /// </param>
    public WindowsLhmBackend(string? restoreMarkerDirectory = null)
        : this(
            CreateRealComputerGuarded(),
            restoreMarkerDirectory is null ? null : LhmRestoreMarker.ForCurrentProcess(restoreMarkerDirectory))
    {
    }

    /// <summary>
    /// Test-/Inject-Ctor: nimmt eine beliebige <see cref="ILhmComputer"/>-Naht. Bewusst plattformneutral -
    /// berührt weder <c>new Computer()</c> noch einen Windows-Guard, damit die Conformance-Tests auf Linux laufen.
    /// </summary>
    internal WindowsLhmBackend(ILhmComputer lhm, LhmRestoreMarker? marker = null)
    {
        _lhm = lhm;
        _marker = marker;
        lock (_gate)
        {
            _lhm.Open();
            _lhm.Update();      // ein initialer Sweep, damit Discovery echte Sensor-Typen/Controls sieht
            Scan();
            foreach (var fan in _fans.Values)
            {
                if (fan.CanControl)
                    _resaveOnNextWrite.Add(fan.Id);
            }
        }

        // Read-only look, so the daemon's start log and the CLI already name a crashed predecessor. Trust itself is
        // decided only when this process touches the hardware (ArmLocked) - a read-only process stays out of it.
        if (marker?.AnyCrashedOfThisBoot() == true)
            AppendUntrustedWarning();
    }

    private string UntrustedWarningText() =>
        UntrustedDefaultsWarning
        + " Erscheint dieser Hinweis auch nach einem Neustart noch: den LinFan-Dienst und alle LinFan-Prozesse "
        + $"beenden, dann die Dateien lhm-restore.*.marker in {Path.GetDirectoryName(_marker!.OwnPath)} löschen.";

    private void AppendUntrustedWarning()
    {
        if (StartupWarning?.Contains(UntrustedDefaultsWarning, StringComparison.Ordinal) == true)
            return;
        string warning = UntrustedWarningText();
        StartupWarning = StartupWarning is null ? warning : StartupWarning + " " + warning;
    }

    /// <summary>Raises a runtime warning queued under <see cref="_gate"/> - outside it, so a slow log sink never
    /// holds up the fan control. Never throws.</summary>
    private void RaisePendingRuntimeWarning()
    {
        if (Interlocked.Exchange(ref _pendingRuntimeWarning, null) is not { } warning)
            return;
        try { RuntimeWarning?.Invoke(warning); }
        catch { /* a failing log sink must not break a write or a restore */ }
    }

    /// <summary>
    /// A software write (caller holds <see cref="_gate"/>, marker armed). Re-scans for foreign markers right after
    /// the writes at which LHM saves the register state - also if the write threw, LHM may have saved before.
    /// </summary>
    private void WriteSoftwareLocked(FanId id, ILhmControl control, float percent)
    {
        bool lhmSaves = _resaveOnNextWrite.Remove(id) | _fullSpeedUntilNextWrite.Remove(id);
        try { control.SetSoftware(percent); }
        finally
        {
            if (lhmSaves)
                CheckForeignMarkersLocked();
        }
    }

    /// <summary>
    /// Before this process touches the hardware (caller holds <see cref="_gate"/>): create its own marker, THEN
    /// look for other markers of this boot (create-then-scan, see <see cref="LhmRestoreMarker"/>). A software
    /// write (<paramref name="forWrite"/>) is refused with <see cref="IOException"/> if the marker cannot be
    /// written; a restore proceeds regardless (the safe direction), with trust judged from the scan alone.
    /// </summary>
    private void ArmLocked(bool forWrite)
    {
        if (_disposed)
        {
            // Dispose has handed every channel back (and a clean one removed the marker): a later write would run
            // with nothing left to undo it.
            if (forWrite)
                throw new ObjectDisposedException(nameof(WindowsLhmBackend));
            return;
        }
        if (_marker is null)
            return;

        if (!_marker.Owned)
        {
            try
            {
                _marker.EnsureOwn();
            }
            catch when (!forWrite)
            {
                // A restore proceeds without a marker, but this process's saves are then unprotected: trust none.
                BecomeUntrustedLocked();
            }
        }

        if (!_armed)
            CheckForeignMarkersLocked();
        _armed = true;
    }

    /// <summary>
    /// At arming and after each write at which LHM saved the register state: a marker of another process that
    /// shows up now may mean that process wrote before the save - it was created before its first write - so the
    /// saved "default" is no longer trusted. The switch is queued as a runtime warning (see
    /// <see cref="RaisePendingRuntimeWarning"/>).
    /// </summary>
    private void CheckForeignMarkersLocked()
    {
        if (_marker is null || _untrustedDefaults)
            return;
        if (_marker.AnyOfThisBoot(deleteOtherBoots: true))
            BecomeUntrustedLocked();
    }

    /// <summary>Switches to untrusted defaults for good and queues the runtime warning (caller holds <see cref="_gate"/>).</summary>
    private void BecomeUntrustedLocked()
    {
        if (_marker is null || _untrustedDefaults)
            return;
        _untrustedDefaults = true;
        AppendUntrustedWarning();
        _pendingRuntimeWarning = UntrustedWarningText();
    }

    /// <summary>
    /// Read-only marker check AFTER a restore pass, OUTSIDE <see cref="_gate"/>: the fail-safe has already written
    /// and never waits on file I/O. Needed on every restore: another writer (e.g. a hard-killed elevated CLI) may
    /// have set a fan this process never writes - LHM's SetDefault() on it is a no-op that still reads as verified.
    /// If such a marker exists, <see cref="RestoreAgainUntrustedLocked"/> follows with 100 %.
    /// </summary>
    private bool ForeignMarkerAfterRestore() =>
        _marker is not null && !_untrustedDefaults && _marker.AnyOfThisBoot(deleteOtherBoots: false);

    /// <summary>Second restore pass after <see cref="ForeignMarkerAfterRestore"/> found a marker: 100 % for every
    /// controllable channel. Caller holds <see cref="_gate"/>.</summary>
    private void RestoreAgainUntrustedLocked()
    {
        BecomeUntrustedLocked();
        RestoreDefaultsLocked();
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
            return FanMode.Auto; // read-only Kanal: kein Steuermodus

        lock (_gate)
        {
            try
            {
                return control.Mode == LhmControlMode.Software ? FanMode.Manual : FanMode.Auto;
            }
            catch (Exception ex)
            {
                throw new IOException($"Steuermodus von {id} nicht lesbar.", ex); // contract: unreadable throws, never a guess
            }
        }
    }

    public void SetMode(FanId id, FanMode mode)
    {
        var fan = Fan(id);
        if (fan.Sensor.Control is not { } control)
            throw new NotSupportedException($"Lüfter {id} ist nicht steuerbar.");

        try
        {
            lock (_gate)
            {
                if (mode == FanMode.Auto)
                {
                    ArmLocked(forWrite: false);
                    if (_untrustedDefaults || _fullSpeedUntilNextWrite.Contains(id))
                    {
                        // SetDefault() would return to a crashed predecessor's manual value. The channel behaves like
                        // one without an auto mode instead: full speed (written only if it is not there already - the
                        // loop asks every tick) and NotSupportedException, which callers handle as "no auto mode".
                        if (control.Mode != LhmControlMode.Software || control.SoftwareValue < 100f)
                            control.SetSoftware(100);
                        throw new NotSupportedException(
                            $"Firmware-Auto für {id} ist bis zum Neustart nicht vertrauenswürdig - Lüfter auf 100 % gesetzt.");
                    }
                    _resaveOnNextWrite.Add(id);
                    control.SetDefault();
                }
                else
                {
                    ArmLocked(forWrite: true);
                    // LHM kennt kein wertfreies „Manual": Software-Modus existiert nur MIT Stellwert. Wir halten
                    // daher den aktuellen Stellwert und schalten so auf Software - der Wert ändert sich nicht.
                    WriteSoftwareLocked(id, control, control.SoftwareValue);
                }
            }
        }
        finally
        {
            RaisePendingRuntimeWarning();
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
        try
        {
            lock (_gate)
            {
                ArmLocked(forWrite: true);
                WriteSoftwareLocked(id, control, ToPercent(value));
            }
        }
        finally
        {
            RaisePendingRuntimeWarning();
        }
    }

    public void RestoreDefaults()
    {
        // Der sichere Zustand ist Firmware-Auto über SetDefault() (LHM regelt nicht weiter, die Hardware
        // übernimmt). Best-effort über alle steuerbaren Kanäle; ein scheiternder Kanal stoppt die übrigen
        // nicht, der Aufruf wirft nie und ist nach Dispose wiederholbar. Bleibt SetDefault wirkungslos oder
        // wirft es, greift ein Volllast-Fallback (analog zum Linux-255-Fallback) - siehe RestoreFanToSafeLocked.
        // Restore first with the current trust - while trusted, SetDefault() only writes back clean saves, and on a
        // never-saved channel it is a no-op that worsens nothing - then check for foreign markers without the lock.
        lock (_gate)
        {
            ArmLocked(forWrite: false);
            RestoreDefaultsLocked();
        }
        if (ForeignMarkerAfterRestore())
        {
            lock (_gate)
                RestoreAgainUntrustedLocked();
        }
        RaisePendingRuntimeWarning(); // a restore never throws
    }

    public void Dispose()
    {
        try
        {
            bool allVerified;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true; // from here on writes are refused (ArmLocked)

                // Never touched the hardware: nothing to hand back, and LHM's Close() has nothing saved to write.
                if (_marker is not null && !_armed)
                {
                    CloseLhm();
                    return;
                }
                allVerified = RestoreDefaultsLocked();
            }

            // Same order as RestoreDefaults: hand back first, check for foreign markers without the lock, and only
            // then decide about the marker and Close().
            bool foreign = ForeignMarkerAfterRestore();
            lock (_gate)
            {
                if (foreign)
                    RestoreAgainUntrustedLocked();

                // Anything but a verified hand-back to firmware auto keeps the marker (the next start in this boot
                // stays on 100 %) AND the LHM computer open: Computer.Close() runs SetControl(i, null) on every
                // Super-I/O channel, which writes back the state LHM saved before this process's first write - after
                // a crashed predecessor its low manual value - and would undo the 100 % just set. Process exit frees
                // the driver.
                if (_untrustedDefaults || !allVerified)
                    return;

                _marker?.DeleteOwn();
                CloseLhm();
            }
        }
        finally
        {
            RaisePendingRuntimeWarning();
        }
    }

    private void CloseLhm()
    {
        try
        {
            _lhm.Dispose();
        }
        catch
        {
            // best-effort: ein scheiterndes Dispose darf den Shutdown-Pfad nicht reißen.
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

    /// <summary>Restores every channel; <c>true</c> if every controllable channel verifiably reached firmware auto.</summary>
    private bool RestoreDefaultsLocked()
    {
        bool allVerified = true;
        foreach (var fan in _fans.Values)
            allVerified &= RestoreFanToSafeLocked(fan);
        return allVerified;
    }

    /// <summary>
    /// Führt EINEN Kanal in den sicheren Zustand (Aufrufer hält <see cref="_gate"/>): erst Firmware-Auto per
    /// <c>SetDefault()</c>, dann <b>verifizieren</b>. Bleibt der Kanal danach im Software-Modus oder wirft
    /// <c>SetDefault</c> - d. h. das Board/der Treiber übernimmt die Auto-Umschaltung nicht - wird in die
    /// sichere Richtung erzwungen: Volllast (100 %, maximale Kühlung), analog zum Linux-255-Fallback. So
    /// bleibt ein Lüfter nie ungeregelt bei einem niedrigen Software-Wert hängen. Best-effort, wirft nie.
    /// With <see cref="_untrustedDefaults"/> SetDefault() is skipped: it would return to a crashed predecessor's
    /// manual value while the Mode check still passed. Returns <c>true</c> only if firmware auto was verified
    /// (or the channel is read-only).
    /// </summary>
    private bool RestoreFanToSafeLocked(ControlChannel fan)
    {
        if (fan.Sensor.Control is not { } control)
            return true; // read-only Kanal: kein Stellwert, nichts zu tun

        // A channel whose last SetDefault() already failed stays on 100 % until the next write (see
        // _fullSpeedUntilNextWrite).
        if (!_untrustedDefaults && !_fullSpeedUntilNextWrite.Contains(fan.Id))
        {
            try
            {
                _resaveOnNextWrite.Add(fan.Id);
                control.SetDefault();
                if (control.Mode != LhmControlMode.Software)
                    return true; // verifiziert: Firmware-Auto erreicht
            }
            catch
            {
                // SetDefault nicht durchgekommen → Volllast-Fallback unten
            }
        }

        // Fallback: SetDefault war wirkungslos oder hat geworfen → in die sichere Richtung (Volllast).
        if (_marker is not null && _resaveOnNextWrite.Remove(fan.Id))
            _fullSpeedUntilNextWrite.Add(fan.Id); // LHM saves its "default" at this write
        try { control.SetSoftware(100); }
        catch { /* auch der Fallback best-effort; die übrigen Kanäle nicht blockieren */ }
        return false;
    }

    /// <summary>Liest die flache Sensorliste einmal ein und baut die Sensor-/Fan-Kataloge auf.</summary>
    private void Scan()
    {
        var all = _lhm.EnumerateSensors().Where(s => !IsDistanceToTjMax(s)).ToArray();

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
            .Select(c => new SensorDescriptor(c.Id, c.Name, c.Kind, c.Unit, c.Source, DeviceOf(c.Sensor)))
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Id.Value, StringComparer.Ordinal)
            .ToArray();
        _fanDescriptors = _fans.Values
            .Select(f => new FanDescriptor(f.Id, f.Name, f.CanControl, f.Tachometer, f.Source,
                f.CanControl ? null : FanReadOnlyReason.NoControlInterface, // LHM offers no Control for the sensor
                DeviceOf(f.Sensor)))
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

    /// <summary>
    /// Intel CPUs report, next to every core temperature, a "… Distance to TjMax" sensor (LHM names it
    /// core name + " Distance to TjMax", e.g. "CPU Core #1 Distance to TjMax", "P-Core #2 Distance to TjMax").
    /// Its value is TjMax − T, so it FALLS as the core heats up. Discovered as an ordinary temperature it drags
    /// a CPU average down (fans spin too slowly) and is wrong data for every consumer, so it is never
    /// discovered. LHM offers no other discriminator - same sensor type, same <c>/intelcpu/N/temperature/M</c>
    /// identifier scheme as the real core temperatures - hence the name. A config that still references such
    /// an id just loses that source: unknown ids read as NaN in Core and are skipped.
    /// </summary>
    private static bool IsDistanceToTjMax(ILhmSensor sensor) =>
        sensor.Type == LhmSensorType.Temperature
        && sensor.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// LHM's hardware name (the product, e.g. "Nuvoton NCT6798D", "AMD Radeon RX 7800 XT") is the only fingerprint the
    /// library offers. A LibreHardwareMonitor update that renames a device would make it read as replaced.
    /// </summary>
    private static HardwareDevice DeviceOf(ILhmSensor sensor) =>
        new(sensor.HardwareName, sensor.HardwareName);

    private readonly record struct SensorChannel(
        SensorId Id, string Name, SensorKind Kind, string Unit, string Source, ILhmSensor Sensor);

    private sealed record ControlChannel(
        FanId Id, string Name, bool CanControl, SensorId? Tachometer, string Source, ILhmSensor Sensor);
}
