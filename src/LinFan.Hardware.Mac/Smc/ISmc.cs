// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Hardware.Mac.Smc;

/// <summary>
/// Schmale, plattformneutrale Naht über den AppleSMC-Zugriff via IOKit. Existiert - analog zu
/// <c>ILhm</c> im Windows-Backend -, damit <see cref="MacSmcBackend"/> ohne echten SMC (und ohne macOS)
/// testbar bleibt: Der reale Adapter <see cref="AppleSmc"/> liegt hinter dieser Naht, ein Fake tritt in
/// den Tests an seine Stelle. Bewusst <b>keine</b> IOKit-Typen in den Signaturen - Keys sind 4-Zeichen-
/// Strings, Werte rohe <see cref="SmcValue"/> - sonst zöge das Test-Projekt Plattform-Interop und der
/// CI-Lauf auf Nicht-macOS risse.
/// <para>
/// Der Aufrufer (<see cref="MacSmcBackend"/>) serialisiert jeden Zugriff; Implementierungen müssen
/// <b>nicht</b> selbst thread-sicher sein, aber schnell/nicht-blockierend (Poll-/Watchdog-Tick).
/// </para>
/// </summary>
internal interface ISmc : IDisposable
{
    /// <summary>Öffnet die Verbindung zum SMC (öffnet auf macOS den IOKit-UserClient). Lesen braucht kein Root.</summary>
    void Open();

    /// <summary>
    /// Liest einen SMC-Key. Liefert <c>false</c>, wenn der Key nicht existiert oder gerade nicht lesbar ist
    /// (kein Wurf - der Aufrufer behandelt das als „nicht vorhanden/kein Wert"). <paramref name="key"/> ist
    /// der 4-Zeichen-Code (z. B. <c>F0Ac</c>).
    /// </summary>
    bool TryReadKey(string key, out SmcValue value);

    /// <summary>
    /// Schreibt einen SMC-Key (Steuer-Pfad, braucht Root). Liefert <c>false</c>, wenn der Write scheitert
    /// (fehlende Rechte, Key unbekannt, Firmware verweigert) - kein Wurf, damit der Fail-Safe-/Best-Effort-
    /// Pfad in <see cref="MacSmcBackend"/> nie durch eine Exception unterbrochen wird.
    /// </summary>
    bool TryWriteKey(string key, SmcValue value);
}
