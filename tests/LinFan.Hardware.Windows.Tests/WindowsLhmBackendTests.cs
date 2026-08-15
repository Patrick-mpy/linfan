// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Hardware.Windows.Lhm;

namespace LinFan.Hardware.Windows.Tests;

/// <summary>
/// Gezielte Unit-Tests des <see cref="WindowsLhmBackend"/> über das Fake-LHM: PWM-Mapping (Round-Trip
/// samt Toleranz-Begründung), Discovery-Pairing und Stabilität. Ergänzt die Conformance-Suite, die das
/// allgemeine Vertragsverhalten abdeckt.
/// </summary>
public sealed class WindowsLhmBackendTests
{
    // --- PWM-Mapping: ToPercent / ToByte -------------------------------------

    [Fact]
    public void ToPercent_And_ToByte_AreExactAtBounds()
    {
        Assert.Equal(0, WindowsLhmBackend.ToPercent(0));
        Assert.Equal(100, WindowsLhmBackend.ToPercent(255));

        Assert.Equal((byte)0, WindowsLhmBackend.ToByte(0));
        Assert.Equal((byte)255, WindowsLhmBackend.ToByte(100));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)64)]
    [InlineData((byte)123)]
    [InlineData((byte)128)]
    [InlineData((byte)200)]
    [InlineData((byte)254)]
    [InlineData((byte)255)]
    public void ByteToPercentToByte_RoundTrip_WithinTolerance(byte value)
    {
        // Verlustbehaftet (0..255 → 0..100 → 0..255). Quantisierung 255/100 ≈ 2.55 → ≤ 3 Byte Abweichung;
        // genau die Toleranz, die WindowsLhmConformanceTests setzt.
        int percent = WindowsLhmBackend.ToPercent(value);
        byte back = WindowsLhmBackend.ToByte(percent);
        Assert.InRange(Math.Abs(back - value), 0, 3);
    }

    // --- Discovery-Pairing ----------------------------------------------------

    [Fact]
    public void Discovery_PairsControlWithSiblingRpmSensor_BySharedIndex()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", new FakeLhmControl()));
        lhm.Add(FakeLhmSensor.Reading("io/fan/1", "Fan #1", "SuperIO", LhmSensorType.Fan, 1500f));

        using var backend = new WindowsLhmBackend(lhm);

        var fan = Assert.Single(backend.DiscoverFans());
        Assert.True(fan.CanControl);
        Assert.Equal(new SensorId("io/fan/1"), fan.Tachometer);
    }

    [Fact]
    public void Discovery_LeavesTachometerNull_WhenNoSiblingRpmSensor()
    {
        var lhm = new FakeLhmComputer();
        // Control #1, aber der einzige RPM-Sensor trägt einen anderen Index (#7) → kein eindeutiger Match.
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", new FakeLhmControl()));
        lhm.Add(FakeLhmSensor.Reading("io/fan/7", "Fan #7", "SuperIO", LhmSensorType.Fan, 900f));

        using var backend = new WindowsLhmBackend(lhm);

        var fan = Assert.Single(backend.DiscoverFans());
        Assert.Null(fan.Tachometer);
    }

    [Fact]
    public void Discovery_LeavesTachometerNull_OnAmbiguousIndexMatch()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", new FakeLhmControl()));
        // Zwei RPM-Sensoren mit demselben Index #1 an derselben Hardware → mehrdeutig, lieber null.
        lhm.Add(FakeLhmSensor.Reading("io/fan/1a", "Fan #1", "SuperIO", LhmSensorType.Fan, 1000f));
        lhm.Add(FakeLhmSensor.Reading("io/fan/1b", "Fan #1", "SuperIO", LhmSensorType.Fan, 1100f));

        using var backend = new WindowsLhmBackend(lhm);

        var fan = Assert.Single(backend.DiscoverFans());
        Assert.Null(fan.Tachometer);
    }

    [Fact]
    public void Discovery_DoesNotPairAcrossDifferentHardware()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", new FakeLhmControl()));
        // Gleicher Index, aber andere Hardware → kein Pairing-Scope.
        lhm.Add(FakeLhmSensor.Reading("gpu/fan/1", "Fan #1", "GPU", LhmSensorType.Fan, 1500f));

        using var backend = new WindowsLhmBackend(lhm);

        var fan = Assert.Single(backend.DiscoverFans());
        Assert.Null(fan.Tachometer);
    }

    [Fact]
    public void Discovery_ReadOnlyControl_HasCanControlFalse()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.ReadOnlyControl("io/control/9", "Fan Control #9", "SuperIO"));

        using var backend = new WindowsLhmBackend(lhm);

        var fan = Assert.Single(backend.DiscoverFans());
        Assert.False(fan.CanControl);
    }

    [Fact]
    public void Discovery_YieldsStableIds_AcrossRepeatedCalls()
    {
        using var backend = new WindowsLhmBackend(BuildMixedScenario());

        var fans1 = backend.DiscoverFans().Select(f => f.Id.Value).OrderBy(x => x, StringComparer.Ordinal);
        var fans2 = backend.DiscoverFans().Select(f => f.Id.Value).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(fans1, fans2);

        var s1 = backend.DiscoverSensors().Select(s => s.Id.Value).OrderBy(x => x, StringComparer.Ordinal);
        var s2 = backend.DiscoverSensors().Select(s => s.Id.Value).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void Discovery_DuplicateChipNames_YieldDistinctIds_ViaLhmInstanceIndex()
    {
        // Untermauert die Doc-Entscheidung „Windows braucht keine Id-Migration": LHM-Identifier sind durch
        // den Instanz-Index (…/0/… vs …/1/…) selbst bei zwei gleichnamigen Chips kollisionsfrei.
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("/lpc/nct6797d/0/control/1", "Fan Control #1", "NCT6797D", new FakeLhmControl()));
        lhm.Add(FakeLhmSensor.Controllable("/lpc/nct6797d/1/control/1", "Fan Control #1", "NCT6797D", new FakeLhmControl()));

        using var backend = new WindowsLhmBackend(lhm);

        var ids = backend.DiscoverFans().Select(f => f.Id.Value).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // --- Start-Diagnose (nur-GPU-Erkennung) ----------------------------------

    [Fact]
    public void StartupWarning_Set_WhenOnlyGpuChannelsFound()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(GpuReading("/gpu-nvidia/0/temperature/0", "GPU Core", LhmSensorType.Temperature, 45f));
        lhm.Add(GpuReading("/gpu-nvidia/0/fan/0", "GPU Fan", LhmSensorType.Fan, 1200f));

        using var backend = new WindowsLhmBackend(lhm);

        Assert.NotNull(((IBackendDiagnostics)backend).StartupWarning);
    }

    [Fact]
    public void StartupWarning_Null_WhenMainboardOrCpuChannelsPresent()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(GpuReading("/gpu-nvidia/0/temperature/0", "GPU Core", LhmSensorType.Temperature, 45f));
        // Ein einziger Nicht-GPU-Kanal (CPU) genügt: der Treiber liest den Chip → kein Konflikt-Verdacht.
        lhm.Add(new FakeLhmSensor
        {
            Identifier = "/amdcpu/0/temperature/0",
            Name = "Core",
            HardwareName = "AMD Ryzen",
            Type = LhmSensorType.Temperature,
            Value = 50f,
            HardwareType = LhmHardwareType.Cpu,
        });

        using var backend = new WindowsLhmBackend(lhm);

        Assert.Null(((IBackendDiagnostics)backend).StartupWarning);
    }

    [Fact]
    public void StartupWarning_Null_WhenNothingFound()
    {
        // Leere Discovery ist NICHT „nur GPU" (Vakuar-Wahrheits-Falle): keine Warnung.
        using var backend = new WindowsLhmBackend(new FakeLhmComputer());

        Assert.Null(((IBackendDiagnostics)backend).StartupWarning);
    }

    private static FakeLhmSensor GpuReading(string id, string name, LhmSensorType type, float value) =>
        new()
        {
            Identifier = id,
            Name = name,
            HardwareName = "NVIDIA GeForce",
            Type = type,
            Value = value,
            HardwareType = LhmHardwareType.GpuNvidia,
        };

    // --- Mode/PWM-Verhalten ---------------------------------------------------

    [Fact]
    public void SetPwm_ForcesSoftwareMode_AndRoundTripsWithinTolerance()
    {
        var control = new FakeLhmControl();
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", control));

        using var backend = new WindowsLhmBackend(lhm);
        var id = backend.DiscoverFans().Single().Id;

        Assert.Equal(FanMode.Auto, backend.GetMode(id)); // startet in Default = Auto
        backend.SetPwm(id, 128);

        Assert.Equal(FanMode.Manual, backend.GetMode(id));
        Assert.Equal(LhmControlMode.Software, control.Mode);
        Assert.InRange((int)backend.GetPwm(id), 128 - 3, 128 + 3);
    }

    [Fact]
    public void RestoreDefaults_CallsSetDefault_OnControllableChannelsOnly()
    {
        var ctl = new FakeLhmControl(LhmControlMode.Software, initialValue: 10f);
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", ctl));
        lhm.Add(FakeLhmSensor.ReadOnlyControl("io/control/2", "Fan Control #2", "SuperIO"));

        using var backend = new WindowsLhmBackend(lhm);
        backend.RestoreDefaults();

        Assert.Equal(LhmControlMode.Default, ctl.Mode);
        Assert.True(ctl.SetDefaultCalls >= 1);
    }

    [Fact]
    public void RestoreDefaults_FallsBackToFullSpeed_WhenSetDefaultLeavesSoftwareMode()
    {
        // Board/Treiber ignoriert SetDefault (Kanal bleibt im Software-Modus). Ohne Verifikation bliebe der
        // Lüfter ungeregelt auf dem letzten (evtl. niedrigen) Wert hängen → Überhitzungsgefahr. Erwartet:
        // Fallback auf Volllast (100 %), die sichere Richtung (analog zum Linux-255-Fallback).
        var ctl = new FakeLhmControl(LhmControlMode.Software, initialValue: 10f) { IgnoreSetDefault = true };
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", ctl));

        using var backend = new WindowsLhmBackend(lhm);
        backend.RestoreDefaults();

        Assert.True(ctl.SetDefaultCalls >= 1);              // Auto wurde zuerst versucht
        Assert.Equal(LhmControlMode.Software, ctl.Mode);    // SetDefault wirkungslos → Fallback greift
        Assert.Equal(100f, ctl.SoftwareValue);              // Volllast erzwungen (max. Kühlung)
    }

    [Fact]
    public void RestoreDefaults_FallsBackToFullSpeed_WhenSetDefaultThrows()
    {
        // SetDefault wirft (Kanal nicht sauber auf Auto zu bringen) → statt den Lüfter niedrig hängen zu
        // lassen, in die sichere Richtung erzwingen: Volllast.
        var failing = new FakeLhmControl(LhmControlMode.Software, initialValue: 40f) { ThrowOnSetDefault = true };
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", failing));

        using var backend = new WindowsLhmBackend(lhm);
        var ex = Record.Exception(() => backend.RestoreDefaults());

        Assert.Null(ex);                                    // wirft nie (INV-2)
        Assert.Equal(LhmControlMode.Software, failing.Mode);
        Assert.Equal(100f, failing.SoftwareValue);          // SetDefault warf → Fallback Volllast
    }

    // --- Fail-Safe: Best-Effort & Never-throw --------------------------------

    [Fact]
    public void RestoreDefaults_IsBestEffort_WhenOneChannelThrows()
    {
        var failing = new FakeLhmControl(LhmControlMode.Software, initialValue: 40f) { ThrowOnSetDefault = true };
        var healthy = new FakeLhmControl(LhmControlMode.Software, initialValue: 30f);
        var lhm = new FakeLhmComputer();
        // Werfender Kanal ZUERST - der gesunde danach muss trotzdem zurückgestellt werden.
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", failing));
        lhm.Add(FakeLhmSensor.Controllable("io/control/2", "Fan Control #2", "SuperIO", healthy));

        using var backend = new WindowsLhmBackend(lhm);

        var ex = Record.Exception(() => backend.RestoreDefaults());
        Assert.Null(ex);                                    // wirft nie (INV-2)
        Assert.Equal(LhmControlMode.Default, healthy.Mode); // gesunder Kanal landet trotzdem auf Auto
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenChannelThrowsOnSetDefault()
    {
        var failing = new FakeLhmControl(LhmControlMode.Software) { ThrowOnSetDefault = true };
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", failing));

        var backend = new WindowsLhmBackend(lhm);

        var ex = Record.Exception(() => backend.Dispose());
        Assert.Null(ex);           // Dispose-Pfad (RestoreDefaults) reißt nicht (INV-2b)
        Assert.True(lhm.Disposed); // Computer.Close() wurde dennoch erreicht
    }

    [Fact]
    public void GetMode_ReturnsAuto_WhenLhmReadThrows()
    {
        using var backend = new WindowsLhmBackend(SingleControl(new FakeLhmControl { ThrowOnRead = true }));
        var id = backend.DiscoverFans().Single().Id;

        Assert.Equal(FanMode.Auto, backend.GetMode(id)); // werfender Getter → sicherer Default, kein Wurf
    }

    [Fact]
    public void GetPwm_ReturnsZero_WhenLhmReadThrows()
    {
        using var backend = new WindowsLhmBackend(SingleControl(new FakeLhmControl { ThrowOnRead = true }));
        var id = backend.DiscoverFans().Single().Id;

        Assert.Equal((byte)0, backend.GetPwm(id)); // werfender Getter → Default 0
    }

    [Fact]
    public void ReadValue_ReturnsNaN_WhenLhmReadThrows()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(new FakeLhmSensor
        {
            Identifier = "cpu/temperature/0",
            Name = "CPU",
            HardwareName = "AMD",
            Type = LhmSensorType.Temperature,
            ThrowOnValueRead = true,
        });

        using var backend = new WindowsLhmBackend(lhm);
        var id = backend.DiscoverSensors().Single().Id;

        Assert.True(double.IsNaN(backend.ReadValue(id))); // werfender Value-Getter → „kein Wert", kein Wurf
    }

    /// <summary>Ein steuerbarer Control-Sensor, inline gebaut (nicht über <c>Controllable</c>), damit ein
    /// <c>ThrowOnRead</c>-Control beim Aufbau nicht vorzeitig den SoftwareValue-Getter triggert.</summary>
    private static FakeLhmComputer SingleControl(FakeLhmControl control)
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(new FakeLhmSensor
        {
            Identifier = "io/control/1",
            Name = "Fan Control #1",
            HardwareName = "SuperIO",
            Type = LhmSensorType.Control,
            Value = null,
            Control = control,
        });
        return lhm;
    }

    [Fact]
    public void ReadValue_NullSensorValue_YieldsNaN()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Reading("cpu/temperature/0", "CPU", "AMD", LhmSensorType.Temperature, null));

        using var backend = new WindowsLhmBackend(lhm);
        var id = backend.DiscoverSensors().Single().Id;

        Assert.True(double.IsNaN(backend.ReadValue(id)));
    }

    [Fact]
    public void Dispose_RestoresDefaults_AndIsRepeatable()
    {
        var ctl = new FakeLhmControl(LhmControlMode.Software, initialValue: 20f);
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", ctl));

        var backend = new WindowsLhmBackend(lhm);
        backend.Dispose();

        Assert.Equal(LhmControlMode.Default, ctl.Mode);
        Assert.True(lhm.Disposed);

        // Nach Dispose erneut: RestoreDefaults darf nicht werfen (Shutdown-Pfad-Wiederholung).
        var ex = Record.Exception(() => backend.RestoreDefaults());
        Assert.Null(ex);
    }

    private static FakeLhmComputer BuildMixedScenario()
    {
        var lhm = new FakeLhmComputer();
        lhm.Add(FakeLhmSensor.Controllable("io/control/1", "Fan Control #1", "SuperIO", new FakeLhmControl()));
        lhm.Add(FakeLhmSensor.ReadOnlyControl("io/control/2", "Fan Control #2", "SuperIO"));
        lhm.Add(FakeLhmSensor.Reading("io/fan/1", "Fan #1", "SuperIO", LhmSensorType.Fan, 1500f));
        lhm.Add(FakeLhmSensor.Reading("cpu/temperature/0", "CPU", "AMD", LhmSensorType.Temperature, 42f));
        return lhm;
    }
}
