// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Conformance;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.Core.Tests.Conformance;

/// <summary>
/// Negativ-Tests: Sie beweisen, dass die Conformance-Suite Verletzungen tatsächlich <b>fängt</b> (nicht nur
/// beschreibt). Jeder Test füttert die Invariante mit einem absichtlich vertragswidrigen Backend und prüft,
/// dass die zugehörige Assertion reißt.
/// </summary>
public sealed class ConformanceSuiteCatchesViolationsTests
{
    /// <summary>Subklasse, die ein BUGGY Backend einhängt: RestoreDefaults stellt den Discovery-Zustand wieder her.</summary>
    private sealed class DiscoveryStateProbe : BackendConformanceTests
    {
        protected override BackendUnderTest CreateBackend()
        {
            var fans = new DiscoveryStateRestoreBackend();
            fans.AddFan("f", initialPwm: 1, initialMode: FanMode.Manual); // „Ausgangszustand": niedrig + Manual
            var sensors = new ConformanceReferenceBackend();
            sensors.AddSensor("t", SensorKind.Temperature, double.NaN, unit: "°C"); // NaN-fähig (für andere INVs)
            return new BackendUnderTest(sensors, fans, fans);
        }

        public void RunInv1() => Inv1_RestoreDefaults_LeavesControllableChannelsSafe_NotPreviousLowPwm();
    }

    [Fact]
    public void Inv1_Fails_For_DiscoveryStateRestore_Backend()
    {
        // Das gefährliche Backend setzt nach RestoreDefaults wieder pwm=1/Manual - INV-1 MUSS das reißen.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => new DiscoveryStateProbe().RunInv1());
    }

    /// <summary>Subklasse, die ein zu langsames Backend mit sehr enger Latenz-Schranke einhängt.</summary>
    private sealed class SlowProbe : BackendConformanceTests
    {
        private readonly SlowBackend _backend = new() { Latency = TimeSpan.FromMilliseconds(80) };

        protected override TimeSpan MaxCallLatency => TimeSpan.FromMilliseconds(20); // Backend überschreitet das bewusst

        protected override BackendUnderTest CreateBackend() => new(_backend, _backend, _backend);

        public void RunInv7() => Inv7_AllContractCalls_StayUnderLatencyBound();
    }

    [Fact]
    public void Inv7_Fails_For_SlowBackend()
    {
        // Ein blockierendes Backend (80 ms/Call) muss die Latenz-Schranke (20 ms) reißen.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => new SlowProbe().RunInv7());
    }

    /// <summary>
    /// Subklasse, die den extrahierten INV-9-Hammer gegen ein NICHT thread-sicheres Backend laufen lässt.
    /// Wichtig: INV-9 ist gegen das (über ein einzelnes Gate serialisierte) Referenz-Backend tautologisch grün
    /// und beweist damit allein NICHT, dass die Suite ein racy Backend fängt. Dieser Negativ-Beweis schließt
    /// die Lücke: <see cref="RacyFanController"/> teilt ungeschützten Lese-/Schreibzustand und muss unter dem
    /// Hammer reißen.
    /// </summary>
    private sealed class RacyProbe : BackendConformanceTests
    {
        private readonly RacyFanController _backend = new();

        protected override BackendUnderTest CreateBackend() => new(_backend, _backend, _backend);

        /// <summary>Führt den exakt gleichen Hammer wie INV-9 aus und liefert die gesammelten Worker-Fehler.</summary>
        public IReadOnlyList<Exception> RunHammer()
        {
            var sensorId = _backend.DiscoverSensors().First().Id;
            var fanId = _backend.DiscoverFans().First(f => f.CanControl).Id;
            return HammerConcurrently(_backend, _backend, sensorId, fanId);
        }
    }

    [Fact]
    public void Inv9_Fails_For_RacyBackend()
    {
        // Das racy Backend (ungeschützter geteilter Zustand zwischen ReadValue und Fan-Writes) MUSS unter dem
        // nebenläufigen Hammer reißen - INV-9 prüft `Assert.Empty(failures)`, genau diese Assertion würde hier
        // also fehlschlagen. Eine Data-Race ist aber nicht in JEDEM Lauf sichtbar (Scheduling-Glück), darum den
        // Hammer bis zu mehrmals wiederholen: schon EIN reißender Lauf beweist, dass die Suite ein racy Backend
        // fängt. Reißt KEINER von vielen Läufen, ist das ein echter Befund (Race nicht auslösbar) → Test rot.
        const int maxAttempts = 50;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            IReadOnlyList<Exception> failures = new RacyProbe().RunHammer();
            // „Collection was modified" (InvalidOperationException) aus dem foreach über die geteilte Liste.
            if (failures.Any(e => e is InvalidOperationException))
                return; // Race ausgelöst → INV-9 (Assert.Empty) reißt für dieses Backend. Beweis erbracht.
        }

        Assert.Fail($"Die Race im RacyFanController wurde in {maxAttempts} Hammer-Läufen nie ausgelöst - " +
                    "INV-9 könnte ein nicht thread-sicheres Backend übersehen.");
    }
}
