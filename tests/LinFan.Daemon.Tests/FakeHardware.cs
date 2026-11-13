// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Daemon.Tests;

/// <summary>In-Memory-Backend für Tests: implementiert beide Hardware-Rollen, scriptbar und protokollierend.</summary>
internal sealed class FakeHardware : ISensorBackend, IFanController
{
    public List<SensorDescriptor> Sensors { get; } = new();
    public List<FanDescriptor> Fans { get; } = new();
    public Dictionary<string, double> Values { get; } = new();
    public Dictionary<string, byte> Pwm { get; } = new();
    public List<(string Fan, byte Pwm)> Writes { get; } = new();
    public int RestoreCount { get; private set; }

    /// <summary>
    /// Zuletzt gesetzter Hardware-Modus je Lüfter - modelliert <c>pwmN_enable</c>: ein PWM-Write bzw.
    /// <see cref="SetMode"/>(Manual) ⇒ Manual (enable=1), <see cref="SetMode"/>(Auto)/<see cref="RestoreDefaults"/>
    /// ⇒ Auto (enable=2). Nur für Tests, die den Terminal-Zustand prüfen (z. B. Fail-Safe nach Reset/Import).
    /// </summary>
    public Dictionary<string, FanMode> ModeLog { get; } = new();

    /// <summary>Every successful <see cref="SetMode"/> call in order (a <see cref="RestoreDefaults"/> is not one).</summary>
    public List<(string Fan, FanMode Mode)> ModeWrites { get; } = new();

    /// <summary>Optional: bildet beim Setzen von PWM die Drehzahl von <see cref="TachId"/> nach (Kalibrierung).</summary>
    public Func<byte, int>? RpmForPwm { get; set; }
    public string? TachId { get; set; }

    /// <summary>
    /// Sensor-IDs, deren <see cref="ReadValue"/> mit <see cref="IOException"/> (EIO) wirft - modelliert einen
    /// intermittierend kaputten hwmon-Kanal. Der defensive Watchdog muss solche Kanäle überspringen.
    /// </summary>
    public HashSet<string> ThrowingReads { get; } = new();

    public void AddTempSensor(string id, double value, string name = "temp")
    {
        Sensors.Add(new SensorDescriptor(new SensorId(id), name, SensorKind.Temperature, "°C", id));
        Values[id] = value;
    }

    /// <summary>Temp-Sensor, dessen Read mit EIO wirft (kaputter Kanal für Fail-Safe-/Watchdog-Tests).</summary>
    public void AddThrowingTempSensor(string id, string name = "temp")
    {
        Sensors.Add(new SensorDescriptor(new SensorId(id), name, SensorKind.Temperature, "°C", id));
        ThrowingReads.Add(id);
    }

    public void AddFanSensor(string id, double rpm, string name = "fan")
    {
        Sensors.Add(new SensorDescriptor(new SensorId(id), name, SensorKind.FanRpm, "RPM", id));
        Values[id] = rpm;
    }

    public void AddFan(string id, bool canControl = true, string? tachId = null, string name = "pwm",
                       FanReadOnlyReason? readOnlyReason = null)
    {
        var tach = tachId is null ? (SensorId?)null : new SensorId(tachId);
        Fans.Add(new FanDescriptor(new FanId(id), name, canControl, tach, id, readOnlyReason));
    }

    /// <summary>Makes <see cref="DiscoverSensors"/> and <see cref="DiscoverFans"/> throw - a backend whose scan broke.</summary>
    public volatile bool FailDiscovery;

    public IReadOnlyList<SensorDescriptor> DiscoverSensors() =>
        FailDiscovery ? throw new IOException("discovery failed (simulated)") : Sensors;

    /// <summary>Runs synchronously inside every <see cref="ReadValue"/> - lets a test wedge the caller's thread.</summary>
    public Action? OnRead { get; set; }

    private int _readValueCalls;
    private int _getPwmCalls;

    /// <summary>How often <see cref="ReadValue"/> has been called - the hardware reads a caller made.</summary>
    public int ReadValueCalls => Volatile.Read(ref _readValueCalls);

