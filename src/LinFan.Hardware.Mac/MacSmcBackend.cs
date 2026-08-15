// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Hardware.Mac.Smc;

namespace LinFan.Hardware.Mac;

/// <summary>
/// macOS-Backend über IOKit/AppleSMC. Liest Temperaturen (kuratierte SMC-Keys, siehe
/// <see cref="MacTemperatureKeys"/>) und Lüfterdrehzahlen (<c>F{i}Ac</c>) und steuert - wo verfügbar -
/// Lüfter über Ziel-Drehzahl (<c>F{i}Tg</c>) und Modus (<c>F{i}Md</c>).
/// <para>
/// <b>Steuerbarkeit ist die Ausnahme, nicht die Regel:</b> Lesen geht ohne Root. Schreiben braucht Root
/// <em>und</em> passende Hardware - auf <b>Apple Silicon</b> ist SMC-Lüftersteuerung nicht verfügbar, dort
/// bleiben alle Kanäle <see cref="FanDescriptor.CanControl"/> == <c>false</c> (regulärer read-only-Zustand,
/// kein Fehler). <see cref="RestoreDefaults"/>/<see cref="Dispose"/> fahren jeden steuerbaren Kanal in den
/// sicheren Zustand (Firmware-Auto), bewusst NICHT in den bei Discovery gelesenen Zustand (Fail-Safe).
/// </para>
/// <para>
/// Die native Interop lebt vollständig hinter <see cref="ISmc"/> (real: <see cref="AppleSmc"/>); die
/// gesamte <b>Logik</b> dieses Backends (Discovery, Mapping, Fail-Safe) ist damit mit einem Fake-SMC
/// testbar (Conformance INV-1..INV-10). Plattform berühren nur der reale Konstruktions-Pfad
/// (IOKit-Adapter + Architektur-/Rechte-Erkennung in <see cref="DetectControl"/>); der injizierende
/// Ctor umgeht beides, sodass die Tests OS-unabhängig laufen.
/// </para>
/// </summary>
public sealed class MacSmcBackend : ISensorBackend, IFanController, IBackendDiagnostics
{
    private readonly ISmc _smc;

    private readonly Dictionary<SensorId, SensorChannel> _sensors = new();
    private readonly Dictionary<FanId, FanChannel> _fans = new();
    private SensorDescriptor[] _sensorDescriptors = Array.Empty<SensorDescriptor>();
    private FanDescriptor[] _fanDescriptors = Array.Empty<FanDescriptor>();

    private bool _disposed;

    /// <summary>Realer Einstieg (Daemon): öffnet den echten SMC und bestimmt die Steuerbarkeit aus Architektur + Rechten.</summary>
    public MacSmcBackend() : this(CreateRealSmc(), DetectControl()) { }

    /// <summary>Test-/DI-Einstieg: injizierter SMC und explizit gesetzte Steuer-Fähigkeit.</summary>
    internal MacSmcBackend(ISmc smc, ControlCapability control)
    {
        _smc = smc;
        _smc.Open();
        Scan(control);
    }

    /// <inheritdoc/>
    public string? StartupWarning { get; private set; }

    // --- ISensorBackend -------------------------------------------------------

    public IReadOnlyList<SensorDescriptor> DiscoverSensors() => _sensorDescriptors;

    public double ReadValue(SensorId id)
    {
        if (!_sensors.TryGetValue(id, out var ch))
            throw new KeyNotFoundException($"Unbekannter Sensor: {id}");

        if (!_smc.TryReadKey(ch.Key, out var raw))
            return double.NaN; // Kanal momentan nicht lesbar - defensiv, kein Fehler

        double v = SmcCodec.Decode(raw);
        // Drehzahl darf nicht negativ sein; ein unplausibler Rohwert gilt als „kein Wert".
        if (ch.Kind == SensorKind.FanRpm && (double.IsNaN(v) || v < 0))
            return double.NaN;
        return v;
    }

    // --- IFanController -------------------------------------------------------

