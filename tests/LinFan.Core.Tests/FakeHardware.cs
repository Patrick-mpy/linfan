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

    public double ReadValue(SensorId id) => Values.TryGetValue(id.Value, out double v) ? v : double.NaN;

    public IReadOnlyList<FanDescriptor> DiscoverFans() => Fans;

    public bool CanControl(FanId id) => Fans.First(f => f.Id == id).CanControl;

    public FanMode GetMode(FanId id) => FanMode.Manual;

    public void SetMode(FanId id, FanMode mode) => ModeWrites.Add((id.Value, mode));

    public byte GetPwm(FanId id) => Pwm.TryGetValue(id.Value, out byte v) ? v : (byte)0;

    public void SetPwm(FanId id, byte value)
    {
        if (!CanControl(id))
            throw new NotSupportedException($"{id} nicht steuerbar");

        Pwm[id.Value] = value;
        Writes.Add((id.Value, value));
        if (RpmForPwm is not null && TachId is not null)
            Values[TachId] = RpmForPwm(value);
    }

    public void RestoreDefaults() => RestoreCount++;

    public void Dispose() { }
}
