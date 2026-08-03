// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Hardware.Windows.Lhm;

/// <summary>
/// Schmale, plattformneutrale Naht über LibreHardwareMonitorLib (LHM). Existiert, damit
/// <see cref="WindowsLhmBackend"/> ohne den realen Kernel-Treiber (und ohne Windows) testbar bleibt:
/// Der reale Adapter <see cref="LhmComputerAdapter"/> liegt hinter dieser Naht, ein Fake tritt in den
/// Tests an seine Stelle. Bewusst <b>keine</b> LHM-Typen in den Signaturen — sonst zöge das Test-Projekt
/// die Windows-only-NuGet und der Linux-CI-Lauf risse.
/// </summary>
internal enum LhmSensorType
{
    Temperature,
    Fan,
    Control,
    Other,
}

/// <summary>Steuermodus eines LHM-Controls (gespiegelt aus <c>ControlMode</c>).</summary>
internal enum LhmControlMode
{
    Undefined,
    Software,
    Default,
}

/// <summary>
/// Grobe Hardware-Klasse eines Sensors (gespiegelt aus LHMs <c>HardwareType</c>). Dient allein der
/// Start-Diagnose (nur-GPU-Erkennung, siehe <see cref="WindowsLhmBackend"/>) — bewusst nach GPU vs.
/// „Rest" auflösend, alles Nicht-Relevante fällt auf <see cref="Other"/>.
/// </summary>
internal enum LhmHardwareType
{
    Cpu,
    GpuNvidia,
    GpuAmd,
    GpuIntel,
    Motherboard,
    SuperIO,
    Other,
}

/// <summary>
/// Eine geöffnete LHM-„Computer"-Instanz, deren Sensoren bereits über die Hardware-/SubHardware-Hierarchie
/// aufgeflacht sind (SuperIO ist SubHardware des Mainboards — siehe Stage-0-Spike). Nicht thread-sicher;
/// der Aufrufer (<see cref="WindowsLhmBackend"/>) serialisiert jeden Zugriff.
/// </summary>
internal interface ILhmComputer : IDisposable
{
    /// <summary>Öffnet die Instanz (lädt auf Windows den Kernel-Treiber).</summary>
    void Open();

    /// <summary>Rekursiver Werte-Sweep über Hardware + SubHardware. Erst danach sind <c>Value</c> aktuell.</summary>
    void Update();

    /// <summary>Alle Sensoren über die gesamte Hierarchie, bereits flach.</summary>
    IReadOnlyList<ILhmSensor> EnumerateSensors();
}

/// <summary>Ein einzelner LHM-Sensor (Temperatur, Drehzahl, Control oder Sonstiges).</summary>
internal interface ILhmSensor
{
    /// <summary>Stabiler Schlüssel (LHM-<c>Identifier</c>) — dient als <c>SensorId</c>/<c>FanId</c>.</summary>
    string Identifier { get; }

    string Name { get; }

    /// <summary>Name der besitzenden Hardware — für den Anzeigenamen und das RPM-Pairing-Scope.</summary>
    string HardwareName { get; }

    /// <summary>Klasse der besitzenden Hardware — für die Start-Diagnose (nur-GPU-Erkennung).</summary>
    LhmHardwareType HardwareType { get; }

    LhmSensorType Type { get; }

    /// <summary>Aktueller Wert; <c>null</c> bedeutet „kein Wert" (→ <see cref="double.NaN"/>).</summary>
    float? Value { get; }

    /// <summary>Steuer-Handle, falls der Kanal steuerbar ist; <c>null</c> ⇒ read-only.</summary>
    ILhmControl? Control { get; }
}

/// <summary>Steuer-Handle eines steuerbaren Kanals.</summary>
internal interface ILhmControl
{
    LhmControlMode Mode { get; }

    /// <summary>Aktueller Software-Stellwert in Prozent (0..100).</summary>
    float SoftwareValue { get; }

    /// <summary>Schaltet auf Software-Steuerung und setzt den Stellwert in Prozent (0..100).</summary>
    void SetSoftware(float percent);

    /// <summary>Gibt den Kanal an die Firmware-Automatik zurück (Fail-Safe-Ziel).</summary>
    void SetDefault();
}
