// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Abstractions;

/// <summary>
/// Steuert Lüfter. Schreibzugriffe erfordern erhöhte Rechte (Root/Admin) und laufen ausschließlich
/// im Daemon-Prozess. Plattform-Implementierungen liegen in <c>LinFan.Hardware.*</c>.
/// </summary>
/// <remarks>
/// Implementierungen müssen <see cref="SetMode"/>, <see cref="SetPwm"/> und <see cref="RestoreDefaults"/>
/// nicht-blockierend/schnell halten: Diese Aufrufe laufen im Poll-/Watchdog-Tick des Daemon (ggf. unter
/// einem gemeinsamen Lock in <c>SynchronizedFanController</c>). Ein langsames Backend (z. B. ein späteres
/// macOS-SMC-Backend) würde sonst den Watchdog-Tick aushungern und damit den Fail-Safe verzögern.
/// <para>
/// PWM-Einheit (Vertrag): Der Rohwert <b>0..255</b> ist die plattformübergreifende Lingua franca. Backends
/// mit prozentbasiertem nativem API (z. B. Windows/LHM <c>SetSoftware(percent)</c>) mappen <b>intern</b>
/// (<c>percent = round(value * 100 / 255)</c>, zurück <c>value = round(percent * 255 / 100)</c>); der
/// Vertrag bleibt einheitlich byte-typisiert.
/// </para>
/// <para>
/// Conformance: Die ausführbare Spezifikation dieses Vertrags ist <c>BackendConformanceTests</c> im
/// geteilten Test-Kit <c>LinFan.Conformance</c> (INV-1..INV-10). Ein neues Backend gilt erst als
/// vertragstreu, wenn es diese Suite besteht (sein Test-Projekt leitet die Basis ab).
/// </para>
/// </remarks>
public interface IFanController : IDisposable
{
    /// <summary>Findet alle PWM-Kanäle (auch nicht steuerbare → read-only).</summary>
    IReadOnlyList<FanDescriptor> DiscoverFans();

    /// <summary>
    /// Ob aktuell Schreibzugriff auf den Kanal besteht. Über die Instanzlebensdauer <b>stabil</b> und
    /// deckungsgleich mit <see cref="FanDescriptor.CanControl"/> derselben <paramref name="id"/>.
    /// <c>true</c> impliziert, dass <see cref="SetPwm"/>/<see cref="SetMode"/> für diesen Kanal nicht
    /// wegen fehlender Rechte werfen. Für eine bekannte (per <see cref="DiscoverFans"/> gemeldete) id wirft
    /// dieser Aufruf nicht; für eine unbekannte id darf geworfen werden.
    /// </summary>
    bool CanControl(FanId id);

    /// <summary>
    /// Aktueller Steuermodus. Für eine bekannte id wirft dieser Aufruf nie - ein nicht ermittelbarer Modus
    /// fällt auf <see cref="FanMode.Auto"/> (sicherer Default) zurück. Für eine unbekannte id darf geworfen werden.
    /// </summary>
    FanMode GetMode(FanId id);

    void SetMode(FanId id, FanMode mode);

    /// <summary>
    /// Aktueller Rohwert 0..255. Für eine bekannte id wirft dieser Aufruf nie - ein nicht lesbarer Wert
    /// fällt auf einen Default (z. B. 0) zurück. Für eine unbekannte id darf geworfen werden.
    /// </summary>
    byte GetPwm(FanId id);

    /// <summary>
    /// Setzt den Rohwert 0..255. Schaltet selbsttätig auf <see cref="FanMode.Manual"/> - der Aufrufer muss
    /// <b>nicht</b> vorher <see cref="SetMode"/> rufen (sonst überschriebe die Firmware den Wert). Prozent-
    /// basierte Backends mappen den Rohwert intern (siehe Typ-Doc).
    /// </summary>
    void SetPwm(FanId id, byte value);

    /// <summary>
    /// Fail-Safe: bringt <b>jeden</b> steuerbaren Kanal in einen kühlungs-sicheren Zustand - Hardware-Auto
    /// (Firmware regelt selbst), ersatzweise Volllast (255), wenn der Kanal keinen Auto-Modus kennt.
    /// <para>
    /// Bewusst <b>unabhängig</b> vom bei Discovery gelesenen Zustand: der kann Manual/niedrig gewesen sein
    /// (z. B. weil ein früherer Lauf abgestürzt ist) und würde den Lüfter ohne aktiven Watchdog dort
    /// festhalten - genau der gefährliche Fall.
    /// </para>
    /// <para>
    /// Garantien: <b>best-effort über alle Kanäle</b> (ein fehlschlagender Kanal überspringt die übrigen
    /// nicht), <b>wirft nicht</b> und ist <b>idempotent / nach <see cref="IDisposable.Dispose"/> wiederholbar</b>.
    /// </para>
    /// </summary>
    void RestoreDefaults();
}