    public IReadOnlyList<FanDescriptor> DiscoverFans() => _fanDescriptors;

    public bool CanControl(FanId id) => Fan(id).CanControl;

    public FanMode GetMode(FanId id)
    {
        var fan = Fan(id);
        if (fan.ModeKey is null || !_smc.TryReadKey(fan.ModeKey, out var raw))
            return FanMode.Auto; // nicht ermittelbar → sicherer Default
        return SmcCodec.Decode(raw) >= 1 ? FanMode.Manual : FanMode.Auto;
    }

    public void SetMode(FanId id, FanMode mode)
    {
        var fan = Fan(id);
        if (!fan.CanControl || fan.ModeKey is null)
            throw new NotSupportedException($"Lüfter {id} ist nicht steuerbar.");
        WriteMode(fan, mode);
    }

    public byte GetPwm(FanId id)
    {
        var fan = Fan(id);
        if (fan.TargetKey is null || !_smc.TryReadKey(fan.TargetKey, out var raw))
            return 0;
        return RpmToPwm(SmcCodec.Decode(raw), fan.MinRpm, fan.MaxRpm);
    }

    public void SetPwm(FanId id, byte value)
    {
        var fan = Fan(id);
        if (!fan.CanControl || fan.TargetKey is null || fan.TargetType is null)
            throw new NotSupportedException(
                $"Lüfter {id} ist nicht steuerbar (Apple Silicon oder ohne Root - read-only).");

        // Vor dem Schreiben Manual erzwingen, sonst überschreibt die Firmware das Ziel sofort wieder.
        WriteMode(fan, FanMode.Manual);
        TryWriteTarget(fan, PwmToRpm(value, fan.MinRpm, fan.MaxRpm));
    }

    public void RestoreDefaults()
    {
        // Sicherer Zustand ist IMMER Firmware-Auto (Md=0) - bewusst NICHT der bei Discovery gelesene
        // Zustand: der kann Manual/niedrig gewesen sein (früherer Absturz) und würde den Lüfter ohne
        // aktiven Watchdog dort festhalten. Zwei UNABHÄNGIGE Wege in einen kühlenden Zustand: primär
        // Auto (Md=0); schlägt der Write fehl oder fehlt der Modus-Key, Rückfall auf Volllast
        // (Ziel = Max-RPM), damit der sichere Endzustand keinen Single-Point-of-Failure hat.
        // Best-effort: TryWriteKey schluckt Fehler je Kanal, wirft nie.
        foreach (var fan in _fans.Values)
        {
            if (!fan.CanControl) continue;
            bool auto = fan.ModeKey is not null && WriteMode(fan, FanMode.Auto);
            if (!auto)
                TryWriteTarget(fan, fan.MaxRpm);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreDefaults();
        _smc.Dispose();
    }

    /// <summary>Setzt den Modus-Key (Md). Liefert <c>true</c>, wenn der Write tatsächlich gelang.</summary>
    private bool WriteMode(FanChannel fan, FanMode mode)
    {
        if (fan.ModeKey is null || fan.ModeType is null) return false;
        byte[]? bytes = SmcCodec.Encode(fan.ModeType, mode == FanMode.Manual ? 1 : 0);
        return bytes is not null && _smc.TryWriteKey(fan.ModeKey, new SmcValue(fan.ModeType, bytes));
    }

    /// <summary>Setzt die Ziel-Drehzahl (Tg). Liefert <c>true</c>, wenn der Write tatsächlich gelang.</summary>
    private bool TryWriteTarget(FanChannel fan, double rpm)
    {
        if (fan.TargetKey is null || fan.TargetType is null) return false;
        byte[]? bytes = SmcCodec.Encode(fan.TargetType, rpm);
        return bytes is not null && _smc.TryWriteKey(fan.TargetKey, new SmcValue(fan.TargetType, bytes));
    }

    // --- Scan -----------------------------------------------------------------

    private void Scan(ControlCapability control)
    {
        ScanFans(control);
        ScanTemperatures();

        // Temperaturen in kuratierter Gruppen-Reihenfolge (CPU → GPU → … → Sonstiges, siehe
        // MacTemperatureKeys), Lüfter-Tachos nach Lüfter-Index - deterministisch statt alphabetisch,
        // damit die GUI-Sensorliste stabil gruppiert ist.
        _sensorDescriptors = _sensors.Values
            .OrderBy(c => c.Kind)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new SensorDescriptor(c.Id, c.Name, c.Kind, c.Unit, c.Key))
            .ToArray();
        _fanDescriptors = _fans.Values
            .Select(f => new FanDescriptor(f.Id, f.Name, f.CanControl, f.Tachometer, f.Source))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();

        StartupWarning = _fans.Count == 0
            ? "Keine Lüfter über SMC gefunden."
            : control.DisabledReason; // null, wenn Steuerung verfügbar (unauffällig)
    }

