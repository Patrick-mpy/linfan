// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Conformance;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.Core.Tests.Conformance;

/// <summary>
/// Wendet die gesamte Conformance-Suite auf das hardwarefreie <see cref="ConformanceReferenceBackend"/> an.
/// Das ist die primäre, in CI deterministisch laufende Verankerung des Vertrags: Das Referenz-Backend MUSS
/// jede Invariante erfüllen.
/// </summary>
public sealed class ReferenceBackendConformanceTests : BackendConformanceTests
{
    protected override BackendUnderTest CreateBackend()
    {
        var backend = new ConformanceReferenceBackend();

        // Vom Hook gefordertes Mindest-Szenario.
        backend.AddSensor("temp/ok", SensorKind.Temperature, 42.0, unit: "°C");
        backend.AddSensor("temp/eio", SensorKind.Temperature, double.NaN, unit: "°C"); // NaN-fähig
        backend.AddSensor("fan/rpm", SensorKind.FanRpm, 1500, unit: "RPM");

        backend.AddFan("fan/ctl", canControl: true, tachometer: new SensorId("fan/rpm"));
        backend.AddFan("fan/ctl2", canControl: true);
        backend.AddFan("fan/readonly", canControl: false); // nicht steuerbar = regulärer Zustand
        backend.AddFullLoadOnlyFan("fan/noauto"); // steuerbar OHNE Auto-Modus → RestoreDefaults = Volllast 255

        return new BackendUnderTest(backend, backend, backend);
    }
}
