// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Hardware.Windows.Lhm;

namespace LinFan.Hardware.Windows.Tests;

/// <summary>
/// Plattformneutrale Fakes der LHM-Naht (<see cref="ILhmComputer"/>/<see cref="ILhmSensor"/>/
/// <see cref="ILhmControl"/>). Kein echtes <c>Computer</c>, keine Windows-API, kein Kernel-Treiber -
/// damit die Backend-Tests (inkl. Conformance) auf JEDEM OS laufen. Der Control ist
/// <b>zustandsbehaftet</b>: <see cref="FakeLhmControl.SetSoftware"/> schaltet auf
/// <see cref="LhmControlMode.Software"/> und merkt den Stellwert, <see cref="FakeLhmControl.SetDefault"/>
/// geht zurück auf <see cref="LhmControlMode.Default"/> - so verhält er sich wie echtes LHM gegenüber
/// dem Round-Trip-/Mode-Vertrag.
/// </summary>
internal sealed class FakeLhmComputer : ILhmComputer
{
    private readonly List<FakeLhmSensor> _sensors = new();

    public bool Opened { get; private set; }
    public int UpdateCount { get; private set; }
    public bool Disposed { get; private set; }

    public FakeLhmSensor Add(FakeLhmSensor sensor)
    {
        _sensors.Add(sensor);
        return sensor;
    }

    public void Open() => Opened = true;

    public void Update() => UpdateCount++;

    public IReadOnlyList<ILhmSensor> EnumerateSensors() => _sensors.ToArray();

    public void Dispose() => Disposed = true;
}

internal sealed class FakeLhmSensor : ILhmSensor
{
    private float? _value;

    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public required string HardwareName { get; init; }
    public required LhmSensorType Type { get; init; }

    /// <summary>Hardware-Klasse; Default <see cref="LhmHardwareType.Other"/>, sodass Bestandstests unbetroffen bleiben. Für die nur-GPU-Diagnose gezielt auf einen GPU-Typ setzen.</summary>
    public LhmHardwareType HardwareType { get; init; } = LhmHardwareType.Other;

    /// <summary>Lässt den <see cref="Value"/>-Getter werfen - simuliert einen werfenden LHM-Sensor-Getter (ReadValue muss NaN liefern, nicht werfen).</summary>
    public bool ThrowOnValueRead { get; init; }

    public float? Value
    {
        get => ThrowOnValueRead ? throw new InvalidOperationException("LHM-Read fehlgeschlagen") : _value;
        set => _value = value;
    }

    public ILhmControl? Control { get; init; }

    /// <summary>Temperatur-/Drehzahl-Sensor (kein Control).</summary>
    public static FakeLhmSensor Reading(
        string id, string name, string hardware, LhmSensorType type, float? value) =>
        new()
        {
            Identifier = id,
            Name = name,
            HardwareName = hardware,
            Type = type,
            Value = value,
        };

    /// <summary>Steuerbarer Control-Sensor (mit zustandsbehaftetem <see cref="FakeLhmControl"/>).</summary>
    public static FakeLhmSensor Controllable(
        string id, string name, string hardware, FakeLhmControl control) =>
        new()
        {
            Identifier = id,
            Name = name,
            HardwareName = hardware,
            Type = LhmSensorType.Control,
            Value = control.SoftwareValue,
            Control = control,
        };

    /// <summary>Read-only Control-Sensor (Control == null) - der reguläre „nicht steuerbar"-Zustand.</summary>
    public static FakeLhmSensor ReadOnlyControl(string id, string name, string hardware) =>
        new()
        {
            Identifier = id,
            Name = name,
            HardwareName = hardware,
            Type = LhmSensorType.Control,
            Value = null,
            Control = null,
        };
}

internal sealed class FakeLhmControl : ILhmControl
{
    private LhmControlMode _mode;
    private float _softwareValue;

    /// <summary>Lässt <see cref="SetDefault"/> werfen - simuliert einen beim Restore nicht erreichbaren Kanal (Best-Effort-Pfad).</summary>
    public bool ThrowOnSetDefault { get; init; }

    /// <summary>Lässt <see cref="SetDefault"/> ein No-op sein (Modus bleibt) - simuliert ein Board, das die Auto-Umschaltung ignoriert.</summary>
    public bool IgnoreSetDefault { get; init; }

    /// <summary>Lässt die Lese-Getter (<see cref="Mode"/>/<see cref="SoftwareValue"/>) werfen - simuliert werfende LHM-Getter (GetMode/GetPwm müssen Default liefern, nicht werfen).</summary>
    public bool ThrowOnRead { get; init; }

    public int SetDefaultCalls { get; private set; }

    public FakeLhmControl(LhmControlMode initialMode = LhmControlMode.Default, float initialValue = 0f)
    {
        _mode = initialMode;
        _softwareValue = initialValue;
    }

    public LhmControlMode Mode =>
        ThrowOnRead ? throw new InvalidOperationException("LHM-Read fehlgeschlagen") : _mode;

    public float SoftwareValue =>
        ThrowOnRead ? throw new InvalidOperationException("LHM-Read fehlgeschlagen") : _softwareValue;

    public void SetSoftware(float percent)
    {
        _mode = LhmControlMode.Software;
        _softwareValue = percent;
    }

    public void SetDefault()
    {
        if (ThrowOnSetDefault)
            throw new InvalidOperationException("Kanal beim Restore nicht erreichbar");
        SetDefaultCalls++;
        if (!IgnoreSetDefault)
            _mode = LhmControlMode.Default; // sonst bleibt der Modus (Board ignoriert die Umschaltung)
    }
}