    private void ScanFans(ControlCapability control)
    {
        // Lüfteranzahl über FNum; jeder Lüfter i hat einen Ist-Drehzahl-Key F{i}Ac (RPM-Sensor + Tacho).
        int count = _smc.TryReadKey("FNum", out var fnum) ? (int)SmcCodec.Decode(fnum) : 0;
        if (count <= 0) return;
        // SMC-Lüfter-Keys nutzen einen EINSTELLIGEN Index (F0..F9); ein zweistelliger würde beim
        // 4-Zeichen-FourCC abgeschnitten (F10Tg → F10T). Reale Macs haben < 10 Lüfter - hart begrenzen.
        count = Math.Min(count, 10);

        for (int i = 0; i < count; i++)
        {
            string acKey = $"F{i}Ac";
            if (!_smc.TryReadKey(acKey, out _)) continue; // kein Ist-Signal → kein Lüfter

            var tachId = new SensorId($"smc/{acKey}");
            _sensors[tachId] = new SensorChannel(tachId, $"Fan {i + 1}", SensorKind.FanRpm, "RPM", acKey, i);

            // Steuer-Keys: Ziel-Drehzahl (Tg) + Modus (Md), Grenzen (Mn/Mx). Steuerbar nur, wenn die
            // Plattform Steuerung erlaubt (Intel + Root; Apple Silicon nie), alle Keys vorhanden sind,
            // die Grenzen plausibel sind UND Ziel/Modus tatsächlich KODIERBAR sind - sonst schaltete
            // SetPwm auf Manual (Firmware aus) und überspränge den Ziel-Write mangels Encoder.
            string? tgKey = KeyIfReadable($"F{i}Tg", out var tgType);
            string? mdKey = KeyIfReadable($"F{i}Md", out var mdType);
            double min = ReadOr($"F{i}Mn", double.NaN);
            double max = ReadOr($"F{i}Mx", double.NaN);

            bool controllable = control.Allowed
                && tgKey is not null && mdKey is not null
                && !double.IsNaN(min) && !double.IsNaN(max) && max > min
                && SmcCodec.Encode(tgType!, min) is not null
                && SmcCodec.Encode(mdType!, 0) is not null;

            var id = new FanId($"smc/F{i}");
            _fans[id] = new FanChannel(
                id, $"Fan {i + 1}", acKey,
                controllable ? tgKey : null, controllable ? tgType : null,
                controllable ? mdKey : null, controllable ? mdType : null,
                min, max, controllable, tachId);
        }
    }

    private void ScanTemperatures()
    {
        int rank = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Apple-Silicon-Cluster zuerst (E-Cores → P-Cores → GPU; Familie per Key-Präsenz erkannt),
        // danach die flache kuratierte Liste. Die Cluster-Tabelle hat Vorrang, weil sich Keys
        // überlagern (z. B. Tp0P: M1 P-Core 6 vs. Intel Netzteil-Proximity) - `seen` verhindert,
        // dass die flache Liste einen Familien-Key erneut (falsch beschriftet) aufnimmt.
        foreach (var (key, name) in MacTemperatureKeys.SelectAppleSiliconCluster(
                     k => _smc.TryReadKey(k, out _)))
        {
            if (seen.Add(key)) AddTemperature(key, name, rank++);
        }

        foreach (var (key, name) in MacTemperatureKeys.Known)
        {
            if (seen.Add(key)) AddTemperature(key, name, rank++);
        }
    }

