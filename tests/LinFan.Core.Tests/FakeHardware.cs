// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Core.Tests;

/// <summary>In-Memory-Backend für Tests: implementiert beide Hardware-Rollen, scriptbar und protokollierend.</summary>
internal sealed class FakeHardware : ISensorBackend, IFanController
{
    public List<SensorDescriptor> Sensors { get; } = new();
    public List<FanDescriptor> Fans { get; } = new();
    public Dictionary<string, double> Values { get; } = new();
    public Dictionary<string, byte> Pwm { get; } = new();
    public List<(string Fan, byte Pwm)> Writes { get; } = new();
    public List<(string Fan, FanMode Mode)> ModeWrites { get; } = new();
    public int RestoreCount { get; private set; }

    /// <summary>Optional: bildet beim Setzen von PWM die Drehzahl von <see cref="TachId"/> nach (Kalibrierung).</summary>
    public Func<byte, int>? RpmForPwm { get; set; }
    public string? TachId { get; set; }

    public void AddTempSensor(string id, double value, string name = "temp")
    {
        Sensors.Add(new SensorDescriptor(new SensorId(id), name, SensorKind.Temperature, "°C", id));
        Values[id] = value;
    }

    public void AddFanSensor(string id, double rpm, string name = "fan")
    {
        Sensors.Add(new SensorDescriptor(new SensorId(id), name, SensorKind.FanRpm, "RPM", id));
        Values[id] = rpm;
    }

    public void AddFan(string id, bool canControl = true, string? tachId = null, string name = "pwm")
    {
        var tach = tachId is null ? (SensorId?)null : new SensorId(tachId);
        Fans.Add(new FanDescriptor(new FanId(id), name, canControl, tach, id));
    }

    public IReadOnlyList<SensorDescriptor> DiscoverSensors() => Sensors;

    /// <summary>How often each sensor was read - the loop must read a sensor once per tick, however many curves use it.</summary>
    public Dictionary<string, int> ReadCounts { get; } = new();

    public double ReadValue(SensorId id)
    {
        ReadCounts[id.Value] = ReadCounts.GetValueOrDefault(id.Value) + 1;
        return Values.TryGetValue(id.Value, out double v) ? v : double.NaN;
    }

    public IReadOnlyList<FanDescriptor> DiscoverFans() => Fans;

    public bool CanControl(FanId id) => Fans.First(f => f.Id == id).CanControl;

    public FanMode GetMode(FanId id) => FanMode.Manual;

    /// <summary>
    /// Makes <see cref="SetMode"/> throw <see cref="NotSupportedException"/>, like the Linux backend for a channel
    /// without an auto mode (no <c>pwmN_enable</c>).
    /// </summary>
    public bool ThrowOnSetMode { get; set; }

    /// <summary>Makes <see cref="SetMode"/> throw <see cref="IOException"/> - a transient failure on a channel that has the mode.</summary>
    public bool FailSetModeTransiently { get; set; }

    public void SetMode(FanId id, FanMode mode)
    {
        if (ThrowOnSetMode)
            throw new NotSupportedException($"{id}: no auto mode (simulated)");
        if (FailSetModeTransiently)
            throw new IOException($"{id}: EIO (simulated)");
        ModeWrites.Add((id.Value, mode));
    }

    public byte GetPwm(FanId id) => Pwm.TryGetValue(id.Value, out byte v) ? v : (byte)0;

    /// <summary>Makes <see cref="SetPwm"/> throw - a write the hardware rejects right now.</summary>
    public bool ThrowOnSetPwm { get; set; }

    public void SetPwm(FanId id, byte value)
    {
        if (!CanControl(id))
            throw new NotSupportedException($"{id} nicht steuerbar");
        if (ThrowOnSetPwm)
            throw new IOException($"{id}: write failed (simulated)");

        Pwm[id.Value] = value;
        Writes.Add((id.Value, value));
        if (RpmForPwm is not null && TachId is not null)
            Values[TachId] = RpmForPwm(value);
    }

    public void RestoreDefaults() => RestoreCount++;

    public void Dispose() { }
}
