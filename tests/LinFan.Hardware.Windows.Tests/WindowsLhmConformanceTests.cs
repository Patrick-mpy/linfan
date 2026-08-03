// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Conformance;
using LinFan.Hardware.Windows.Lhm;

namespace LinFan.Hardware.Windows.Tests;

/// <summary>
/// Wendet die geteilte Conformance-Suite (INV-1..INV-10) auf das echte <see cref="WindowsLhmBackend"/>
/// an — über ein Fake-LHM injiziert, daher ohne Kernel-Treiber/Admin und auf jedem OS lauffähig. Das ist
/// der Vertragstreue-Beweis des Windows-Backends, parallel zur Referenz-/Linux-Verankerung.
/// <para>
/// Round-Trip-Toleranz 3: Das Backend mappt 0..255 verlustbehaftet auf LHM-Prozente (0..100) und zurück.
/// </para>
/// </summary>
public sealed class WindowsLhmConformanceTests : BackendConformanceTests
{
    protected override int PwmRoundTripTolerance => 3;

    protected override BackendUnderTest CreateBackend()
    {
        var lhm = new FakeLhmComputer();

        // Steuerbare Controls — starten in Default (= Auto), damit INV-4 die Ausgangsannahme erfüllt.
        // Gepaart mit RPM-Sensoren derselben Hardware über den Namens-Index (#1/#2).
        lhm.Add(FakeLhmSensor.Controllable("nct/control/1", "Fan Control #1", "Nuvoton NCT6797D", new FakeLhmControl()));
        lhm.Add(FakeLhmSensor.Controllable("nct/control/2", "Fan Control #2", "Nuvoton NCT6797D", new FakeLhmControl()));

        // Read-only Fan-Kanal (Control == null) — der reguläre „nicht steuerbar"-Zustand.
        lhm.Add(FakeLhmSensor.ReadOnlyControl("nct/control/3", "Fan Control #3", "Nuvoton NCT6797D"));

        // RPM-Sensoren (mit Wert) — #1/#2 paaren zu den Controls, #3 read-only.
        lhm.Add(FakeLhmSensor.Reading("nct/fan/1", "Fan #1", "Nuvoton NCT6797D", LhmSensorType.Fan, 1200f));
        lhm.Add(FakeLhmSensor.Reading("nct/fan/2", "Fan #2", "Nuvoton NCT6797D", LhmSensorType.Fan, 980f));

        // Temperatur mit Wert + ein Sensor OHNE Wert (Value == null) → NaN-Fall (INV-6).
        lhm.Add(FakeLhmSensor.Reading("cpu/temperature/0", "CPU Package", "AMD Ryzen", LhmSensorType.Temperature, 47.5f));
        lhm.Add(FakeLhmSensor.Reading("cpu/temperature/9", "CPU CCD2", "AMD Ryzen", LhmSensorType.Temperature, null));

        var backend = new WindowsLhmBackend(lhm);
        return new BackendUnderTest(backend, backend, backend);
    }
}