    private void AddTemperature(string key, string name, int rank)
    {
        if (!_smc.TryReadKey(key, out var raw)) return;
        double v = SmcCodec.Decode(raw);
        // Nur exponieren, wenn der Sensor real bestückt ist (endlicher, plausibler Wert > 0).
        if (double.IsNaN(v) || v <= 0 || v >= 130) return;

        var id = new SensorId($"smc/{key}");
        _sensors[id] = new SensorChannel(id, name, SensorKind.Temperature, "°C", key, rank);
    }

    /// <summary>Liefert den Key, wenn er lesbar ist (existiert), samt seines SMC-Datentyps; sonst <c>null</c>.</summary>
    private string? KeyIfReadable(string key, out string? type)
    {
        if (_smc.TryReadKey(key, out var raw)) { type = raw.Type; return key; }
        type = null;
        return null;
    }

    private double ReadOr(string key, double fallback) =>
        _smc.TryReadKey(key, out var raw) ? SmcCodec.Decode(raw) : fallback;

    // --- Mapping PWM (0..255) ↔ Ziel-Drehzahl (RPM) ---------------------------

    internal static byte RpmToPwm(double rpm, double min, double max)
    {
        if (double.IsNaN(rpm) || max <= min) return 0;
        double f = (rpm - min) / (max - min);
        return (byte)Math.Clamp(Math.Round(f * 255.0), 0, 255);
    }

    internal static double PwmToRpm(byte pwm, double min, double max)
    {
        if (max <= min) return min;
        return min + (max - min) * pwm / 255.0;
    }

    private FanChannel Fan(FanId id) =>
        _fans.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Unbekannter Lüfter: {id}");

    // --- Plattform-Erkennung (real) -------------------------------------------

    private static ISmc CreateRealSmc()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("MacSmcBackend läuft nur unter macOS.");
        return new AppleSmc();
    }

    /// <summary>
    /// Bestimmt, ob Lüftersteuerung möglich ist. Voraussetzung ist <b>Root</b> - SMC-Steuer-Writes sind
    /// privilegiert. Gilt für Intel <b>und</b> Apple Silicon: neuere Apple-Silicon-Macs nehmen SMC-Writes
    /// auf <c>F{i}Md</c>/<c>F{i}Tg</c> an (wie Macs Fan Control u. a. zeigen). Ob ein <em>konkreter</em>
    /// Kanal steuerbar ist, entscheidet zusätzlich <see cref="ScanFans"/> (Steuer-Keys vorhanden +
    /// kodierbar); reagiert die Hardware trotzdem nicht, greift der Fail-Safe (Watchdog + RestoreDefaults).
    /// </summary>
    private static ControlCapability DetectControl()
    {
        if (Geteuid() != 0)
            return new ControlCapability(false,
                "Lüftersteuerung braucht Root - den Daemon per sudo starten; sonst sind die Kanäle read-only.");
        return new ControlCapability(true, null);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();

    /// <summary>Ob Lüftersteuerung möglich ist, plus (falls nicht) ein diagnostischer Grund für <see cref="StartupWarning"/>.</summary>
    internal readonly record struct ControlCapability(bool Allowed, string? DisabledReason);

    // Rank = Anzeigerang innerhalb der Sensorart (kuratierte Listenposition bzw. Lüfter-Index).
    private readonly record struct SensorChannel(
        SensorId Id, string Name, SensorKind Kind, string Unit, string Key, int Rank);

    private sealed record FanChannel(
        FanId Id, string Name, string AcKey,
        string? TargetKey, string? TargetType,
        string? ModeKey, string? ModeType,
        double MinRpm, double MaxRpm, bool CanControl, SensorId? Tachometer)
    {
        public string Source => AcKey;
    }
}
