// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Abstractions;

/// <summary>
/// Liest Sensoren (Temperaturen, Drehzahlen). Lesen erfordert i. d. R. keine erhöhten Rechte.
/// Plattform-Implementierungen liegen in <c>LinFan.Hardware.*</c>.
/// </summary>
/// <remarks>
/// <see cref="DiscoverSensors"/> und <see cref="ReadValue"/> müssen - wie der Steuer-Pfad (siehe
/// <see cref="IFanController"/>) - nicht-blockierend/schnell sein: Der Sensor-Pfad läuft im selben
/// Poll-/Watchdog-Tick. <see cref="ReadValue"/> läuft zudem <b>nicht</b> durch das Fan-Lock von
/// <c>SynchronizedFanController</c> und muss daher nebenläufig zu Fan-Writes sicher sein.
/// Conformance: <c>BackendConformanceTests</c> im geteilten Test-Kit <c>LinFan.Conformance</c>.
/// </remarks>
public interface ISensorBackend : IDisposable
{
    /// <summary>Findet alle auslesbaren Kanäle. Wiederholte Aufrufe liefern stabile <see cref="SensorId"/>.</summary>
    IReadOnlyList<SensorDescriptor> DiscoverSensors();

    /// <summary>
    /// Liest den aktuellen Wert (°C bzw. RPM). Für eine bekannte (per <see cref="DiscoverSensors"/>
    /// gemeldete) id liefert dieser Aufruf <b>immer</b> einen <see cref="double"/> und wirft <b>nie</b>:
    /// Ist der Kanal gerade nicht lesbar (z. B. EIO), wird <see cref="double.NaN"/> zurückgegeben -
    /// Aufrufer behandeln dies als „kein Wert", nicht als Fehler. Für eine unbekannte id darf geworfen werden.
    /// </summary>
    double ReadValue(SensorId id);
}
