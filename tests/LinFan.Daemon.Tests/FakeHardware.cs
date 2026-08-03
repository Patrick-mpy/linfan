// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Daemon.Tests;

/// <summary>In-Memory-Backend für Tests: implementiert beide Hardware-Rollen, scriptbar und protokollierend.</summary>
internal sealed class FakeHardware : ISensorBackend, IFanController, ILegacyIdMap
{
    public List<SensorDescriptor> Sensors { get; } = new();
    public List<FanDescriptor> Fans { get; } = new();
    public Dictionary<string, double> Values { get; } = new();
    public Dictionary<string, byte> Pwm { get; } = new();
    public List<(string Fan, byte Pwm)> Writes { get; } = new();
    public int RestoreCount { get; private set; }

    /// <summary>
    /// Zuletzt gesetzter Hardware-Modus je Lüfter — modelliert <c>pwmN_enable</c>: ein PWM-Write bzw.
    /// <see cref="SetMode"/>(Manual) ⇒ Manual (enable=1), <see cref="SetMode"/>(Auto)/<see cref="RestoreDefaults"/>
    /// ⇒ Auto (enable=2). Nur für Tests, die den Terminal-Zustand prüfen (z. B. Fail-Safe nach Reset/Import).
    /// </summary>
    public Dictionary<string, FanMode> ModeLog { get; } = new();

    /// <summary>Optional: bildet beim Setzen von PWM die Drehzahl von <see cref="TachId"/> nach (Kalibrierung).</summary>
    public Func<byte, int>? RpmForPwm { get; set; }
    public string? TachId { get; set; }

    /// <summary>
    /// Sensor-IDs, deren <see cref="ReadValue"/> mit <see cref="IOException"/> (EIO) wirft — modelliert einen
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

    public void AddFan(string id, bool canControl = true, string? tachId = null, string name = "pwm")
    {
        var tach = tachId is null ? (SensorId?)null : new SensorId(tachId);
        Fans.Add(new FanDescriptor(new FanId(id), name, canControl, tach, id));
    }

    public IReadOnlyList<SensorDescriptor> DiscoverSensors() => Sensors;

    public double ReadValue(SensorId id)
    {
        if (ThrowingReads.Contains(id.Value))
            throw new IOException($"EIO: {id.Value}");
        return Values.TryGetValue(id.Value, out double v) ? v : double.NaN;
    }

    public IReadOnlyList<FanDescriptor> DiscoverFans() => Fans;

    public bool CanControl(FanId id) => Fans.First(f => f.Id == id).CanControl;

    public FanMode GetMode(FanId id) => FanMode.Manual;

    public void SetMode(FanId id, FanMode mode) => ModeLog[id.Value] = mode;

    public byte GetPwm(FanId id) => Pwm.TryGetValue(id.Value, out byte v) ? v : (byte)0;

    public void SetPwm(FanId id, byte value)
    {
        if (!CanControl(id))
            throw new NotSupportedException($"{id} nicht steuerbar");

        Pwm[id.Value] = value;
        Writes.Add((id.Value, value));
        ModeLog[id.Value] = FanMode.Manual; // ein PWM-Write impliziert enable=1 (Manual)
        if (RpmForPwm is not null && TachId is not null)
            Values[TachId] = RpmForPwm(value);
    }

    public void RestoreDefaults()
    {
        RestoreCount++;
        foreach (FanDescriptor fan in Fans.Where(f => f.CanControl))
            ModeLog[fan.Id.Value] = FanMode.Auto; // enable=2 je steuerbarem Kanal
    }

    /// <summary>Legacy→stabil-Zuordnung für die Migration; leer = kein Effekt (Default für die meisten Tests).</summary>
    public Dictionary<string, string> LegacyIds { get; } = new();

    public IReadOnlyDictionary<string, string> LegacyToStableIds() => LegacyIds;

    public void Dispose() { }
}
