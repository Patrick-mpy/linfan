// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;

namespace LinFan.Hardware.Windows.Lhm;

/// <summary>
/// Realer Adapter der <see cref="ILhmComputer"/>-Naht über <see cref="Computer"/> aus
/// LibreHardwareMonitorLib. Kapselt Computer-Setup, das Treiber-Laden (<see cref="Computer.Open"/>),
/// den rekursiven Update-Sweep und das Aufflachen von Hardware + SubHardware (SuperIO sitzt als
/// SubHardware am Mainboard — verifiziert im Stage-0-Spike). Nur dieser Typ berührt LHM-Typen direkt;
/// er läuft ausschließlich auf Windows (Kernel-Treiber).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class LhmComputerAdapter : ILhmComputer
{
    private readonly Computer _computer = new()
    {
        // Genau die Klassen mit Lüftern/PWM: SuperIO am Mainboard (Motherboard/Controller),
        // CPU/GPU für Temperaturen und GPU-Lüfter. Rest aus — nur Latenz/Rauschen.
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = false,
        IsStorageEnabled = false,
        IsNetworkEnabled = false,
        IsPsuEnabled = false,
        IsBatteryEnabled = false,
    };

    private bool _opened;

    public void Open()
    {
        _computer.Open();
        _opened = true;
    }

    public void Update()
    {
        if (!_opened)
            return;
        foreach (var hw in _computer.Hardware)
            UpdateRecursive(hw);
    }

    public IReadOnlyList<ILhmSensor> EnumerateSensors()
    {
        var result = new List<ILhmSensor>();
        foreach (var hw in Flatten(_computer.Hardware))
        {
            var hwType = Map(hw.HardwareType);
            foreach (var sensor in hw.Sensors)
                result.Add(new SensorAdapter(sensor, hw.Name, hwType));
        }
        return result;
    }

    public void Dispose()
    {
        try
        {
            _computer.Close();
        }
        catch
        {
            // best-effort: ein scheiterndes Close darf den Shutdown-Pfad nicht reißen.
        }
    }

    // LHM aktualisiert Werte erst nach Update(); SubHardware muss mit aktualisiert werden.
    private static void UpdateRecursive(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware)
            UpdateRecursive(sub);
    }

    // Flacht Hardware + SubHardware in eine Sequenz auf (SuperIO ist SubHardware des Mainboards).
    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> roots)
    {
        foreach (var hw in roots)
        {
            yield return hw;
            foreach (var sub in Flatten(hw.SubHardware))
                yield return sub;
        }
    }

    private static LhmSensorType Map(SensorType type) => type switch
    {
        SensorType.Temperature => LhmSensorType.Temperature,
        SensorType.Fan => LhmSensorType.Fan,
        SensorType.Control => LhmSensorType.Control,
        _ => LhmSensorType.Other,
    };

    private static LhmControlMode Map(ControlMode mode) => mode switch
    {
        ControlMode.Software => LhmControlMode.Software,
        ControlMode.Default => LhmControlMode.Default,
        _ => LhmControlMode.Undefined,
    };

    private static LhmHardwareType Map(HardwareType type) => type switch
    {
        HardwareType.Cpu => LhmHardwareType.Cpu,
        HardwareType.GpuNvidia => LhmHardwareType.GpuNvidia,
        HardwareType.GpuAmd => LhmHardwareType.GpuAmd,
        HardwareType.GpuIntel => LhmHardwareType.GpuIntel,
        HardwareType.Motherboard => LhmHardwareType.Motherboard,
        HardwareType.SuperIO => LhmHardwareType.SuperIO,
        _ => LhmHardwareType.Other,
    };

    private sealed class SensorAdapter : ILhmSensor
    {
        private readonly ISensor _sensor;

        public SensorAdapter(ISensor sensor, string hardwareName, LhmHardwareType hardwareType)
        {
            _sensor = sensor;
            HardwareName = hardwareName;
            HardwareType = hardwareType;
            Control = sensor.Control is { } c ? new ControlAdapter(c) : null;
        }

        public string Identifier => _sensor.Identifier.ToString();
        public string Name => _sensor.Name;
        public string HardwareName { get; }
        public LhmHardwareType HardwareType { get; }
        public LhmSensorType Type => Map(_sensor.SensorType);
        public float? Value => _sensor.Value;
        public ILhmControl? Control { get; }
    }

    private sealed class ControlAdapter : ILhmControl
    {
        private readonly IControl _control;

        public ControlAdapter(IControl control) => _control = control;

        public LhmControlMode Mode => Map(_control.ControlMode);
        public float SoftwareValue => _control.SoftwareValue;
        public void SetSoftware(float percent) => _control.SetSoftware(percent);
        public void SetDefault() => _control.SetDefault();
    }
}
