// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Conformance;

namespace LinFan.Hardware.Mac.Tests;

/// <summary>
/// Wendet die geteilte Conformance-Suite (INV-1..INV-10) auf das echte <see cref="MacSmcBackend"/> an -
/// über ein Fake-SMC injiziert, daher ohne IOKit/Root und auf jedem OS lauffähig. Vertragstreue-Beweis
/// des macOS-Backends, parallel zur Referenz-/Linux-/Windows-Verankerung.
/// <para>
/// Steuerbarkeit wird hier explizit <c>true</c> gesetzt (auf realer Hardware entscheidet Architektur +
/// Rechte). Round-Trip-Toleranz 2: <c>SetPwm</c> mappt 0..255 → Ziel-Drehzahl (RPM) und zurück, was durch
/// das Runden minimal verlustbehaftet ist.
/// </para>
/// </summary>
public sealed class MacSmcConformanceTests : BackendConformanceTests
{
    protected override int PwmRoundTripTolerance => 2;

    protected override BackendUnderTest CreateBackend()
    {
        var smc = new FakeSmc();
        smc.SetUi8("FNum", 2);

        // Fan 0 - steuerbar: Ist-/Ziel-Drehzahl, Modus (startet Auto ⇒ INV-4-Vorbedingung), Grenzen.
        smc.SetFloat("F0Ac", 1200f);
        smc.SetFloat("F0Tg", 1200f);
        smc.SetUi8("F0Md", 0);
        smc.SetFloat("F0Mn", 1200f);
        smc.SetFloat("F0Mx", 5000f);

        // Fan 1 - nicht steuerbar (keine Tg/Md-Keys). F1Ac absichtlich zu kurz ⇒ Decode NaN
        // (der geforderte NaN-fähige Sensor: ein bekannter Kanal, der gerade keinen Wert liefert).
        smc.Set("F1Ac", "flt ", new byte[] { 0, 0 });

        // Ein lesbarer Temperatur-Sensor (kuratierter Key).
        smc.SetFloat("TC0P", 45.5f);

        var backend = new MacSmcBackend(smc, new MacSmcBackend.ControlCapability(true, null));
        return new BackendUnderTest(backend, backend, backend);
    }
}
