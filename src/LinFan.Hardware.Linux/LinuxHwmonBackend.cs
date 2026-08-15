// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Hardware.Linux;

/// <summary>
/// Linux-Backend über das <c>hwmon</c>-Subsystem (<c>/sys/class/hwmon</c>).
/// Liest Temperaturen (<c>tempN_input</c>) und Drehzahlen (<c>fanN_input</c>) und steuert
/// PWM-Kanäle (<c>pwmN</c> / <c>pwmN_enable</c>).
/// <para>
/// Lesen funktioniert ohne Root; PWM-Schreiben braucht Root. <see cref="RestoreDefaults"/> /
/// <see cref="Dispose"/> fahren jeden Kanal in den <em>sicheren</em> Zustand (Hardware-Auto, sonst
/// Volllast) - bewusst NICHT in den bei Discovery gelesenen Zustand (Fail-Safe, siehe dort).
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxHwmonBackend : ISensorBackend, IFanController, ILegacyIdMap
{
    private const string HwmonRoot = "/sys/class/hwmon";

    private readonly Dictionary<SensorId, SensorChannel> _sensors = new();
    private readonly Dictionary<FanId, PwmChannel> _fans = new();

    // Einmal beim Scan materialisierte, sortierte Deskriptor-Listen: _sensors/_fans ändern sich nach dem
    // Scan nicht mehr, daher darf der Hot-Path (Snapshot-/Regel-Tick ruft Discover* jeden Tick) sie
    // wiederverwenden, statt die Liste jedes Mal neu aufzubauen und zu sortieren. Unveränderlich → thread-safe.
    private SensorDescriptor[] _sensorDescriptors = Array.Empty<SensorDescriptor>();
    private FanDescriptor[] _fanDescriptors = Array.Empty<FanDescriptor>();

    // Alte instabile Id (hwmonN/channel) → aktuelle stabile Id (chip/channel), für die Config-Migration.
    private readonly Dictionary<string, string> _legacyAliases = new(StringComparer.Ordinal);
    private bool _disposed;

    public LinuxHwmonBackend()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("LinuxHwmonBackend läuft nur unter Linux.");
        Scan();
    }

    // --- ISensorBackend -------------------------------------------------------

    public IReadOnlyList<SensorDescriptor> DiscoverSensors() => _sensorDescriptors;

    public double ReadValue(SensorId id)
    {
        if (!_sensors.TryGetValue(id, out var ch))
            throw new KeyNotFoundException($"Unbekannter Sensor: {id}");

        if (!TryReadRaw(ch.InputPath, out long raw))
            return double.NaN; // Kanal momentan nicht lesbar (z. B. EIO) - defensiv, kein Fehler

        return InterpretRaw(ch.Kind, raw);
    }

    /// <summary>
    /// Wandelt einen rohen hwmon-Wert in die Zielgröße: Temperatur m°C → °C; Drehzahl unverändert,
    /// aber der EC-Sentinel <c>0xFFFF</c> (65535 - „ungültig", z. B. beim Moduswechsel) wird zu NaN.
    /// </summary>
    public static double InterpretRaw(SensorKind kind, long raw) => kind switch
    {
        SensorKind.Temperature => raw / 1000.0,
        SensorKind.FanRpm => raw >= 0xFFFF ? double.NaN : raw,
        _ => raw,
    };

    // --- IFanController -------------------------------------------------------

    public IReadOnlyList<FanDescriptor> DiscoverFans() => _fanDescriptors;

    public bool CanControl(FanId id) => Fan(id).CanControl;

    public FanMode GetMode(FanId id)
    {
        var fan = Fan(id);
        if (fan.EnablePath is null || !TryReadRaw(fan.EnablePath, out long v))
            return FanMode.Auto;
        return v == 1 ? FanMode.Manual : FanMode.Auto;
    }

    public void SetMode(FanId id, FanMode mode)
    {
        var fan = Fan(id);
        if (fan.EnablePath is null)
            throw new NotSupportedException($"Lüfter {id} kennt keinen Steuermodus (kein pwmN_enable).");

        // 1 = Manual, 2 = Hardware-Auto (gängige hwmon-Semantik).
        WriteRaw(fan.EnablePath, mode == FanMode.Manual ? 1 : AutoEnable);
    }

    /// <summary>hwmon-Wert für „Hardware regelt selbst" (Fail-Safe-/Auto-Zielzustand).</summary>
    private const long AutoEnable = 2;

    public byte GetPwm(FanId id)
    {
        var fan = Fan(id);
        return TryReadRaw(fan.PwmPath, out long v) ? (byte)Math.Clamp(v, 0, 255) : (byte)0;
    }

    public void SetPwm(FanId id, byte value)
    {
        var fan = Fan(id);
        if (!fan.CanControl)
            throw new NotSupportedException(
                $"Lüfter {id} ist nicht steuerbar (kein Schreibzugriff - als Root ausführen).");

        // Vor dem Schreiben Manual erzwingen, sonst überschreibt die Firmware den Wert.
        if (fan.EnablePath is not null)
            WriteRaw(fan.EnablePath, 1);
        WriteRaw(fan.PwmPath, value);
    }

    public void RestoreDefaults()
    {
        // Der sichere Zustand ist IMMER Hardware-Auto (die Firmware übernimmt die thermische Regelung).
        // Bewusst NICHT der bei Discovery gelesene Zustand: der kann Manual/niedrig gewesen sein (z. B.
        // weil ein früherer Lauf abgestürzt ist) und würde den Lüfter ohne aktiven Watchdog dort
        // festhalten - genau der gefährliche Fall. Kennt ein Kanal keinen Auto-Modus (kein pwmN_enable),
        // fällt er ersatzweise auf Volllast (255). Best-effort: Try* schluckt Fehler je Kanal.
        foreach (var fan in _fans.Values)
        {
            if (fan.EnablePath is not null)
                TryWriteRaw(fan.EnablePath, AutoEnable); // 2 = Hardware-Auto
            else
                TryWriteRaw(fan.PwmPath, 255);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreDefaults();
    }

    // --- Scan & I/O -----------------------------------------------------------

    private PwmChannel Fan(FanId id) =>
        _fans.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Unbekannter Lüfter: {id}");

    private void Scan()
    {
        if (!Directory.Exists(HwmonRoot))
            return;

        // Pass 1: jedes hwmon-Verzeichnis mit Chip-Name (stabil) und Bus-Adresse (für Kollisionen) erfassen.
        var dirs = new List<ChipDir>();
        foreach (string dir in Directory.EnumerateDirectories(HwmonRoot))
        {
            string hwmonName = Path.GetFileName(dir);          // z. B. "hwmon7" - INSTABIL über Reboots
            string chip = ReadText(Path.Combine(dir, "name")) ?? hwmonName;
            dirs.Add(new ChipDir(dir, hwmonName, chip, ReadBusAddr(dir)));
        }

        // Pass 2: pro Verzeichnis einen stabilen Chip-Schlüssel bestimmen und die Kanäle damit registrieren.
        // So lautet die Id "chip/channel" statt "hwmonN/channel" und übersteht eine geänderte Enumeration.
        IReadOnlyDictionary<string, string> chipKeys = ResolveChipKeys(dirs);
        foreach (ChipDir d in dirs)
        {
            string chipKey = chipKeys[d.HwmonName];

            foreach (string input in Directory.EnumerateFiles(d.Dir, "temp*_input"))
                AddSensor(d, chipKey, input, SensorKind.Temperature, "°C");
            foreach (string input in Directory.EnumerateFiles(d.Dir, "fan*_input"))
                AddSensor(d, chipKey, input, SensorKind.FanRpm, "RPM");

            foreach (string pwmPath in Directory.EnumerateFiles(d.Dir, "pwm*"))
            {
                string file = Path.GetFileName(pwmPath);
                if (IsBarePwm(file))                            // "pwm1", nicht "pwm1_enable"/"pwm1_mode"
                    AddFan(d, chipKey, pwmPath, file);
            }
        }

        // Deskriptoren einmal sortiert materialisieren (siehe Feld-Kommentar) - danach sind die Kanäle fix.
        _sensorDescriptors = _sensors.Values
            .Select(c => new SensorDescriptor(c.Id, c.Name, c.Kind, c.Unit, c.InputPath))
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
        _fanDescriptors = _fans.Values
            .Select(f => new FanDescriptor(f.Id, f.Name, f.CanControl, f.Tachometer, f.PwmPath))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Bestimmt je hwmon-Verzeichnis einen stabilen, eindeutigen Chip-Schlüssel: den Chip-<c>name</c>;
    /// bei Namensgleichheit (z. B. zwei <c>coretemp</c>) per stabiler Bus-/Plattform-Adresse
    /// disambiguiert (<c>coretemp@0000:…</c>). Fehlt dann noch die Adresse, bleibt als letzter - und
    /// einziger instabiler - Ausweg der hwmon-Name. Rein (kein I/O) und damit testbar.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ResolveChipKeys(IReadOnlyList<ChipDir> dirs)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ChipDir d in dirs)
            counts[d.Chip] = counts.GetValueOrDefault(d.Chip) + 1;

        var keys = new Dictionary<string, string>(StringComparer.Ordinal); // hwmonName → chipKey
        var used = new HashSet<string>(StringComparer.Ordinal);
        // Deterministische Reihenfolge, damit der seltene Pathologie-Fallback (#i) stabil bleibt.
        foreach (ChipDir d in dirs.OrderBy(x => x.BusAddr ?? "", StringComparer.Ordinal)
                                  .ThenBy(x => x.HwmonName, StringComparer.Ordinal))
        {
            string key =
                counts[d.Chip] == 1 ? d.Chip
                : !string.IsNullOrEmpty(d.BusAddr) ? $"{d.Chip}@{d.BusAddr}"
                : d.HwmonName;

            if (!used.Add(key)) // theoretische Restkollision (gleicher Name UND gleiche Adresse) absichern
            {
                string baseKey = key;
                for (int i = 2; !used.Add(key = $"{baseKey}#{i}"); i++) { }
            }

            keys[d.HwmonName] = key;
        }
        return keys;
    }

    private void AddSensor(ChipDir d, string chipKey, string inputPath, SensorKind kind, string unit)
    {
        string file = Path.GetFileName(inputPath);             // "temp1_input"
        string channel = file[..file.IndexOf("_input", StringComparison.Ordinal)]; // "temp1"
        string? label = ReadText(Path.Combine(d.Dir, channel + "_label"));
        string name = label is null ? $"{d.Chip} {channel}" : $"{d.Chip} {label}";

        var id = new SensorId($"{chipKey}/{channel}");
        _sensors[id] = new SensorChannel(id, name, kind, unit, inputPath);
        RecordAlias($"{d.HwmonName}/{channel}", id.Value);
    }

    private void AddFan(ChipDir d, string chipKey, string pwmPath, string pwmFile)
    {
        string index = new string(pwmFile.Where(char.IsDigit).ToArray()); // "1"
        string? enablePath = PathIfExists(Path.Combine(d.Dir, $"pwm{index}_enable"));
        string? tachPath = PathIfExists(Path.Combine(d.Dir, $"fan{index}_input"));
        SensorId? tach = tachPath is null ? null : new SensorId($"{chipKey}/fan{index}");

        bool canControl = HasWriteAccess(pwmPath);

        var id = new FanId($"{chipKey}/pwm{index}");
        _fans[id] = new PwmChannel(id, $"{d.Chip} pwm{index}", pwmPath, enablePath, tach, canControl);
        RecordAlias($"{d.HwmonName}/pwm{index}", id.Value);
    }

    /// <summary>Merkt sich die alte hwmonN-basierte Id als Alias der neuen stabilen Id (für die Migration).</summary>
    private void RecordAlias(string legacy, string stable)
    {
        if (!string.Equals(legacy, stable, StringComparison.Ordinal))
            _legacyAliases[legacy] = stable;
    }

    /// <summary>
    /// Stabile Bus-/Plattform-Adresse eines hwmon-Chips (letztes Segment des <c>device</c>-Symlinks,
    /// z. B. <c>0000:00:18.3</c> für PCI oder <c>nct6775.2592</c> für ISA/Plattform). <c>null</c>, wenn
    /// kein <c>device</c>-Link existiert. Wird nur zur Disambiguierung doppelter Chip-Namen gebraucht.
    /// </summary>
    private static string? ReadBusAddr(string dir)
    {
        try
        {
            FileSystemInfo? target =
                Directory.ResolveLinkTarget(Path.Combine(dir, "device"), returnFinalTarget: true);
            return string.IsNullOrEmpty(target?.Name) ? null : target.Name;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBarePwm(string file)
    {
        if (!file.StartsWith("pwm", StringComparison.Ordinal))
            return false;
        string rest = file[3..];
        return rest.Length > 0 && rest.All(char.IsDigit);
    }

    /// <summary>Prüft Schreibrecht ohne Seiteneffekt (kein Öffnen der sysfs-Datei) via <c>access(2)</c>.</summary>
    private static bool HasWriteAccess(string path) => access(path, W_OK) == 0;

    private const int W_OK = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern int access(string pathname, int mode);

    private static string? PathIfExists(string path) => File.Exists(path) ? path : null;

    private static bool TryReadRaw(string path, out long value)
    {
        value = 0;
        try
        {
            string text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        catch
        {
            return false; // EIO, EACCES, ENOENT … → Kanal momentan nicht lesbar
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim() is { Length: > 0 } s ? s : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteRaw(string path, long value) =>
        File.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture));

    private static bool TryWriteRaw(string path, long value)
    {
        try
        {
            WriteRaw(path, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- ILegacyIdMap ---------------------------------------------------------

    public IReadOnlyDictionary<string, string> LegacyToStableIds() => _legacyAliases;

    // --- Records --------------------------------------------------------------

    /// <summary>Ein hwmon-Verzeichnis mit den für die stabile Id relevanten Stammdaten.</summary>
    internal readonly record struct ChipDir(string Dir, string HwmonName, string Chip, string? BusAddr);

    private readonly record struct SensorChannel(
        SensorId Id, string Name, SensorKind Kind, string Unit, string InputPath);

    private sealed record PwmChannel(
        FanId Id, string Name, string PwmPath, string? EnablePath, SensorId? Tachometer,
        bool CanControl);
}
