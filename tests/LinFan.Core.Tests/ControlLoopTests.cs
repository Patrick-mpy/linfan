// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class ControlLoopTests
{
    private static AppConfig ConfigWith(double hysteresis = 2.0, byte min = 0, byte max = 255) => new()
    {
        FailSafeTempC = 90,
        Curves = new[]
        {
            new CurveConfig
            {
                Id = "c", Name = "c", SourceSensorIds = new[] { "t" }, HysteresisC = hysteresis,
                Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
            },
        },
        Fans = new[]
        {
            new FanConfig { FanId = "f", Name = "f", AssignedCurveId = "c", MinPwm = min, MaxPwm = max },
        },
    };

    private static FakeHardware Hw(double temp)
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t", temp);
        hw.AddFan("f", canControl: true);
        return hw;
    }

    [Fact]
    public void Tick_DryRun_ComputesPwm_DoesNotWrite()
    {
        var hw = Hw(55);                                   // Mitte 30..80 → 50 % → pwm 128
        var tick = new ControlLoop(hw, hw, dryRun: true).Tick(ConfigWith());

        var a = Assert.Single(tick.Actions);
        Assert.Equal(FanActionKind.DryRun, a.Kind);
        Assert.Equal((byte)128, a.Pwm);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void Tick_Applies_WhenNotDryRun()
    {
        var hw = Hw(80);                                   // 100 % → 255
        var tick = new ControlLoop(hw, hw, dryRun: false).Tick(ConfigWith());

        Assert.Equal(FanActionKind.Applied, Assert.Single(tick.Actions).Kind);
        Assert.Equal(("f", (byte)255), Assert.Single(hw.Writes));
    }

    [Fact]
    public void Tick_RespectsHysteresis()
    {
        var hw = Hw(55);
        var loop = new ControlLoop(hw, hw, dryRun: false);

        loop.Tick(ConfigWith(hysteresis: 5));              // erster Tick: gesetzt
        hw.Values["t"] = 57;                               // +2 < 5 → halten
        var tick = loop.Tick(ConfigWith(hysteresis: 5));

        Assert.Equal(FanActionKind.Held, Assert.Single(tick.Actions).Kind);
        Assert.Single(hw.Writes);                          // kein zweiter Write
    }

    [Fact]
    public void Tick_FailSafe_OnOverTemp_RestoresDefaults_NoWrites()
    {
        var hw = Hw(95);
        var tick = new ControlLoop(hw, hw, dryRun: false).Tick(ConfigWith());

        Assert.True(tick.FailSafeTriggered);
        Assert.Equal(1, hw.RestoreCount);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void Tick_ClampsToFanMinimum()
    {
        var hw = Hw(30);                                   // 0 % → pwm 0, aber MinPwm 80 → 80
        var tick = new ControlLoop(hw, hw, dryRun: true).Tick(ConfigWith(min: 80));

        Assert.Equal((byte)80, Assert.Single(tick.Actions).Pwm);
    }

    [Fact]
    public void Tick_SkipsWhenSourceSensorNaN()
    {
        var hw = Hw(double.NaN);
        var tick = new ControlLoop(hw, hw, dryRun: true).Tick(ConfigWith());

        Assert.Equal(FanActionKind.Skipped, Assert.Single(tick.Actions).Kind);
    }

    [Fact]
    public void Tick_FailSafe_WhenControllingButNoReadableTemperature()
    {
        var hw = Hw(double.NaN);                       // einziger Temp-Sensor liefert NaN (z. B. EIO)
        var loop = new ControlLoop(hw, hw, dryRun: false);
        var config = ConfigWith();

        // Die ersten blinden Ticks halten nur an (kein Fail-Safe, keine Wiederherstellung) …
        for (int i = 0; i < 2; i++)
        {
            Assert.False(loop.Tick(config).FailSafeTriggered);
            Assert.Equal(0, hw.RestoreCount);
        }

        // … nach genügend Ticks ohne lesbare Temperatur kippt der Watchdog in den sicheren Zustand.
        var tick = loop.Tick(config);
        Assert.True(tick.FailSafeTriggered);
        Assert.Equal(1, hw.RestoreCount);
        Assert.NotNull(tick.FailSafeReason);
    }

    [Fact]
    public void ResetHysteresis_AppliesNewCurve_EvenWhenTemperatureUnchanged()
    {
        var hw = Hw(55);                                   // konstante Temperatur
        var loop = new ControlLoop(hw, hw, dryRun: false);

        loop.Tick(ConfigWith());                           // 30..80 → 50 % → pwm 128
        loop.Tick(ConfigWith());                           // gleiche Temp → Held (kein zweiter Write)
        Assert.Single(hw.Writes);

        // Steilere Kurve: 55 °C → 100 %. Ohne Reset hielte die Hysterese den alten Wert.
        var steeper = ConfigWith() with
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c", SourceSensorIds = new[] { "t" },
                    Points = new[] { new CurvePoint(30, 100), new CurvePoint(80, 100) },
                },
            },
        };

        loop.ResetHysteresis();
        var tick = loop.Tick(steeper);

        Assert.Equal(FanActionKind.Applied, Assert.Single(tick.Actions).Kind);
        Assert.Equal(("f", (byte)255), hw.Writes[^1]);     // neue Kurve sofort angewandt
    }

    [Fact]
    public void Tick_DryRunWithNaN_DoesNotFailSafe()
    {
        var hw = Hw(double.NaN);                       // ohne Root (dryRun) regeln wir nicht aktiv …
        var loop = new ControlLoop(hw, hw, dryRun: true);

        for (int i = 0; i < 5; i++)
        {
            var tick = loop.Tick(ConfigWith());
            Assert.False(tick.FailSafeTriggered);      // … also kein Blind-Watchdog-Fail-Safe
        }
        Assert.Equal(0, hw.RestoreCount);
    }

    [Fact]
    public void ManualOverride_WritesFixedValue_NotCurve()
    {
        var hw = Hw(55);                               // Kurve gäbe 128
        var loop = new ControlLoop(hw, hw, dryRun: false);
        loop.SetManualOverride("f", 200);

        var tick = loop.Tick(ConfigWith());

        FanAction a = Assert.Single(tick.Actions);
        Assert.Equal(FanActionKind.Manual, a.Kind);
        Assert.Equal((byte)200, a.Pwm);
        Assert.Equal(("f", (byte)200), Assert.Single(hw.Writes));
        Assert.Contains("f", loop.ManualFanIds());
    }

    [Fact]
    public void ClearManualOverride_ReturnsFanToCurve()
    {
        var hw = Hw(55);
        var loop = new ControlLoop(hw, hw, dryRun: false);

        loop.SetManualOverride("f", 200);
        loop.Tick(ConfigWith());                       // manuell 200
        loop.SetManualOverride("f", null);             // zurück auf Auto/Kurve

        var tick = loop.Tick(ConfigWith());            // Kurve: 55 °C → 128
        Assert.Equal(FanActionKind.Applied, Assert.Single(tick.Actions).Kind);
        Assert.Equal((byte)128, tick.Actions[0].Pwm);
        Assert.DoesNotContain("f", loop.ManualFanIds());
    }

    [Fact]
    public void SuspendedFan_IsSkipped_NotWritten()
    {
        var hw = Hw(55);
        var loop = new ControlLoop(hw, hw, dryRun: false);
        loop.Suspend("f");

        var tick = loop.Tick(ConfigWith());

        Assert.Equal(FanActionKind.Skipped, Assert.Single(tick.Actions).Kind);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void ManualOnly_NoCurve_NoReadableTemp_FailsSafeAfterBlindTicks()
    {
        var hw = Hw(double.NaN);                        // einziger Temp-Sensor liefert NaN
        var loop = new ControlLoop(hw, hw, dryRun: false);
        var config = new AppConfig
        {
            FailSafeTempC = 90,
            Fans = new[] { new FanConfig { FanId = "f", Name = "f" } }, // KEINE Kurve, nur manuell
        };
        loop.SetManualOverride("f", 40);

        Assert.False(loop.Tick(config).FailSafeTriggered); // blind 1
        Assert.False(loop.Tick(config).FailSafeTriggered); // blind 2

        var tick = loop.Tick(config);                      // blind 3 → Fail-Safe (auch ohne Kurve!)
        Assert.True(tick.FailSafeTriggered);
        Assert.Equal(1, hw.RestoreCount);
        Assert.DoesNotContain("f", loop.ManualFanIds());   // Override verworfen
    }

    [Fact]
    public void Tick_MultiSensorMax_UsesHottestSensor()
    {
        // Kurve 30..80 → 0..100 %. Quellen: t1=40 (kühl), t2=80 (heiß). Max → 80 → 100 % → pwm 255.
        var hw = new FakeHardware();
        hw.AddTempSensor("t1", 40);
        hw.AddTempSensor("t2", 80);
        hw.AddFan("f", canControl: true);

        var config = new AppConfig
        {
            FailSafeTempC = 90,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c", SourceSensorIds = new[] { "t1", "t2" },
                    Aggregation = SensorAggregation.Max,
                    Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "f", Name = "f", AssignedCurveId = "c" } },
        };

        var tick = new ControlLoop(hw, hw, dryRun: true).Tick(config);

        FanAction a = Assert.Single(tick.Actions);
        Assert.Equal(FanActionKind.DryRun, a.Kind);
        Assert.Equal((byte)255, a.Pwm); // heißester Sensor (80 °C) bestimmt die Drehzahl
    }

    [Fact]
    public void Tick_MultiSensorAvg_UsesMeanTemperature()
    {
        // Quellen: t1=30, t2=80 → Avg 55 °C → 50 % → pwm 128.
        var hw = new FakeHardware();
        hw.AddTempSensor("t1", 30);
        hw.AddTempSensor("t2", 80);
        hw.AddFan("f", canControl: true);

        var config = new AppConfig
        {
            FailSafeTempC = 90,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c", SourceSensorIds = new[] { "t1", "t2" },
                    Aggregation = SensorAggregation.Avg,
                    Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "f", Name = "f", AssignedCurveId = "c" } },
        };

        var tick = new ControlLoop(hw, hw, dryRun: true).Tick(config);

        Assert.Equal((byte)128, Assert.Single(tick.Actions).Pwm);
    }

    [Fact]
    public void Tick_MultiSensor_SkipsWhenAllSourcesNaN()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("t1", double.NaN);
        hw.AddTempSensor("t2", double.NaN);
        hw.AddFan("f", canControl: true);

        var config = new AppConfig
        {
            FailSafeTempC = 90,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c", Name = "c", SourceSensorIds = new[] { "t1", "t2" },
                    Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "f", Name = "f", AssignedCurveId = "c" } },
        };

        var tick = new ControlLoop(hw, hw, dryRun: true).Tick(config);

        Assert.Equal(FanActionKind.Skipped, Assert.Single(tick.Actions).Kind);
    }

    [Fact]
    public void FailSafe_ClearsManualOverride()
    {
        var hw = Hw(95);                               // Übertemperatur
        var loop = new ControlLoop(hw, hw, dryRun: false);
        loop.SetManualOverride("f", 200);

        var tick = loop.Tick(ConfigWith());

        Assert.True(tick.FailSafeTriggered);
        Assert.Equal(1, hw.RestoreCount);
        Assert.Empty(hw.Writes);
        Assert.DoesNotContain("f", loop.ManualFanIds()); // nicht automatisch in Manual zurück
    }

    /// <summary>Baut <see cref="ConfigWith"/> mit deaktivierter Kurve (gleiche Quelle/Punkte, nur Enabled=false).</summary>
    private static AppConfig Disabled() => ConfigWith() with
    {
        Curves = new[]
        {
            new CurveConfig
            {
                Id = "c", Name = "c", SourceSensorIds = new[] { "t" }, Enabled = false,
                Points = new[] { new CurvePoint(30, 0), new CurvePoint(80, 100) },
            },
        },
    };

    [Fact]
    public void Tick_DisabledCurve_SetsAssignedFanToAuto_NoPwmWrite()
    {
        var hw = Hw(55);                               // aktive Kurve gäbe 128
        var tick = new ControlLoop(hw, hw, dryRun: false).Tick(Disabled());

        FanAction a = Assert.Single(tick.Actions);
        Assert.Equal(FanActionKind.Skipped, a.Kind);  // nicht geregelt …
        Assert.Equal(("f", FanMode.Auto), Assert.Single(hw.ModeWrites)); // … sondern Hardware-Auto
        Assert.Empty(hw.Writes);                       // kein eingefrorener PWM
    }

    [Fact]
    public void Tick_DisabledCurve_DryRun_DoesNotTouchHardware()
    {
        var hw = Hw(55);
        var tick = new ControlLoop(hw, hw, dryRun: true).Tick(Disabled());

        Assert.Equal(FanActionKind.Skipped, Assert.Single(tick.Actions).Kind);
        Assert.Empty(hw.ModeWrites);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void Tick_DisabledCurve_ManualOverrideStillWins()
    {
        var hw = Hw(55);
        var loop = new ControlLoop(hw, hw, dryRun: false);
        loop.SetManualOverride("f", 200);

        var tick = loop.Tick(Disabled());

        Assert.Equal(FanActionKind.Manual, Assert.Single(tick.Actions).Kind);
        Assert.Equal(("f", (byte)200), Assert.Single(hw.Writes));
        Assert.Empty(hw.ModeWrites);                   // Manual hat Vorrang vor dem Auto-Fallback
    }

    [Fact]
    public void Tick_ReenabledCurve_AppliesImmediately()
    {
        var hw = Hw(55);                               // Kurve gäbe 128
        var loop = new ControlLoop(hw, hw, dryRun: false);

        loop.Tick(Disabled());                         // deaktiviert → Auto, kein PWM
        Assert.Single(hw.ModeWrites);
        Assert.Empty(hw.Writes);

        var tick = loop.Tick(ConfigWith());            // wieder aktiv → sofort PWM (Hysterese verworfen)
        Assert.Equal(FanActionKind.Applied, Assert.Single(tick.Actions).Kind);
        Assert.Equal(("f", (byte)128), Assert.Single(hw.Writes));
    }

    /// <summary>Wie <see cref="ConfigWith"/>, aber ohne Kurven-Zuordnung (z. B. nach Entfernen der Zuordnung).</summary>
    private static AppConfig Unassigned() => ConfigWith() with
    {
        Fans = new[] { new FanConfig { FanId = "f", Name = "f", AssignedCurveId = null } },
    };

    [Fact]
    public void Tick_UnassignedFan_SetsModeAuto_NoPwmWrite()
    {
        var hw = Hw(55);                               // eine aktive Kurve gäbe 128 …
        var tick = new ControlLoop(hw, hw, dryRun: false).Tick(Unassigned());

        FanAction a = Assert.Single(tick.Actions);
        Assert.Equal(FanActionKind.Skipped, a.Kind);                    // … aber ohne Zuordnung nicht geregelt …
        Assert.Equal(("f", FanMode.Auto), Assert.Single(hw.ModeWrites)); // … sondern Hardware-Auto (nicht eingefroren)
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void Tick_UnassignedFan_DryRun_DoesNotTouchHardware()
    {
        var hw = Hw(55);
        var tick = new ControlLoop(hw, hw, dryRun: true).Tick(Unassigned());

        Assert.Equal(FanActionKind.Skipped, Assert.Single(tick.Actions).Kind);
        Assert.Empty(hw.ModeWrites);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void Tick_UnassignedFan_ManualOverrideStillWins()
    {
        var hw = Hw(55);
        var loop = new ControlLoop(hw, hw, dryRun: false);
        loop.SetManualOverride("f", 200);

        var tick = loop.Tick(Unassigned());

        Assert.Equal(FanActionKind.Manual, Assert.Single(tick.Actions).Kind);
        Assert.Equal(("f", (byte)200), Assert.Single(hw.Writes));
        Assert.Empty(hw.ModeWrites);                   // Manual hat Vorrang vor dem Auto-Fallback
    }

    [Fact]
    public void Tick_UnassignedReadOnlyFan_SkippedReadOnly_NoSetMode()
    {
        // Read-only-Kanal ohne Zuordnung: NICHT jeden Tick vergeblich auf Auto stellen (würfe Exception →
        // Failed → warn-Flut), sondern still als read-only überspringen.
        var hw = new FakeHardware();
        hw.AddTempSensor("t", 55);
        hw.AddFan("f", canControl: false);

        var tick = new ControlLoop(hw, hw, dryRun: false).Tick(Unassigned());

        FanAction a = Assert.Single(tick.Actions);
        Assert.Equal(FanActionKind.Skipped, a.Kind);
        Assert.Empty(hw.ModeWrites);                   // kein vergeblicher SetMode
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void Tick_FanReassignedAfterUnassign_AppliesImmediately()
    {
        var hw = Hw(55);                               // Kurve gäbe 128
        var loop = new ControlLoop(hw, hw, dryRun: false);

        loop.Tick(Unassigned());                       // ohne Kurve → Auto, kein PWM
        Assert.Single(hw.ModeWrites);
        Assert.Empty(hw.Writes);

        var tick = loop.Tick(ConfigWith());            // wieder zugeordnet → sofort PWM (Hysterese verworfen)
        Assert.Equal(FanActionKind.Applied, Assert.Single(tick.Actions).Kind);
        Assert.Equal(("f", (byte)128), Assert.Single(hw.Writes));
    }

    [Fact]
    public void Tick_ThrowingTemperatureSensor_DoesNotThrow_AndWatchdogStillRuns()
    {
        // Fail-Safe: ein Sensor, dessen ReadValue WIRFT (EIO als Exception statt NaN), darf den Watchdog-Tick
        // nicht abreißen — sonst überspränge der Daemon den ganzen Tick (er fängt Tick-Würfe) und die Übertemp-/
        // Blind-Erkennung liefe nie. Erwartet: kein Wurf; nach den Blind-Ticks greift der Fail-Safe (Restore).
        var hw = new ThrowingSensorRig();
        var loop = new ControlLoop(hw, hw, dryRun: false);
        var config = ConfigWith();

        Assert.False(loop.Tick(config).FailSafeTriggered); // blind 1 (Wurf → NaN, kein Absturz)
        Assert.False(loop.Tick(config).FailSafeTriggered); // blind 2
        var tick = loop.Tick(config);                      // blind 3 → Fail-Safe
        Assert.True(tick.FailSafeTriggered);
        Assert.Equal(1, hw.RestoreCount);
    }

    /// <summary>Backend, dessen <c>ReadValue</c> immer wirft (kein KeyNotFound) — für die Watchdog-Resilienz.</summary>
    private sealed class ThrowingSensorRig : ISensorBackend, IFanController
    {
        public int RestoreCount { get; private set; }

        public IReadOnlyList<SensorDescriptor> DiscoverSensors() =>
            new[] { new SensorDescriptor(new SensorId("t"), "t", SensorKind.Temperature, "°C", "t") };
        public double ReadValue(SensorId id) => throw new InvalidOperationException("EIO");

        public IReadOnlyList<FanDescriptor> DiscoverFans() =>
            new[] { new FanDescriptor(new FanId("f"), "f", true, null, "f") };
        public bool CanControl(FanId id) => true;
        public FanMode GetMode(FanId id) => FanMode.Manual;
        public void SetMode(FanId id, FanMode mode) { }
        public byte GetPwm(FanId id) => 0;
        public void SetPwm(FanId id, byte value) { }
        public void RestoreDefaults() => RestoreCount++;
        public void Dispose() { }
    }
}
