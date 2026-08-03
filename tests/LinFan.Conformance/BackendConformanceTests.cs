// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.Conformance;

/// <summary>
/// Ausführbare Spezifikation des Backend-Vertrags (<see cref="ISensorBackend"/> + <see cref="IFanController"/>).
/// Sie nagelt die Fail-Safe-relevanten Verhaltenszusagen als Invarianten INV-1..INV-10 fest, die die
/// Doku in <c>Abstractions/</c> beschreibt. Ein vertragstreues-aber-verhaltensabweichendes Backend fällt hier durch.
/// <para>
/// Ein neues Backend leitet diese Basis in seinem <em>eigenen</em> Test-Projekt ab und liefert die Hooks.
/// Beispiel (Linux — heute über <c>FakeHardware</c>/Referenz abgedeckt, ein echtes HW-Test-Projekt sähe so aus):
/// <code>
/// public sealed class LinuxHwmonConformanceTests : BackendConformanceTests
/// {
///     protected override BackendUnderTest CreateBackend()
///     {
///         var backend = new LinuxHwmonBackend(); // ISensorBackend + IFanController in einem
///         return new BackendUnderTest(backend, backend, backend);
///     }
/// }
/// </code>
/// Core.Tests referenziert <c>LinFan.Hardware.*</c> bewusst NICHT (Schichtgrenze) — der Linux/Windows-
/// Beweis lebt in <c>Hardware.Linux.Tests</c> bzw. später <c>Hardware.Windows.Tests</c>. Das hiesige
/// <see cref="ConformanceReferenceBackend"/> hält die Spezifikation hardwarefrei in CI grün.
/// </para>
/// </summary>
public abstract class BackendConformanceTests
{
    /// <summary>Das zu prüfende Backend plus die Test-Hooks, die zum Aufbau der Szenarien nötig sind.</summary>
    /// <param name="Sensors">Sensor-Rolle des Backends.</param>
    /// <param name="Fans">Fan-Steuer-Rolle des Backends.</param>
    /// <param name="Disposable">Gemeinsam zu entsorgendes Objekt (oft = Backend selbst, eine Instanz für beide Rollen).</param>
    protected sealed record BackendUnderTest(ISensorBackend Sensors, IFanController Fans, IDisposable Disposable);

    /// <summary>
    /// Liefert ein einsatzbereites Backend mit mindestens: einem steuerbaren Lüfter, einem nicht-steuerbaren
    /// Lüfter, einem lesbaren Sensor und einem Sensor, der gerade <see cref="double.NaN"/> liefert.
    /// </summary>
    protected abstract BackendUnderTest CreateBackend();

    /// <summary>Obere Zeitschranke für einen einzelnen Vertrags-Call (INV-7). Großzügig für CI-Jitter.</summary>
    protected virtual TimeSpan MaxCallLatency => TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Erlaubte Abweichung beim PWM-Round-Trip (<c>SetPwm(v)</c> → <c>GetPwm()</c>) in INV-4. <b>0</b> für
    /// exakte Backends (Linux sysfs, Referenz). Ein Backend, das intern verlustbehaftet auf Prozent mappt
    /// (Windows/LHM: 0..255 → % → 0..255), überschreibt das mit z. B. <b>3</b>. Betrifft nur den exakten
    /// Round-Trip-Wert — die Sicherheits-Asserts (Auto bzw. exakt 255, das auch über Prozent verlustfrei
    /// zurückkommt) bleiben bewusst exakt.
    /// </summary>
    protected virtual int PwmRoundTripTolerance => 0;

    // --- Auswahl-Helfer: erster steuerbarer / nicht-steuerbarer Kanal --------

    private static FanId FirstControllable(IFanController fans) =>
        fans.DiscoverFans().First(f => f.CanControl).Id;

    // === INV-1: RestoreDefaults landet kühlungs-sicher, NIE auf dem vorherigen niedrigen Wert ==========

    [Fact]
    public void Inv1_RestoreDefaults_LeavesControllableChannelsSafe_NotPreviousLowPwm()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        foreach (var fan in b.Fans.DiscoverFans().Where(f => f.CanControl))
            b.Fans.SetPwm(fan.Id, 1); // niedrig: Lüfter quasi aus

        b.Fans.RestoreDefaults();

