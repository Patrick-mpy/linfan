// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
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
/// Lesen funktioniert ohne Root; PWM-Schreiben braucht Root. <see cref="RestoreDefaults"/> fährt jeden Kanal in den
/// <em>sicheren</em> Zustand (Hardware-Auto, sonst Volllast) - bewusst NICHT in den bei Discovery gelesenen Zustand
/// (Fail-Safe, siehe dort).
/// </para>
/// <para>
/// <see cref="Dispose"/> restores every channel the same way, but only once this instance has written one
/// (<see cref="SetPwm"/>, <see cref="SetMode"/> to manual): a read-only use - the CLI's <c>list</c>/<c>monitor</c>/
/// <c>init</c> as root, next to a running daemon - must not hand that daemon's fans to the firmware.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxHwmonBackend : ISensorBackend, IFanController, IRestoreDiagnostics,
    IBackendDiagnostics
{
    private const string SysHwmonRoot = "/sys/class/hwmon";
    private const string SysModuleRoot = "/sys/module";

    /// <summary>hwmon <c>name</c> of the <c>thinkpad_acpi</c> driver.</summary>
    private const string ThinkpadChip = "thinkpad";

    /// <summary>hwmon <c>name</c> of the nvme driver.</summary>
    private const string NvmeChip = "nvme";

    private readonly string _hwmonRoot;
    private readonly string _moduleRoot;

    private readonly Dictionary<SensorId, SensorChannel> _sensors = new();
    private readonly Dictionary<FanId, PwmChannel> _fans = new();

    // Einmal beim Scan materialisierte, sortierte Deskriptor-Listen: _sensors/_fans ändern sich nach dem
    // Scan nicht mehr, daher darf der Hot-Path (Snapshot-/Regel-Tick ruft Discover* jeden Tick) sie
    // wiederverwenden, statt die Liste jedes Mal neu aufzubauen und zu sortieren. Unveränderlich → thread-safe.
    private SensorDescriptor[] _sensorDescriptors = Array.Empty<SensorDescriptor>();
    private FanDescriptor[] _fanDescriptors = Array.Empty<FanDescriptor>();

    private volatile bool _disposed;
    private volatile bool _tookOver; // see Dispose
    private volatile IReadOnlyList<string> _lastRestoreProblems = Array.Empty<string>();

    public LinuxHwmonBackend()
        : this(SysHwmonRoot, SysModuleRoot)
    {
    }

    /// <summary>
    /// Test seam: scans a directory laid out like <c>/sys/class/hwmon</c> and reads driver parameters from one laid
    /// out like <c>/sys/module</c>.
    /// </summary>
    internal LinuxHwmonBackend(string hwmonRoot, string moduleRoot)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("LinuxHwmonBackend läuft nur unter Linux.");
        _hwmonRoot = hwmonRoot;
        _moduleRoot = moduleRoot;
        Scan();
    }

    /// <inheritdoc/>
    public string? StartupWarning { get; private set; }

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
        if (fan.EnablePath is null)
            return FanMode.Auto; // no pwmN_enable: no control mode
        if (!TryReadRaw(fan.EnablePath, out long v))
            throw new IOException($"Steuermodus von {id} nicht lesbar ({fan.EnablePath}).");
        return v == 1 ? FanMode.Manual : FanMode.Auto;
    }

    public void SetMode(FanId id, FanMode mode)
    {
        var fan = Fan(id);
        if (fan.EnablePath is null)
            throw new NotSupportedException($"Lüfter {id} kennt keinen Steuermodus (kein pwmN_enable).");

        if (mode == FanMode.Manual)
        {
            // Dispose has restored every channel already; a later manual write would run with nothing to undo it.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _tookOver = true; // before the write: a partial or failed write may still have left the channel manual
            WriteOrHandBack(fan, () => WriteRaw(fan.EnablePath, 1));
            return;
        }
        WriteRaw(fan.EnablePath, AutoEnable); // 2 = Hardware-Auto, 1 = Manual (gängige hwmon-Semantik)
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        _tookOver = true;
        WriteOrHandBack(fan, () =>
        {
            // Vor dem Schreiben Manual erzwingen, sonst überschreibt die Firmware den Wert.
            if (fan.EnablePath is not null)
                WriteRaw(fan.EnablePath, 1);
            WriteRaw(fan.PwmPath, value);
        });
    }

    /// <summary>
    /// Runs a manual write; if it fails, hands the channel back (<see cref="HandBack"/>) and rethrows. The enable write may
    /// have gone through before the value failed: left alone, the channel would sit on manual with its old value, and the
    /// caller saw a failed write, so it has nothing to hand back.
    /// </summary>
    private void WriteOrHandBack(PwmChannel fan, Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            // The caller only sees the exception: a hand-back that failed as well must show up there.
            if (HandBack(fan) is { } problem)
                throw new IOException($"{ex.Message} {problem}", ex);
            throw;
        }
    }

    public void RestoreDefaults()
    {
        // Der sichere Zustand ist IMMER Hardware-Auto (die Firmware übernimmt die thermische Regelung).
        // Bewusst NICHT der bei Discovery gelesene Zustand: der kann Manual/niedrig gewesen sein (z. B.
        // weil ein früherer Lauf abgestürzt ist) und würde den Lüfter ohne aktiven Watchdog dort
        // festhalten - genau der gefährliche Fall. Kennt ein Kanal keinen Auto-Modus (kein pwmN_enable),
        // fällt er ersatzweise auf Volllast (255). Best effort: a failing channel never skips the others.
        // A channel whose auto write fails gets full speed as well: its mode is unknown then, and it may still sit at
        // a low manual value. Read-only channels are skipped - nothing could be written there.
        var problems = new List<string>();
        foreach (var fan in _fans.Values)
        {
            if (!fan.CanControl)
            {
                // Nothing can be written, but a fan the EC still holds on a manual level (module reloaded without
                // fan_control while the fan was manual) must not pass for restored. pwm1_enable stays readable.
                if (fan.DriverLocked && fan.EnablePath is not null && TryReadRaw(fan.EnablePath, out long mode) && mode == 1)
                {
                    problems.Add($"{fan.Id}: steht auf einer manuellen Stufe, thinkpad_acpi ohne fan_control=1 lässt "
                        + "keinen Write zu - mit fan_control=1 laden und 'restore' ausführen oder neu starten.");
                }
                continue;
            }
            if (HandBack(fan) is { } problem)
                problems.Add(problem);
        }
        _lastRestoreProblems = problems;
    }

    /// <summary>
    /// Puts one controllable channel into its safe state - hardware auto, else full speed. Returns what went wrong, or
    /// <c>null</c>; never throws.
    /// </summary>
    private string? HandBack(PwmChannel fan)
    {
        Exception? autoError = null;
        if (fan.EnablePath is not null && (autoError = TryWriteRaw(fan.EnablePath, AutoEnable)) is null)
            return null; // 2 = Hardware-Auto

        Exception? fullSpeedError = TryWriteRaw(fan.PwmPath, 255);
        if (autoError is null && fullSpeedError is null)
            return null; // no auto mode at all: full speed is this channel's safe state
        return (autoError, fullSpeedError) switch
        {
            ({ } a, null) => $"{fan.Id}: Hardware-Auto nicht setzbar ({a.Message}) - stattdessen Volllast.",
            ({ } a, { } f) => $"{fan.Id}: weder Hardware-Auto ({a.Message}) noch Volllast ({f.Message}) setzbar.",
            (null, { } f) => $"{fan.Id}: kein Hardware-Auto, Volllast nicht setzbar ({f.Message}).",
            _ => throw new UnreachableException(),
        };
    }

    public IReadOnlyList<string> LastRestoreProblems => _lastRestoreProblems;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Restores every channel, but only if this instance wrote one: fans that something else drives (a running
        // daemon, the firmware) are not this instance's to reset. Explicit RestoreDefaults calls (daemon start/stop,
        // fail-safe, CLI `restore`) always restore.
        if (_tookOver)
            RestoreDefaults();
    }

    // --- Scan & I/O -----------------------------------------------------------

    private PwmChannel Fan(FanId id) =>
        _fans.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Unbekannter Lüfter: {id}");

    private void Scan()
    {
        if (!Directory.Exists(_hwmonRoot))
            return;

        // Pass 1: jedes hwmon-Verzeichnis mit Chip-Name (stabil) und Bus-Adresse (für Kollisionen) erfassen.
        var dirs = new List<ChipDir>();
        foreach (string dir in Directory.EnumerateDirectories(_hwmonRoot))
        {
            string hwmonName = Path.GetFileName(dir);          // z. B. "hwmon7" - INSTABIL über Reboots
            string chip = ReadText(Path.Combine(dir, "name")) ?? hwmonName;
            dirs.Add(new ChipDir(dir, hwmonName, chip, ReadBusAddr(dir)));
        }

        // Pass 2: pro Verzeichnis einen stabilen Chip-Schlüssel bestimmen und die Kanäle damit registrieren.
        // So lautet die Id "chip/channel" statt "hwmonN/channel" und übersteht eine geänderte Enumeration.
        IReadOnlyDictionary<string, string> chipKeys = ResolveChipKeys(dirs);
        bool thinkpadLocked = false;
        foreach (ChipDir d in dirs)
        {
            string chipKey = chipKeys[d.HwmonName];
            bool driverLocked = d.Chip == ThinkpadChip && ThinkpadFanControlLocked();
            HardwareDevice device = ReadDevice(d);

            foreach (string input in Directory.EnumerateFiles(d.Dir, "temp*_input"))
                AddSensor(d, chipKey, input, SensorKind.Temperature, "°C", device);
            foreach (string input in Directory.EnumerateFiles(d.Dir, "fan*_input"))
                AddSensor(d, chipKey, input, SensorKind.FanRpm, "RPM", device);

            foreach (string pwmPath in Directory.EnumerateFiles(d.Dir, "pwm*"))
            {
                string file = Path.GetFileName(pwmPath);
                if (!IsBarePwm(file))                           // "pwm1", nicht "pwm1_enable"/"pwm1_mode"
                    continue;
                AddFan(d, chipKey, pwmPath, file, driverLocked, device);
                thinkpadLocked |= driverLocked;
            }
        }

        // Deskriptoren einmal sortiert materialisieren (siehe Feld-Kommentar) - danach sind die Kanäle fix.
        _sensorDescriptors = _sensors.Values
            .Select(c => new SensorDescriptor(c.Id, c.Name, c.Kind, c.Unit, c.InputPath, c.Device))
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
        _fanDescriptors = _fans.Values
            .Select(f => new FanDescriptor(f.Id, f.Name, f.CanControl, f.Tachometer, f.PwmPath, ReadOnlyReasonOf(f),
                f.Device))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();

        if (thinkpadLocked)
        {
            StartupWarning = "thinkpad_acpi ist ohne fan_control=1 geladen: der Treiber lehnt jeden Lüfter-Write ab, "
                + "LinFan kann den Lüfter nur lesen. Freischalten: 'options thinkpad_acpi fan_control=1' "
                + "in /etc/modprobe.d/ und das Modul neu laden (docs/INSTALL.md).";
        }
    }

    private static FanReadOnlyReason? ReadOnlyReasonOf(PwmChannel fan) =>
        fan.CanControl ? null
        : fan.DriverLocked ? FanReadOnlyReason.DriverLocked
        : FanReadOnlyReason.NoWriteAccess; // every hwmon pwmN is writable for someone - here not for this process

    /// <summary>
    /// <c>thinkpad_acpi</c> refuses every fan write with EPERM unless it was loaded with <c>fan_control=1</c> - root's
    /// writes included, which <c>access(2)</c> reports as allowed. Such a channel is read-only - a restore could not
    /// write it either (see <see cref="RestoreDefaults"/> for one still on a manual level). An unreadable parameter
    /// leaves the channel to the write check.
    /// </summary>
    private bool ThinkpadFanControlLocked() =>
        ReadText(Path.Combine(_moduleRoot, "thinkpad_acpi", "parameters", "fan_control")) == "N";

    /// <summary>
    /// Bestimmt je hwmon-Verzeichnis einen stabilen, eindeutigen Chip-Schlüssel: den Chip-<c>name</c>;
    /// bei Namensgleichheit (z. B. zwei <c>coretemp</c>) per stabiler Bus-/Plattform-Adresse
    /// disambiguiert (<c>coretemp@0000:…</c>). Fehlt dann noch die Adresse, bleibt als letzter - und
    /// instabiler - Ausweg der hwmon-Name. The same holds for a chip without a <c>name</c> file, whose name is its hwmon
    /// name (Scan). Rein (kein I/O) und damit testbar.
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

    private void AddSensor(ChipDir d, string chipKey, string inputPath, SensorKind kind, string unit,
        HardwareDevice device)
    {
        string file = Path.GetFileName(inputPath);             // "temp1_input"
        string channel = file[..file.IndexOf("_input", StringComparison.Ordinal)]; // "temp1"
        string? label = ReadText(Path.Combine(d.Dir, channel + "_label"));
        string name = label is null ? $"{d.Chip} {channel}" : $"{d.Chip} {label}";

        var id = new SensorId($"{chipKey}/{channel}");
        _sensors[id] = new SensorChannel(id, name, kind, unit, inputPath, device);
    }

    private void AddFan(ChipDir d, string chipKey, string pwmPath, string pwmFile, bool driverLocked,
        HardwareDevice device)
    {
        string index = new string(pwmFile.Where(char.IsDigit).ToArray()); // "1"
        string? enablePath = PathIfExists(Path.Combine(d.Dir, $"pwm{index}_enable"));
        string? tachPath = PathIfExists(Path.Combine(d.Dir, $"fan{index}_input"));
        SensorId? tach = tachPath is null ? null : new SensorId($"{chipKey}/fan{index}");

        bool canControl = !driverLocked && HasWriteAccess(pwmPath);

        var id = new FanId($"{chipKey}/pwm{index}");
        _fans[id] = new PwmChannel(id, $"{d.Chip} pwm{index}", pwmPath, enablePath, tach, canControl, driverLocked,
            device);
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

    /// <summary>
    /// The device behind a hwmon chip, read once at scan: the chip name and a fingerprint of the device its
    /// <c>device</c> link leads to. Never throws, and nothing else depends on it - chip key, ids and control checks are
    /// made without it. The scan also serves the CLI's <c>restore</c> and the unit's <c>ExecStopPost=</c>, which a throw
    /// here would block; every failure only costs the fingerprint (<c>null</c> = cannot tell).
    /// </summary>
    private static HardwareDevice ReadDevice(ChipDir d)
    {
        try
        {
            return new HardwareDevice(d.Chip, ReadFingerprint(Path.Combine(d.Dir, "device"), d.Chip));
        }
        catch
        {
            return new HardwareDevice(d.Chip, null);
        }
    }

    /// <summary>
    /// Fingerprint of the device at <paramref name="device"/>, from attributes the kernel holds in memory anyway - never
    /// <c>serial</c>, <c>vpd</c>, <c>config</c> or firmware attributes. The paths go through the <c>device</c> link for
    /// the kernel to follow: the link is relative to the real hwmon directory, which <c>/sys/class/hwmon</c> only links
    /// to, and <see cref="Directory.ResolveLinkTarget(string, bool)"/> applies its <c>..</c> to the linked path instead
    /// (<c>/sys/class/hwmon/hwmon11/device</c> → <c>/sys/0000:c5:00.0</c>, which does not exist).
    /// <list type="bullet">
    /// <item>A PCI device carries its ids itself (amdgpu, k10temp): <see cref="PciFingerprint"/>.</item>
    /// <item>An nvme controller names its drive: <c>nvme:&lt;model&gt;</c>, ahead of its PCI ids.</item>
    /// <item>A class device (nvme0, a wifi <c>phy0</c>) has its PCI device as the directory <c>device</c>; on a PCI
    /// device itself that name is the file with the device id.</item>
    /// <item>Platform, i2c and virtual devices (nct6775, thinkpad, acpitz, spd5118, power supplies): <c>null</c>.</item>
    /// </list>
    /// An attribute that exists but does not read cleanly gives <c>null</c> rather than a fallback: a fingerprint that
    /// differs from the usual one would read as another device.
    /// </summary>
    private static string? ReadFingerprint(string device, string chip)
    {
        if (File.Exists(Path.Combine(device, "vendor")))
            return PciFingerprint(device);
        if (chip == NvmeChip && File.Exists(Path.Combine(device, "model")))
            return ReadText(Path.Combine(device, "model")) is { } model ? "nvme:" + model : null;
        string parent = Path.Combine(device, "device");
        return Directory.Exists(parent) && File.Exists(Path.Combine(parent, "vendor")) ? PciFingerprint(parent) : null;
    }

    /// <summary>
    /// <c>pci:vendor:device:subsystem_vendor:subsystem_device</c> in lowercase hex without <c>0x</c>
    /// (<c>pci:1002:1114:17aa:5129</c>), just <c>pci:vendor:device</c> if neither subsystem id exists. <c>null</c> if an
    /// existing id is no hex number or unreadable, or only one subsystem id exists.
    /// </summary>
    private static string? PciFingerprint(string dir)
    {
        string? vendor = HexAttribute(dir, "vendor");
        string? device = HexAttribute(dir, "device");
        if (vendor is null || device is null)
            return null;
        if (!File.Exists(Path.Combine(dir, "subsystem_vendor")) && !File.Exists(Path.Combine(dir, "subsystem_device")))
            return $"pci:{vendor}:{device}";
        string? subVendor = HexAttribute(dir, "subsystem_vendor");
        string? subDevice = HexAttribute(dir, "subsystem_device");
        return subVendor is null || subDevice is null ? null : $"pci:{vendor}:{device}:{subVendor}:{subDevice}";
    }

    /// <summary>
    /// A sysfs id like <c>0x1002</c> as <c>1002</c>; <c>null</c> if unreadable or not <c>0x</c> and 1-8 hex digits (a
    /// SCSI disk's <c>vendor</c> is text, for instance).
    /// </summary>
    private static string? HexAttribute(string dir, string attribute)
    {
        string? text = ReadText(Path.Combine(dir, attribute));
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return null;
        string hex = text[2..];
        return hex.Length is >= 1 and <= 8 && hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
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

    /// <summary>Writes and returns the failure instead of throwing - <c>null</c> on success.</summary>
    private static Exception? TryWriteRaw(string path, long value)
    {
        try
        {
            WriteRaw(path, value);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // --- Records --------------------------------------------------------------

    /// <summary>Ein hwmon-Verzeichnis mit den für die stabile Id relevanten Stammdaten.</summary>
    internal readonly record struct ChipDir(string Dir, string HwmonName, string Chip, string? BusAddr);

    private readonly record struct SensorChannel(
        SensorId Id, string Name, SensorKind Kind, string Unit, string InputPath, HardwareDevice Device);

    private sealed record PwmChannel(
        FanId Id, string Name, string PwmPath, string? EnablePath, SensorId? Tachometer,
        bool CanControl, bool DriverLocked, HardwareDevice Device);
}