    /// <summary>How often <see cref="GetPwm"/> has been called.</summary>
    public int GetPwmCalls => Volatile.Read(ref _getPwmCalls);

    public double ReadValue(SensorId id)
    {
        Interlocked.Increment(ref _readValueCalls);
        OnRead?.Invoke();
        if (ThrowingReads.Contains(id.Value))
            throw new IOException($"EIO: {id.Value}");
        return Values.TryGetValue(id.Value, out double v) ? v : double.NaN;
    }

    public IReadOnlyList<FanDescriptor> DiscoverFans() =>
        FailDiscovery ? throw new IOException("discovery failed (simulated)") : Fans;

    public bool CanControl(FanId id) => Fans.First(f => f.Id == id).CanControl;

    public FanMode GetMode(FanId id) => FanMode.Manual;

    /// <summary>
    /// Channels without an auto mode (writable <c>pwmN</c>, no <c>pwmN_enable</c>, e.g. dell_smm_hwmon): <see cref="SetMode"/>
    /// throws like the Linux backend, <see cref="RestoreDefaults"/> sets them to full speed instead.
    /// </summary>
    public HashSet<string> NoAutoMode { get; } = new();

    /// <summary>Makes <see cref="SetMode"/> to auto fail like a transient I/O error (the channel has an auto mode).</summary>
    public volatile bool FailAutoMode;

    public void SetMode(FanId id, FanMode mode)
    {
        if (NoAutoMode.Contains(id.Value))
            throw new NotSupportedException($"Lüfter {id} kennt keinen Steuermodus (kein pwmN_enable).");
        if (FailAutoMode && mode == FanMode.Auto)
            throw new IOException($"EIO: {id}");
        ModeLog[id.Value] = mode;
        ModeWrites.Add((id.Value, mode));
    }

    /// <summary>
    /// Runs synchronously inside every <see cref="GetPwm"/>. Only the daemon's loop thread reads the PWM (the snapshot,
    /// a held curve), so this lets a test hold the loop while device actions run on.
    /// </summary>
    public Action? OnGetPwm { get; set; }

    public byte GetPwm(FanId id)
    {
        Interlocked.Increment(ref _getPwmCalls);
        OnGetPwm?.Invoke();
        return Pwm.TryGetValue(id.Value, out byte v) ? v : (byte)0;
    }

    /// <summary>Makes every <see cref="SetPwm"/> fail like a channel whose hardware went away (EIO).</summary>
    public volatile bool FailWrites;

    public void SetPwm(FanId id, byte value)
    {
        if (!CanControl(id))
            throw new NotSupportedException($"{id} nicht steuerbar");
        if (FailWrites)
            throw new IOException($"EIO: {id}");

        Pwm[id.Value] = value;
        Writes.Add((id.Value, value));
        ModeLog[id.Value] = FanMode.Manual; // ein PWM-Write impliziert enable=1 (Manual)
        if (RpmForPwm is not null && TachId is not null)
            Values[TachId] = RpmForPwm(value);
    }

    /// <summary>Makes <see cref="RestoreDefaults"/> throw (after counting the attempt) - a backend failure.</summary>
    public bool ThrowOnRestore { get; set; }

    /// <summary>Makes only the next this many <see cref="RestoreDefaults"/> calls throw - a transient backend failure.</summary>
    public int FailNextRestores { get; set; }

    public void RestoreDefaults()
    {
        RestoreCount++;
        bool transient = FailNextRestores > 0;
        if (transient)
            FailNextRestores--;
        if (ThrowOnRestore || transient)
            throw new IOException("RestoreDefaults failed (simulated).");
        foreach (FanDescriptor fan in Fans.Where(f => f.CanControl))
        {
            if (NoAutoMode.Contains(fan.Id.Value))
                Pwm[fan.Id.Value] = 255;              // no auto mode → full speed
            else
                ModeLog[fan.Id.Value] = FanMode.Auto; // enable=2 je steuerbarem Kanal
        }
    }

    public void Dispose() { }
}