        foreach (var fan in b.Fans.DiscoverFans().Where(f => f.CanControl))
        {
            var mode = b.Fans.GetMode(fan.Id);
            byte pwm = b.Fans.GetPwm(fan.Id);

            bool safe = mode == FanMode.Auto || pwm == 255;
            Assert.True(safe, $"Kanal {fan.Id} nach RestoreDefaults weder Auto (war {mode}) noch Volllast (pwm {pwm}).");

            // Explizit: NIE auf dem vorher gesetzten niedrigen Wert hängen geblieben (der gefährliche Fall).
            if (mode == FanMode.Manual)
                Assert.NotEqual((byte)1, pwm);
        }
    }

    // === INV-2: best-effort über mehrere Kanäle, ein Write-Fehler stoppt die übrigen nicht ============
    // Mit einem eigens fehlerinjizierenden Backend (ein Kanal wirft beim Write) statt CreateBackend().

    [Fact]
    public void Inv2_RestoreDefaults_IsBestEffort_AcrossChannels_AndDoesNotThrow()
    {
        var (_, fans) = FaultBackends.WithOneFailingWrite();
        using var _ = fans;

        var ok = new FanId("ok");
        var broken = new FanId("broken");
        fans.SetPwm(ok, 1); // gesunder Kanal niedrig

        var ex = Record.Exception(() => fans.RestoreDefaults());
        Assert.Null(ex); // wirft nicht, obwohl ein Kanal beim Write scheitert

        // Der gesunde Kanal landet sicher, obwohl der kaputte davor/danach scheiterte.
        Assert.True(fans.GetMode(ok) == FanMode.Auto || fans.GetPwm(ok) == 255);
        Assert.True(fans.CanControl(broken)); // der kaputte Kanal bleibt sichtbar/bekannt
    }

    // === INV-2b: Dispose stellt den sicheren Zustand best-effort her und WIRFT NIE — auch wenn ein =====
    // Kanal beim Restore-Write fehlschlägt. Genau der Shutdown-Pfad (using/SIGTERM), der den Fail-Safe
    // garantiert erreichen muss; eine durchschlagende Exception aus Dispose würde ihn vereiteln.

    [Fact]
    public void Inv2b_Dispose_DoesNotThrow_EvenIfAChannelFailsToRestore()
    {
        var (_, fans) = FaultBackends.WithOneFailingWrite();

        var ok = new FanId("ok");
        var broken = new FanId("broken");
        fans.SetPwm(ok, 1); // gesunder Kanal niedrig — Dispose muss ihn trotzdem sicher hinterlassen

        var ex = Record.Exception(() => fans.Dispose());
        Assert.Null(ex); // Dispose wirft nicht, obwohl der kaputte Kanal beim Restore-Write scheitert

        // Best-effort erfüllt: der gesunde Kanal ist nach Dispose sicher (Auto oder Volllast).
        Assert.True(fans.GetMode(ok) == FanMode.Auto || fans.GetPwm(ok) == 255);
    }

    // === INV-3: idempotent + nach Dispose wiederholbar, Endzustand sicher =============================

    [Fact]
    public void Inv3_RestoreDefaults_And_Dispose_AreIdempotent_NoThrow()
    {
        var b = CreateBackend();

        var controllable = b.Fans.DiscoverFans().Where(f => f.CanControl).Select(f => f.Id).ToArray();
        foreach (var id in controllable)
            b.Fans.SetPwm(id, 1);

        var ex = Record.Exception(() =>
        {
            b.Fans.RestoreDefaults();
            b.Fans.RestoreDefaults();
            b.Disposable.Dispose();
            b.Disposable.Dispose();
            // Vertrag: RestoreDefaults ist „nach Dispose wiederholbar" — NACH Dispose erneut aufrufen
            // darf NICHT werfen (ein Shutdown-Pfad kann den Fail-Safe nach dem Dispose noch anstoßen).
            b.Fans.RestoreDefaults();
        });

        Assert.Null(ex);

        // Endzustand bleibt sicher, auch nach dem Restore nach Dispose: jeder steuerbare Kanal Auto oder 255.
        foreach (var id in controllable)
        {
            var mode = b.Fans.GetMode(id);
            byte pwm = b.Fans.GetPwm(id);
            Assert.True(mode == FanMode.Auto || pwm == 255,
                $"Kanal {id} nach RestoreDefaults-nach-Dispose weder Auto (war {mode}) noch Volllast (pwm {pwm}).");
        }
    }

    // === INV-4: SetPwm erzwingt Manual, ohne vorheriges SetMode =======================================

    [Fact]
    public void Inv4_SetPwm_ForcesManual_WithoutPriorSetMode()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        var id = FirstControllable(b.Fans);
        Assert.Equal(FanMode.Auto, b.Fans.GetMode(id)); // Ausgangsannahme: steuerbarer Kanal startet in Auto

        b.Fans.SetPwm(id, 123); // KEIN SetMode(Manual) davor

        Assert.Equal(FanMode.Manual, b.Fans.GetMode(id));
        // Round-Trip soweit lesbar; verlustbehaftete (Prozent-)Backends innerhalb der Toleranz (siehe Hook).
        Assert.InRange((int)b.Fans.GetPwm(id), 123 - PwmRoundTripTolerance, 123 + PwmRoundTripTolerance);
    }

    // === INV-5: Discovery ↔ Steuerung-Konsistenz; wiederholte Discovery liefert stabile IDs ===========

    [Fact]
    public void Inv5_EveryDiscoveredId_IsValidAcrossAllCalls()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        foreach (var sensor in b.Sensors.DiscoverSensors())
            Assert.False(double.IsPositiveInfinity(b.Sensors.ReadValue(sensor.Id))); // gültig (Wert oder NaN), kein Throw

        foreach (var fan in b.Fans.DiscoverFans())
        {
            // Alle Lese-Abfragen sind für jede gemeldete id gültig.
            _ = b.Fans.CanControl(fan.Id);
            _ = b.Fans.GetMode(fan.Id);
            _ = b.Fans.GetPwm(fan.Id);
            if (fan.CanControl)
            {
                var ex = Record.Exception(() => b.Fans.SetPwm(fan.Id, 100));
                Assert.Null(ex);
            }
        }
    }

    [Fact]
    public void Inv5_Discovery_YieldsStableIds_AcrossRepeatedCalls()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        var fans1 = b.Fans.DiscoverFans().Select(f => f.Id.Value).OrderBy(x => x, StringComparer.Ordinal);
        var fans2 = b.Fans.DiscoverFans().Select(f => f.Id.Value).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(fans1, fans2);

        var s1 = b.Sensors.DiscoverSensors().Select(s => s.Id.Value).OrderBy(x => x, StringComparer.Ordinal);
        var s2 = b.Sensors.DiscoverSensors().Select(s => s.Id.Value).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(s1, s2);
    }

    // === INV-6: bekannter Sensor-Kanal wirft nie, liefert immer double (auch NaN) =====================

    [Fact]
    public void Inv6_ReadValue_KnownChannel_NeverThrows_ReturnsDouble()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        foreach (var sensor in b.Sensors.DiscoverSensors())
        {
            double value = double.NegativeInfinity;
            var ex = Record.Exception(() => value = b.Sensors.ReadValue(sensor.Id));
            Assert.Null(ex);
            Assert.False(double.IsPositiveInfinity(value)); // ein double (ggf. NaN), kein „nie geschrieben"-Sentinel
        }
    }

    [Fact]
    public void Inv6_ReadValue_NaNCapableChannel_YieldsNaN_NotException()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        // Mindestens ein Sensor liefert laut Vertrag des CreateBackend NaN (gerade nicht lesbar).
        bool sawNaN = b.Sensors.DiscoverSensors().Any(s => double.IsNaN(b.Sensors.ReadValue(s.Id)));
        Assert.True(sawNaN, "CreateBackend muss einen NaN-fähigen Sensor bereitstellen (Vertrag der Hook).");
    }

    // === INV-7 (Windows-Risiko): jeder Vertrags-Call unter der Latenz-Obergrenze ======================

    [Fact]
    public void Inv7_AllContractCalls_StayUnderLatencyBound()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        var sensorId = b.Sensors.DiscoverSensors().First().Id;
        var fanId = FirstControllable(b.Fans);

        AssertUnderBound(() => b.Sensors.DiscoverSensors(), nameof(ISensorBackend.DiscoverSensors));
        AssertUnderBound(() => b.Sensors.ReadValue(sensorId), nameof(ISensorBackend.ReadValue));
        AssertUnderBound(() => b.Fans.DiscoverFans(), nameof(IFanController.DiscoverFans));
        AssertUnderBound(() => b.Fans.CanControl(fanId), nameof(IFanController.CanControl));
        AssertUnderBound(() => b.Fans.GetMode(fanId), nameof(IFanController.GetMode));
        AssertUnderBound(() => b.Fans.GetPwm(fanId), nameof(IFanController.GetPwm));
        AssertUnderBound(() => b.Fans.SetPwm(fanId, 100), nameof(IFanController.SetPwm));
        AssertUnderBound(() => b.Fans.SetMode(fanId, FanMode.Auto), nameof(IFanController.SetMode));
        AssertUnderBound(() => b.Fans.RestoreDefaults(), nameof(IFanController.RestoreDefaults));
    }

    private void AssertUnderBound(Action call, string name)
    {
        var sw = Stopwatch.StartNew();
        call();
        sw.Stop();
        Assert.True(sw.Elapsed <= MaxCallLatency,
            $"{name} brauchte {sw.ElapsedMilliseconds} ms (> {MaxCallLatency.TotalMilliseconds} ms) — blockierendes Backend?");
    }

    // === INV-8: CanControl stabil + deckungsgleich mit FanDescriptor; true ⇒ SetPwm wirft nicht ========

    [Fact]
    public void Inv8_CanControl_IsStable_AndMatchesDescriptor()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        foreach (var fan in b.Fans.DiscoverFans())
        {
            bool first = b.Fans.CanControl(fan.Id);
            Assert.Equal(fan.CanControl, first); // deckungsgleich mit dem Descriptor-Feld

            for (int i = 0; i < 5; i++)
                Assert.Equal(first, b.Fans.CanControl(fan.Id)); // stabil über die Instanzlebensdauer
        }
    }

    [Fact]
    public void Inv8_CanControlTrue_Implies_SetPwm_DoesNotThrow()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        foreach (var fan in b.Fans.DiscoverFans().Where(f => f.CanControl))
        {
            var ex = Record.Exception(() => b.Fans.SetPwm(fan.Id, 128));
            Assert.Null(ex);
        }
    }

    // === INV-10: GetPwm/GetMode für bekannte id werfen nie (Fallback) =================================

    [Fact]
    public void Inv10_GetPwm_GetMode_KnownId_NeverThrow()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        foreach (var fan in b.Fans.DiscoverFans()) // explizit AUCH die nicht-steuerbaren Kanäle
        {
            var exMode = Record.Exception(() => b.Fans.GetMode(fan.Id));
            var exPwm = Record.Exception(() => b.Fans.GetPwm(fan.Id));
            Assert.Null(exMode);
            Assert.Null(exPwm);
        }
    }

    // === INV-9 (Windows-Risiko): nebenläufiges Hämmern ohne Crash/State-Korruption ====================
    // Hintergrund: ReadValue läuft NICHT durch das Fan-Lock — muss parallel zu Fan-Writes sicher sein.

    [Fact]
    public void Inv9_ConcurrentReadValue_SetPwm_RestoreDefaults_DoNotCrash()
    {
        var b = CreateBackend();
        using var lifetime = b.Disposable;

        var sensorId = b.Sensors.DiscoverSensors().First().Id;
        var fanId = FirstControllable(b.Fans);

        var failures = HammerConcurrently(b.Sensors, b.Fans, sensorId, fanId);

        Assert.Empty(failures);

        // Endzustand konsistent: bekannter Kanal weiter abfragbar, Sensor weiter lesbar.
        Assert.False(double.IsPositiveInfinity(b.Sensors.ReadValue(sensorId)));
        _ = b.Fans.GetMode(fanId);
        _ = b.Fans.GetPwm(fanId);
    }

    /// <summary>
    /// Hämmert <see cref="ISensorBackend.ReadValue"/> nebenläufig zu <see cref="IFanController.SetPwm"/>/
    /// <see cref="IFanController.RestoreDefaults"/> und sammelt alle Worker-Exceptions ein. Extrahiert, damit
    /// INV-9 sowohl positiv (Referenz-Backend → leer) als auch im Negativ-Beweis (racy Backend → reißt) gegen
    /// ein <em>beliebiges</em> Backend laufen kann. Großzügig dimensioniert (8 Threads × 1000 Iterationen), damit
    /// ein nicht thread-sicheres Backend die Race verlässlich auslöst statt nur sporadisch.
    /// </summary>
    protected static IReadOnlyList<Exception> HammerConcurrently(
        ISensorBackend sensors, IFanController fans, SensorId sensorId, FanId fanId,
        int threads = 8, int iterations = 1000)
    {
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var start = new ManualResetEventSlim(false);

        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    switch ((t + i) % 3)
                    {
                        case 0: _ = sensors.ReadValue(sensorId); break;
                        case 1: fans.SetPwm(fanId, (byte)(i % 256)); break;
                        default: fans.RestoreDefaults(); break;
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToArray();

        foreach (var w in workers) w.Start();
        start.Set();
        foreach (var w in workers) w.Join();

        return failures.ToArray();
    }
}
