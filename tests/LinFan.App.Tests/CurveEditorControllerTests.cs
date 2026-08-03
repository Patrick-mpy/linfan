// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

public sealed class CurveEditorControllerTests
{
    /// <summary>Fängt ab, was der Controller „an den Daemon" senden würde (statt Dateizugriff).</summary>
    private sealed class SaveSink
    {
        public List<AppConfig> Saved { get; } = new();
        public bool Result { get; set; } = true;

        public Task<bool> SaveAsync(AppConfig config)
        {
            Saved.Add(config);
            return Task.FromResult(Result);
        }
    }

    // Simuliert einen Daemon-Snapshot, wie ihn der IPC-Monitor liefert (Live-Werte + aktuelle Config).
    private static MonitorSnapshot Snapshot(AppConfig? config = null) => new(
        "test",
        new[]
        {
            new SensorReading("hwmon6/temp1", "k10temp Tctl", SensorKind.Temperature, "°C", 40),
            new SensorReading("hwmon7/temp1", "thinkpad CPU", SensorKind.Temperature, "°C", 38),
        },
        new[]
        {
            new FanReading("hwmon7/pwm1", "thinkpad pwm1", 1900, 255, FanMode.Auto, true),
        },
        config ?? AppConfig.Empty);

    [Fact]
    public async Task Save_SendsEditedCurve_AndAssignment_ToDaemon()
    {
        var config = new AppConfig
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Quiet", SourceSensorId = "hwmon6/temp1",
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1", MinPwm = 40 } },
        };

        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(config));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        Assert.Equal("Quiet", curve.Name);
        Assert.Equal(2, curve.Points.Count);

        // Bearbeiten: umbenennen, Punkt hinzufügen, Lüfter zuordnen + Position/Gruppe + Namen setzen.
        curve.Name = "Silent";
        curve.AddPointRow(55, 50);
        FanAssignRow fanRow = Assert.Single(ctrl.Fans);
        fanRow.Selected = curve;
        fanRow.Location = FanLocationOption.For(FanLocation.CaseRearExhaust);
        fanRow.Group = "Gehäuse";
        fanRow.Name = "CPU-Lüfter";
        fanRow.Visible = false; // Lüfter im Dashboard ausblenden
        SensorOption tctl = ctrl.Sensors.First(s => s.Id == "hwmon6/temp1");
        tctl.Name = "CPU-Paket";
        tctl.Visible = false;   // Sensor ausblenden
        tctl.Group = "CPU";     // Sensor gruppieren

        await ctrl.SaveCommand.ExecuteAsync(null);

        AppConfig sent = Assert.Single(sink.Saved);
        CurveConfig rc = Assert.Single(sent.Curves);
        Assert.Equal("Silent", rc.Name);
        Assert.Equal(3, rc.Points.Count);

        FanConfig rf = Assert.Single(sent.Fans);
        Assert.Equal("c1", rf.AssignedCurveId);
        Assert.Equal((byte)40, rf.MinPwm); // unveränderte Felder (MinPwm) bleiben erhalten
        Assert.Equal(FanLocation.CaseRearExhaust, rf.Location);
        Assert.Equal("Gehäuse", rf.Group);
        Assert.Equal("CPU-Lüfter", rf.Name);
        Assert.True(rf.Hidden);
        Assert.Contains(sent.Sensors,
            s => s.SensorId == "hwmon6/temp1" && s.Name == "CPU-Paket" && s.Hidden && s.Group == "CPU");
    }

    [Fact]
    public async Task Save_SendsMultipleSources_AndAggregation()
    {
        var config = new AppConfig
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Mix", SourceSensorIds = new[] { "hwmon6/temp1" },
                    Aggregation = SensorAggregation.Max,
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1" } },
        };

        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(config));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        // Erste Quelle ist bereits angekreuzt; zweite Quelle hinzunehmen und auf Mittelwert stellen.
        SensorCheck second = curve.SensorChecks.First(c => c.Sensor.Id == "hwmon7/temp1");
        second.Selected = true;
        curve.Aggregation = SensorAggregation.Avg;

        await ctrl.SaveCommand.ExecuteAsync(null);

        AppConfig sent = Assert.Single(sink.Saved);
        CurveConfig rc = Assert.Single(sent.Curves);
        Assert.Equal(new[] { "hwmon6/temp1", "hwmon7/temp1" }, rc.SourceSensorIds);
        Assert.Equal(SensorAggregation.Avg, rc.Aggregation);
    }

    [Fact]
    public void SetCurveEnabled_SendsCommand_StaysClean_AndSurvivesRevert()
    {
        var config = new AppConfig
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Quiet", SourceSensorId = "hwmon6/temp1",
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "pwm1", AssignedCurveId = "c1" } },
        };

        var sink = new SaveSink();
        var toggles = new List<(string Id, bool Enabled)>();
        var ctrl = new CurveEditorController(sink.SaveAsync,
            setCurveEnabled: (id, enabled) => { toggles.Add((id, enabled)); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(config));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        Assert.True(curve.Enabled);
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.SetCurveEnabled(curve, false);

        Assert.False(curve.Enabled);
        Assert.Equal(("c1", false), Assert.Single(toggles)); // Live-Command gesendet …
        Assert.False(ctrl.HasUnsavedChanges);                // … aber kein „Nicht gespeichert"-Banner

        // „Verwerfen" stellt aus der Baseline wieder her — der live persistierte Toggle bleibt erhalten.
        ctrl.RevertCommand.Execute(null);
        Assert.False(Assert.Single(ctrl.Curves).Enabled);
    }

    [Fact]
    public void SetCurveEnabled_NoChange_DoesNotSend()
    {
        var sink = new SaveSink();
        var toggles = new List<(string, bool)>();
        var ctrl = new CurveEditorController(sink.SaveAsync,
            setCurveEnabled: (id, enabled) => { toggles.Add((id, enabled)); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(new AppConfig
        {
            Curves = new[] { new CurveConfig { Id = "c1", Name = "Q", SourceSensorId = "hwmon6/temp1", Points = new[] { new CurvePoint(30, 20) } } },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "pwm1" } },
        }));

        ctrl.SetCurveEnabled(Assert.Single(ctrl.Curves), enabled: true); // bereits an → No-Op
        Assert.Empty(toggles);
    }

    [Fact]
    public void From_LegacyCurve_MigratesSingleSourceToCheckedSensor()
    {
        var config = new AppConfig
        {
            // Altbestand: nur SourceSensorId, kein SourceSensorIds.
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Alt", SourceSensorId = "hwmon6/temp1",
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1" } },
        };

        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        SensorOption[] sources = curve.Sources.ToArray();
        Assert.Single(sources);
        Assert.Equal("hwmon6/temp1", sources[0].Id);   // altes Einzelfeld als angekreuzte Quelle migriert
    }

    [Fact]
    public void From_GloballyHiddenSensor_NotOfferedInSensorChecks()
    {
        var config = new AppConfig
        {
            // hwmon6/temp1 ist im Geräte-Tab global ausgeblendet, wird aber von der Kurve referenziert.
            Sensors = new[] { new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl", Hidden = true } },
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "K", SourceSensorIds = new[] { "hwmon6/temp1" },
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "pwm1" } },
        };

        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        // Der ausgeblendete Sensor taucht nicht in der Auswahl auf; nur der sichtbare ist wählbar.
        Assert.DoesNotContain(curve.SensorChecks, c => c.Sensor.Id == "hwmon6/temp1");
        Assert.Contains(curve.SensorChecks, c => c.Sensor.Id == "hwmon7/temp1");
    }

    [Fact]
    public async Task Profile_Switch_AppliesAssignments_AndSaveSendsProfiles()
    {
        var curves = new[]
        {
            new CurveConfig { Id = "quiet", Name = "Quiet", SourceSensorId = "hwmon6/temp1",
                Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) } },
            new CurveConfig { Id = "loud", Name = "Loud", SourceSensorId = "hwmon6/temp1",
                Points = new[] { new CurvePoint(30, 60), new CurvePoint(80, 100) } },
        };
        var config = new AppConfig
        {
            // Beide Kurven gehören (profilgebunden) zu beiden Profilen; sie unterscheiden sich in der Zuordnung.
            Curves = curves,
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "Fan", AssignedCurveId = "quiet" } },
            Profiles = new[]
            {
                new Profile { Id = "p-silent", Name = "Silent", Curves = curves,
                    Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "quiet") } },
                new Profile { Id = "p-perf", Name = "Performance", Curves = curves,
                    Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "loud") } },
            },
            ActiveProfileId = "p-silent",
        };

        var sink = new SaveSink();
        var activated = new List<string>();
        var ctrl = new CurveEditorController(sink.SaveAsync, id => { activated.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(config));

        Assert.Equal("p-silent", ctrl.SelectedProfile!.Id);
        Assert.Equal("quiet", Assert.Single(ctrl.Fans).Selected!.Id);

        ctrl.SelectedProfile = ctrl.Profiles.First(p => p.Id == "p-perf"); // umschalten
        Assert.Equal("loud", Assert.Single(ctrl.Fans).Selected!.Id);       // Zuordnung übernommen
        Assert.Contains("p-perf", activated);                              // live aktiviert

        await ctrl.SaveCommand.ExecuteAsync(null);

        AppConfig sent = Assert.Single(sink.Saved);
        Assert.Equal("p-perf", sent.ActiveProfileId);
        Assert.Equal(2, sent.Profiles.Count);
    }

    [Fact]
    public async Task Save_BeforeInitialize_SendsNothing()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync); // kein Initialize → noch nicht verbunden

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Empty(sink.Saved); // nichts gesendet → kein Überschreiben mit leerer Config
    }

    [Fact]
    public async Task Save_ReportsFailure_WhenDaemonUnreachable()
    {
        var sink = new SaveSink { Result = false };
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot());

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Contains("nicht erreichbar", ctrl.Status);
    }

    [Fact]
    public void AddCurve_ThenDelete_UpdatesCollectionAndSelection()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot());
        Assert.Empty(ctrl.Curves);

        ctrl.AddCurveCommand.Execute(null);
        Assert.Single(ctrl.Curves);
        Assert.NotNull(ctrl.SelectedCurve);
        Assert.Equal(5, ctrl.SelectedCurve!.Points.Count); // Default-Stützpunkte (mehr als das frühere 30/80-Paar)

        ctrl.DeleteCurveCommand.Execute(null);
        Assert.Empty(ctrl.Curves);
        Assert.Null(ctrl.SelectedCurve);
    }

    [Fact]
    public void DeleteCurve_ClearsAssignmentsReferencingIt()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot());
        ctrl.AddCurveCommand.Execute(null);
        CurveEditRow curve = ctrl.SelectedCurve!;
        FanAssignRow fan = Assert.Single(ctrl.Fans);
        fan.Selected = curve;

        ctrl.DeleteCurveCommand.Execute(null);

        Assert.Null(fan.Selected); // Zuordnung wurde mit gelöscht
    }

    // --- InterpolationMode-Fluss ----------------------------------------------------------------

    [Theory]
    [InlineData(InterpolationMode.Linear)]
    [InlineData(InterpolationMode.Spline)]
    public async Task Save_InterpolationMode_FlowsIntoConfig(InterpolationMode mode)
    {
        var config = new AppConfig
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Test", SourceSensorId = "hwmon6/temp1",
                    InterpolationMode = mode,
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1" } },
        };

        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(config));

        CurveEditRow row = Assert.Single(ctrl.Curves);
        Assert.Equal(mode, row.InterpolationMode);

        await ctrl.SaveCommand.ExecuteAsync(null);

        AppConfig sent = Assert.Single(sink.Saved);
        Assert.Equal(mode, Assert.Single(sent.Curves).InterpolationMode);
    }

    [Fact]
    public void From_CurveConfig_WithSpline_SetsInterpolationMode()
    {
        var config = new AppConfig
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Spline-Kurve", SourceSensorId = "hwmon6/temp1",
                    InterpolationMode = InterpolationMode.Spline,
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1" } },
        };

        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));

        CurveEditRow row = Assert.Single(ctrl.Curves);
        Assert.Equal(InterpolationMode.Spline, row.InterpolationMode);
        // SelectedInterpolation zeigt die Option an.
        Assert.Equal(InterpolationMode.Spline, row.SelectedInterpolation.Value);
    }

    // ---------------------------------------------------------------------------
    // Dirty-State (ungespeicherte Änderungen)
    // ---------------------------------------------------------------------------

    private static AppConfig ConfigWithProfile() => new()
    {
        Profiles = new[]
        {
            new Profile
            {
                Id = "default", Name = "Standard",
                Curves = new[]
                {
                    new CurveConfig
                    {
                        Id = "c1", Name = "C", SourceSensorIds = new[] { "hwmon6/temp1" },
                        Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                    },
                },
                Assignments = Array.Empty<ProfileAssignment>(),
            },
        },
        ActiveProfileId = "default",
        Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "Fan" } },
    };

    [Fact]
    public void Initialize_MarksReady_AndNoUnsavedChanges()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(ConfigWithProfile()));

        Assert.True(ctrl.IsReady);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public async Task Resync_RebuildsFromNewConfig_DiscardsLocalEdits()
    {
        // Erst mit „Quiet" initialisieren und lokal bearbeiten (dirty) …
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(new AppConfig
        {
            Curves = new[] { new CurveConfig { Id = "c1", Name = "Quiet", SourceSensorIds = new[] { "hwmon6/temp1" }, Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) } } },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "Fan", AssignedCurveId = "c1" } },
        }));
        ctrl.Curves.Single().Name = "LokaleÄnderung";
        Assert.True(ctrl.HasUnsavedChanges);

        // … dann Reset/Import: mit einer anderen Config neu aufbauen.
        ctrl.Resync(Snapshot(new AppConfig
        {
            Curves = new[] { new CurveConfig { Id = "c2", Name = "Cool", SourceSensorIds = new[] { "hwmon6/temp1" }, Points = new[] { new CurvePoint(40, 30) } } },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "Fan", AssignedCurveId = "c2" } },
        }));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        Assert.Equal("Cool", curve.Name);          // neue Config, nicht die lokale Änderung
        Assert.Equal("c2", curve.Id);
        Assert.True(ctrl.IsReady);
        Assert.False(ctrl.HasUnsavedChanges);       // Baseline entspricht der neuen Config → sauber

        // Ein anschließendes Speichern schreibt die NEUE Config zurück (nicht die alte „Quiet"/„LokaleÄnderung").
        await ctrl.SaveCommand.ExecuteAsync(null);
        AppConfig sent = Assert.Single(sink.Saved);
        Assert.Equal("Cool", Assert.Single(sent.Curves).Name);
    }

    [Fact]
    public void Edit_MarksUnsavedChanges_Immediately()
    {
        AppConfig config = ConfigWithProfile();
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.Curves[0].Name = "Geändert";   // Editor-Änderung

        Assert.True(ctrl.HasUnsavedChanges); // sofort dirty — ohne auf den nächsten Tick zu warten
    }

    [Fact]
    public void UpdateLive_PopulatesLiveSensorTemperaturesAndFanRpm()
    {
        AppConfig config = ConfigWithProfile();
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));

        ctrl.UpdateLive(Snapshot(config)); // Live-Werte aus dem Snapshot in die Geräte-Zeilen übernehmen

        Assert.Equal("40.0 °C", ctrl.Sensors.First(s => s.Id == "hwmon6/temp1").LiveValue);
        Assert.Equal("1900 RPM", ctrl.Fans.First(f => f.FanId == "hwmon7/pwm1").LiveRpm);
    }

    // Regression zum Dirty-Funnel: Live-Werte UND Kalibrier-/Identify-Status sind reine Anzeige und stehen
    // bewusst NICHT in der Whitelist der On*RowChanged-/ConfigChanged-Handler — ein Tick darf den Editor NIE
    // als „ungespeichert" markieren (sonst kehrte die Pro-Tick-Serialisierung zurück).
    [Fact]
    public void UpdateLive_WithChangedLiveValuesAndStatus_DoesNotMarkDirty()
    {
        AppConfig config = ConfigWithProfile();
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));
        Assert.False(ctrl.HasUnsavedChanges);

        // Ein Tick mit ABWEICHENDEN Live-Werten (andere Temperaturen + Drehzahl) plus laufendem Kalibrier-
        // und Identify-Status — gleiche Config.
        var live = new MonitorSnapshot(
            "test",
            new[]
            {
                new SensorReading("hwmon6/temp1", "k10temp Tctl", SensorKind.Temperature, "°C", 71),
                new SensorReading("hwmon7/temp1", "thinkpad CPU", SensorKind.Temperature, "°C", 66),
            },
            new[] { new FanReading("hwmon7/pwm1", "thinkpad pwm1", 2600, 255, FanMode.Auto, true) },
            config)
        {
            Calibration = new CalibrationStatus("hwmon7/pwm1", CalibrationPhase.Measuring, 120, 1500,
                                                Running: true, Done: false, StartPwm: null, FailReason: null),
            Identify = new IdentifyStatus("hwmon7/pwm1", true, null),
        };

        ctrl.UpdateLive(live);

        Assert.False(ctrl.HasUnsavedChanges); // weder Live-Werte noch Status sind ungespeicherte Editor-Änderungen
        Assert.Equal("71.0 °C", ctrl.Sensors.First(s => s.Id == "hwmon6/temp1").LiveValue);
        Assert.Equal("2600 RPM", ctrl.Fans.First(f => f.FanId == "hwmon7/pwm1").LiveRpm);
    }

    [Fact]
    public async Task Save_ResetsUnsavedChanges()
    {
        AppConfig config = ConfigWithProfile();
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(config));
        ctrl.Curves[0].Name = "Geändert";
        Assert.True(ctrl.HasUnsavedChanges);

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.False(ctrl.HasUnsavedChanges);
    }

    // ---------------------------------------------------------------------------
    // Dirty-Erkennung je Edit-Pfad — edit-getrieben (nicht pro Tick): sofort dirty, OHNE UpdateLive.
    // Ein vergessener Pfad ⇒ HasUnsavedChanges bliebe falsch (das Risiko dieses Umbaus).
    // ---------------------------------------------------------------------------

    private static CurveEditorController CleanEditor()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        Assert.False(ctrl.HasUnsavedChanges); // Ausgangspunkt: nichts offen
        return ctrl;
    }

    [Fact]
    public void Edit_CurveName_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Curves.Single().Name = "Geändert";
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_CurveHysteresis_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Curves.Single().Hysteresis = 5m;
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_CurveAggregation_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Curves.Single().SelectedAggregation = AggregationOption.For(SensorAggregation.Avg);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_CurveInterpolation_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Curves.Single().SelectedInterpolation = InterpolationOption.For(InterpolationMode.Spline);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_CurveSourceSelection_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        SensorCheck check = ctrl.Curves.Single().SensorChecks.First(c => c.Sensor.Id == "hwmon7/temp1");
        check.Selected = true; // bislang nicht gewählte Quelle ankreuzen → SourceSensorIds ändern sich
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_CurveAddPoint_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Curves.Single().AddPointCommand.Execute(null);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_CurveRemovePoint_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Curves.Single().Points.First().RemoveCommand!.Execute(null);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_CurvePointTemperature_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Curves.Single().Points.First().Temperature = 42m; // der Graph-Drag-Pfad
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_CurvePointPercent_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Curves.Single().Points.First().Percent = 42m; // der Graph-Drag-Pfad
        Assert.True(ctrl.HasUnsavedChanges);
    }

    // Coalescing des Dirty-Vergleichs (Review-Effizienz): Ein Punkt-Drag darf nicht pro Maus-Sample die ganze
    // Config serialisieren. Aus dem sauberen Zustand greift dirty weiterhin sofort (oben getestet); im bereits
    // dirty-Zustand wird die einzige Rück-Transition (Edit exakt zurück auf die Baseline) erst am nächsten
    // Live-Tick nachgezogen — Bedeutung/Speicherzeitpunkt bleiben gleich, nur der Vergleich läuft nicht je Sample.
    [Fact]
    public void PointDragBackToBaseline_StaysDirtyUntilTickReconciles()
    {
        CurveEditorController ctrl = CleanEditor();
        var point = ctrl.Curves.Single().Points.First();
        decimal baseTemp = point.Temperature;

        point.Temperature = baseTemp + 10m; // erster Drag-Sample: clean→dirty, sofort erkannt
        Assert.True(ctrl.HasUnsavedChanges);

        point.Temperature = baseTemp;        // zurück auf Baseline, während dirty → koalesziert (kein Serialize je Sample)
        Assert.True(ctrl.HasUnsavedChanges); // Banner bleibt vorerst stehen …

        ctrl.UpdateLive(Snapshot(CurveAndFanConfig())); // … der Tick zieht den zurückgestellten Vergleich nach
        Assert.False(ctrl.HasUnsavedChanges);           // Editor entspricht wieder exakt der Baseline
    }

    [Fact]
    public void Edit_AddCurve_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.AddCurveCommand.Execute(null);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_DuplicateCurve_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.DuplicateCurveCommand.Execute(null);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanName_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Fans.Single().Name = "CPU-Lüfter";
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanAssignment_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Fans.Single().Selected = ctrl.Curves.Single();
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanAssignment_ViaFanCurveCheck_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.SelectedCurveFans.Single().Assigned = true; // Checkbox setzt FanAssignRow.Selected
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanLocation_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Fans.Single().Location = FanLocationOption.For(FanLocation.CaseRearExhaust);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanGroup_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Fans.Single().Group = "Gehäuse";
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanVisible_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Fans.Single().Visible = false;
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanMinPwm_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Fans.Single().MinPwm = 80;
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanMaxPwm_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Fans.Single().MaxPwm = 200;
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_FanMinPercent_MarksDirty_ViaMinPwm()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Fans.Single().MinPercent = 50; // reiner Anzeige-Spiegel → setzt MinPwm → markiert dirty
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_SensorName_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Sensors.First(s => s.Id == "hwmon6/temp1").Name = "CPU-Paket";
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_SensorVisible_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Sensors.First(s => s.Id == "hwmon6/temp1").Visible = false;
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_SensorGroup_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.Sensors.First(s => s.Id == "hwmon6/temp1").Group = "CPU";
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_AddProfile_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.AddProfileCommand.Execute(null);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_DuplicateProfile_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.DuplicateProfileCommand.Execute(null);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void Edit_RenameProfile_MarksDirty()
    {
        CurveEditorController ctrl = CleanEditor();
        ctrl.SelectedProfile!.Name = "Neuer Name";
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public async Task Edit_SwitchActiveProfile_MarksDirty()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ProfileRow first = ctrl.SelectedProfile!;
        ctrl.AddProfileCommand.Execute(null);      // zweites Profil (aktiv) → Editor dirty
        await ctrl.SaveCommand.ExecuteAsync(null);  // Baseline = zwei Profile, das zweite aktiv
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.SelectedProfile = first;               // zurück auf das erste → ActiveProfileId ändert sich

        Assert.True(ctrl.HasUnsavedChanges);
    }

    // ---------------------------------------------------------------------------
    // Bedingtes Auto-Save: bestätigte Löschung speichert nur, wenn der Editor vorher sauber war
    // (sonst würden fremde, unfertige Änderungen ungewollt mit-committet).
    // ---------------------------------------------------------------------------

    private static AppConfig TwoProfileConfig()
    {
        var curveA = new CurveConfig
        {
            Id = "ca",
            Name = "A",
            SourceSensorIds = new[] { "hwmon6/temp1" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var curveB = new CurveConfig
        {
            Id = "cb",
            Name = "B",
            SourceSensorIds = new[] { "hwmon6/temp1" },
            Points = new[] { new CurvePoint(40, 30), new CurvePoint(90, 100) },
        };
        return new AppConfig
        {
            Curves = new[] { curveA },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1", MinPwm = 40, MaxPwm = 255 } },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p1", Name = "Eins", Curves = new[] { curveA }, Assignments = Array.Empty<ProfileAssignment>(),
                },
                new Profile
                {
                    Id = "p2", Name = "Zwei", Curves = new[] { curveB }, Assignments = Array.Empty<ProfileAssignment>(),
                },
            },
            ActiveProfileId = "p1",
        };
    }

    [Fact]
    public async Task DeleteCurve_WhenClean_AutoSaves_WithoutTheCurve()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Single(ctrl.Curves);

        await ctrl.DeleteCurveCommand.ExecuteAsync(null);

        Assert.Empty(ctrl.Curves);
        AppConfig saved = Assert.Single(sink.Saved); // genau ein Auto-Save
        Assert.Empty(saved.Curves);                  // die Löschung wurde persistiert
        Assert.False(ctrl.HasUnsavedChanges);        // → sauber
    }

    [Fact]
    public async Task DeleteCurve_WhenDirty_DeletesButDoesNotAutoSave()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.Fans.Single().Name = "vorher geändert"; // Editor schon dirty
        Assert.True(ctrl.HasUnsavedChanges);

        await ctrl.DeleteCurveCommand.ExecuteAsync(null);

        Assert.Empty(ctrl.Curves);            // gelöscht
        Assert.Empty(sink.Saved);             // aber NICHT auto-gespeichert (fremde Änderung läge sonst mit drin)
        Assert.True(ctrl.HasUnsavedChanges);  // bleibt offen
    }

    [Fact]
    public async Task DeleteProfile_WhenClean_AutoSaves()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(TwoProfileConfig()));
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Equal(2, ctrl.Profiles.Count);

        await ctrl.DeleteProfileCommand.ExecuteAsync(null); // löscht das aktive Profil (p1)

        Assert.Single(ctrl.Profiles);
        AppConfig saved = Assert.Single(sink.Saved);
        Assert.Single(saved.Profiles);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public async Task DeleteProfile_WhenDirty_DeletesButDoesNotAutoSave()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(TwoProfileConfig()));
        ctrl.Fans.Single().Name = "vorher geändert";
        Assert.True(ctrl.HasUnsavedChanges);

        await ctrl.DeleteProfileCommand.ExecuteAsync(null);

        Assert.Single(ctrl.Profiles);
        Assert.Empty(sink.Saved);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public async Task DeleteCurve_WhenClean_SaveFails_KeepsChangePending()
    {
        var sink = new SaveSink { Result = false }; // Daemon nicht erreichbar
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        Assert.False(ctrl.HasUnsavedChanges);

        await ctrl.DeleteCurveCommand.ExecuteAsync(null);

        Assert.Empty(ctrl.Curves);           // lokal gelöscht
        Assert.Single(sink.Saved);           // ein Save-Versuch
        Assert.True(ctrl.HasUnsavedChanges); // fehlgeschlagen → Löschung bleibt offen (kein stiller Verlust)
    }

    [Fact]
    public async Task Fan_Calibrate_FlowsThroughControllerDelegate()
    {
        var calls = new List<string>();
        var ctrl = new CurveEditorController(calibrate: id => { calls.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot()); // Default-Snapshot: hwmon7/pwm1 ist steuerbar

        FanAssignRow fan = Assert.Single(ctrl.Fans);
        Assert.True(fan.CanControl);

        await fan.CalibrateCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "hwmon7/pwm1" }, calls);
    }

    [Fact]
    public async Task Fan_Identify_FlowsThroughControllerDelegate()
    {
        var calls = new List<string>();
        var ctrl = new CurveEditorController(identify: id => { calls.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot()); // Default-Snapshot: hwmon7/pwm1 ist steuerbar

        FanAssignRow fan = Assert.Single(ctrl.Fans);
        Assert.True(fan.CanControl);

        await fan.IdentifyCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "hwmon7/pwm1" }, calls);
    }

    [Fact]
    public void UpdateLive_RoutesIdentifyStatus_ToMatchingRow()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot());

        FanAssignRow fan = Assert.Single(ctrl.Fans);
        Assert.False(fan.IsIdentifying);

        MonitorSnapshot withIdentify = Snapshot() with
        {
            Identify = new IdentifyStatus("hwmon7/pwm1", Running: true, FailReason: null),
        };
        ctrl.UpdateLive(withIdentify);

        Assert.True(fan.IsIdentifying);
        Assert.Contains("100 %", fan.IdentifyProgress);
    }

    // ---------------------------------------------------------------------------
    // Kalibrier-Ergebnis (MinPwm) aus dem Geräte-Tab — der Daemon ändert die Config hier ohne
    // Zutun des Editors. Regression: die einmalig befüllte Zeile blieb auf dem alten Anlaufpunkt
    // und das nächste Speichern schrieb das Ergebnis wieder weg.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task UpdateLive_AdoptsCalibratedMinPwm_AndSaveKeepsIt()
    {
        AppConfig config = CurveAndFanConfig(); // MinPwm 40
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(config));

        FanAssignRow fan = Assert.Single(ctrl.Fans);
        Assert.Equal(40, fan.MinPwm);

        // Daemon hat kalibriert und den Anlaufpunkt persistiert.
        ctrl.UpdateLive(Snapshot(WithMinPwm(config, "hwmon7/pwm1", 96)));

        Assert.Equal(96, fan.MinPwm);
        Assert.False(ctrl.HasUnsavedChanges); // Kalibrierung ist keine ungespeicherte Nutzer-Änderung

        await ctrl.SaveCommand.ExecuteAsync(null);
        Assert.Equal(96, Assert.Single(sink.Saved).Fans.Single(f => f.FanId == "hwmon7/pwm1").MinPwm);
    }

    // „Verwerfen" darf das übernommene Kalibrier-Ergebnis nicht auf den Vor-Kalibrier-Wert zurückdrehen —
    // die Baseline muss mitgezogen worden sein.
    [Fact]
    public void Revert_AfterCalibration_KeepsCalibratedMinPwm()
    {
        AppConfig config = CurveAndFanConfig();
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.Initialize(Snapshot(config));

        FanAssignRow fan = Assert.Single(ctrl.Fans);
        ctrl.UpdateLive(Snapshot(WithMinPwm(config, "hwmon7/pwm1", 96)));

        fan.Name = "Umbenannt"; // eine echte Nutzer-Änderung, damit „Verwerfen" überhaupt ausführbar ist
        Assert.True(ctrl.HasUnsavedChanges);
        ctrl.RevertCommand.Execute(null);

        Assert.Equal("thinkpad pwm1", fan.Name); // Nutzer-Änderung verworfen …
        Assert.Equal(96, fan.MinPwm);            // … Kalibrier-Ergebnis bleibt
    }

    // Gegenprobe: ein Tick mit unveränderter Config darf eine getippte, noch nicht gespeicherte
    // MinPwm-Eingabe nicht zurücksetzen (sonst wäre das Feld nicht mehr bedienbar).
    [Fact]
    public void UpdateLive_WithUnchangedConfig_KeepsEditedMinPwm()
    {
        AppConfig config = CurveAndFanConfig();
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.Initialize(Snapshot(config));

        FanAssignRow fan = Assert.Single(ctrl.Fans);
        fan.MinPwm = 77;
        ctrl.UpdateLive(Snapshot(config));

        Assert.Equal(77, fan.MinPwm);
        Assert.True(ctrl.HasUnsavedChanges);
    }

    private static AppConfig WithMinPwm(AppConfig config, string fanId, byte minPwm) => config with
    {
        Fans = config.Fans.Select(f => f.FanId == fanId ? f with { MinPwm = minPwm } : f).ToList(),
    };

    // ---------------------------------------------------------------------------
    // Airflow-Auto-Tune
    // ---------------------------------------------------------------------------

    // Snapshot mit drei positionierten Lüftern (CPU-Kühler, Gehäuse-Einlass, Gehäuse-Auslass).
    private static MonitorSnapshot AirflowSnapshot()
    {
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon6/temp1", Name = "k10temp Tctl" } },
            Fans = new[]
            {
                new FanConfig { FanId = "cpu", Name = "CPU", Location = FanLocation.CpuCooler },
                new FanConfig { FanId = "front", Name = "Front", Location = FanLocation.CaseFrontIntake },
                new FanConfig { FanId = "rear", Name = "Rear", Location = FanLocation.CaseRearExhaust },
            },
            Profiles = new[]
            {
                new Profile { Id = "default", Name = "Standard", Curves = Array.Empty<CurveConfig>(),
                    Assignments = Array.Empty<ProfileAssignment>() },
            },
            ActiveProfileId = "default",
        };
        return new MonitorSnapshot(
            "test",
            new[] { new SensorReading("hwmon6/temp1", "k10temp Tctl", SensorKind.Temperature, "°C", 40) },
            new[]
            {
                new FanReading("cpu", "CPU", 1500, 128, FanMode.Auto, true),
                new FanReading("front", "Front", 1000, 128, FanMode.Auto, true),
                new FanReading("rear", "Rear", 1200, 128, FanMode.Auto, true),
            },
            config);
    }

    [Fact]
    public void AnalyzeAirflow_PopulatesSuggestionsAndPressure()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshot());

        ctrl.AnalyzeAirflowCommand.Execute(null);

        Assert.True(ctrl.HasAirflowSuggestion);
        Assert.Equal(3, ctrl.AirflowSuggestions.Count);
        Assert.Contains("ausgeglichen", ctrl.AirflowPressureText); // 1 Einlass : 1 Auslass → ausgeglichen
        AirflowSuggestionRow cpu = ctrl.AirflowSuggestions.First(r => r.FanId == "cpu");
        Assert.Contains("CPU", cpu.CurveName);
        Assert.True(cpu.Apply); // mit Kurve → standardmäßig angekreuzt
    }

    [Fact]
    public async Task ApplyAirflow_ThenSave_SendsAirflowCurvesAndAssignments()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(AirflowSnapshot());

        ctrl.AnalyzeAirflowCommand.Execute(null);
        ctrl.ApplyAirflowCommand.Execute(null);

        // Der Vorschlag ist in den Editor geladen, die Vorschau ausgeblendet, Stand ungespeichert.
        Assert.Contains(ctrl.Curves, c => c.Id == "airflow-cpu");
        Assert.False(ctrl.HasAirflowSuggestion);
        Assert.True(ctrl.HasUnsavedChanges);

        await ctrl.SaveCommand.ExecuteAsync(null);

        AppConfig sent = Assert.Single(sink.Saved);
        Assert.Contains(sent.Curves, c => c.Id == "airflow-cpu");
        Assert.Equal("airflow-cpu", sent.Fans.First(f => f.FanId == "cpu").AssignedCurveId);
        Assert.Equal("airflow-intake", sent.Fans.First(f => f.FanId == "front").AssignedCurveId);
        Assert.Equal("airflow-exhaust", sent.Fans.First(f => f.FanId == "rear").AssignedCurveId);
    }

    [Fact]
    public async Task ApplyAirflow_SkipsUncheckedFans()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(AirflowSnapshot());
        ctrl.AnalyzeAirflowCommand.Execute(null);

        ctrl.AirflowSuggestions.First(r => r.FanId == "cpu").Apply = false; // CPU-Vorschlag abwählen

        ctrl.ApplyAirflowCommand.Execute(null);
        await ctrl.SaveCommand.ExecuteAsync(null);

        AppConfig sent = Assert.Single(sink.Saved);
        Assert.Null(sent.Fans.First(f => f.FanId == "cpu").AssignedCurveId);           // nicht übernommen
        Assert.Equal("airflow-intake", sent.Fans.First(f => f.FanId == "front").AssignedCurveId);
        Assert.DoesNotContain(sent.Curves, c => c.Id == "airflow-cpu");                // ungenutzte Kurve weggelassen
    }

    [Fact]
    public void AnalyzeAirflow_BeforeInitialize_DoesNothing()
    {
        var ctrl = new CurveEditorController();

        ctrl.AnalyzeAirflowCommand.Execute(null);

        Assert.False(ctrl.HasAirflowSuggestion);
        Assert.Empty(ctrl.AirflowSuggestions);
    }

    // Regression: nach Ablauf einer Auto-Hide-Statusmeldung darf der nächste Status nicht auf einem
    // bereits entsorgten CancellationTokenSource Cancel() aufrufen (ObjectDisposedException beim Speichern).
    [Fact]
    public async Task SetStatus_AfterAutoHideElapsed_DoesNotThrowOnNextStatus()
    {
        var sink = new SaveSink();
        // Sehr kurze Auto-Hide-Dauer, damit der Timer im Test sicher abläuft.
        var ctrl = new CurveEditorController(sink.SaveAsync, statusAutoHide: TimeSpan.FromMilliseconds(20));
        ctrl.Initialize(AirflowSnapshot());

        ctrl.AnalyzeAirflowCommand.Execute(null);
        ctrl.ApplyAirflowCommand.Execute(null); // setzt einen Auto-Hide-Status (CTS #1)
        await Task.Delay(150);                  // Auto-Hide läuft ab → CTS #1 wird entsorgt

        // Früher: ObjectDisposedException, weil _statusCts noch auf das entsorgte CTS #1 zeigte.
        await ctrl.SaveCommand.ExecuteAsync(null); // setzt erneut einen Auto-Hide-Status

        Assert.Single(sink.Saved);
        Assert.Contains("Gespeichert", ctrl.Status);
    }

    // --- Verwerfen (Revert) + Aktiv-Badge ------------------------------------------------------

    private static AppConfig CurveAndFanConfig()
    {
        var curve = new CurveConfig
        {
            Id = "c1",
            Name = "Quiet",
            SourceSensorIds = new[] { "hwmon6/temp1" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        return new AppConfig
        {
            Curves = new[] { curve },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1", MinPwm = 40, MaxPwm = 255 } },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p1", Name = "Std", Curves = new[] { curve },
                    Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", null) },
                },
            },
            ActiveProfileId = "p1",
        };
    }

    [Fact]
    public void Revert_RestoresBaseline_AndClearsDirty()
    {
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        Assert.False(ctrl.HasUnsavedChanges);

        // Quer editieren: Kurve, Zuordnung und Geräte-Config.
        CurveEditRow curve = Assert.Single(ctrl.Curves);
        curve.Name = "Geändert";
        curve.AddPointRow(55, 50);
        FanAssignRow fan = Assert.Single(ctrl.Fans);
        fan.Selected = curve;
        fan.MinPwm = 99;
        SensorOption sensor = ctrl.Sensors.First(s => s.Id == "hwmon6/temp1");
        sensor.Visible = false;
        Assert.True(ctrl.HasUnsavedChanges);

        ctrl.RevertCommand.Execute(null);

        Assert.False(ctrl.HasUnsavedChanges);
        CurveEditRow reverted = Assert.Single(ctrl.Curves);
        Assert.Equal("Quiet", reverted.Name);
        Assert.Equal(2, reverted.Points.Count);
        Assert.Null(ctrl.Fans.Single().Selected);   // Baseline: nicht zugeordnet
        Assert.Equal(40, ctrl.Fans.Single().MinPwm);
        Assert.True(ctrl.Sensors.First(s => s.Id == "hwmon6/temp1").Visible);
    }

    [Fact]
    public void Revert_CanExecute_TracksHasUnsavedChanges()
    {
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        Assert.False(ctrl.RevertCommand.CanExecute(null)); // nichts zu verwerfen

        Assert.Single(ctrl.Curves).Name = "Geändert";
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.True(ctrl.RevertCommand.CanExecute(null));
    }

    [Fact]
    public void AssigningFan_NotifiesCurveIsActive()
    {
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        FanAssignRow fan = Assert.Single(ctrl.Fans);
        Assert.False(curve.IsActive); // Quelle vorhanden, aber (noch) kein Lüfter zugeordnet

        var raised = new List<string?>();
        curve.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        fan.Selected = curve; // Zuordnung → Controller wertet das Badge neu aus
        Assert.True(curve.IsActive);
        Assert.Contains(nameof(CurveEditRow.IsActive), raised);
    }

    // --- Profil/Kurve anlegen + duplizieren + Namensfeld -----------------------------------------

    [Fact]
    public void AddProfile_IsEmpty_OneDefaultCurve_NoAssignments_OpensNaming()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.Fans.Single().Selected = ctrl.Curves.Single(); // vorher zuordnen

        int profilesBefore = ctrl.Profiles.Count;
        ctrl.AddProfileCommand.Execute(null);

        Assert.Equal(profilesBefore + 1, ctrl.Profiles.Count);
        Assert.True(ctrl.IsNamingProfile);              // Namensfeld eingeblendet
        CurveEditRow curve = Assert.Single(ctrl.Curves); // genau eine Default-Kurve
        Assert.Equal("Neue Kurve", curve.Name);
        Assert.Equal(5, curve.Points.Count);            // Standard-Stützpunkte
        Assert.Null(ctrl.Fans.Single().Selected);       // leeres Profil → keine Zuordnung
    }

    [Fact]
    public void DuplicateProfile_CopiesCurrentState_WithKopieName_OpensNaming()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        CurveEditRow curve = ctrl.Curves.Single();
        ctrl.Fans.Single().Selected = curve; // Zuordnung, die mitkopiert werden soll
        string activeName = ctrl.SelectedProfile!.Name;

        ctrl.DuplicateProfileCommand.Execute(null);

        Assert.True(ctrl.IsNamingProfile);
        Assert.Equal($"{activeName} (Kopie)", ctrl.SelectedProfile!.Name);
        Assert.Single(ctrl.Curves);                     // Kopie = aktueller Stand
        Assert.NotNull(ctrl.Fans.Single().Selected);    // Zuordnung mitkopiert
    }

    [Fact]
    public void DuplicateCurve_CopiesSelected_WithNewIdAndKopieName()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        CurveEditRow original = Assert.Single(ctrl.Curves);

        ctrl.DuplicateCurveCommand.Execute(null);

        Assert.Equal(2, ctrl.Curves.Count);
        CurveEditRow copy = ctrl.SelectedCurve!;
        Assert.NotSame(original, copy);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal($"{original.Name} (Kopie)", copy.Name);
        Assert.Equal(original.Points.Count, copy.Points.Count);
    }

    // --- Gruppen-Auswahl (Auto-Vervollständigung) ----------------------------------------------

    private static AppConfig ConfigWithGroups() => new()
    {
        Sensors = new[] { new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl", Group = "CPU" } },
        Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "Fan", Group = "Gehäuse" } },
    };

    [Fact]
    public void Initialize_PopulatesAvailableGroups_FromSensorsAndFans_DistinctSorted()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(ConfigWithGroups()));

        // Vereinigung beider Quellen, sortiert; jede Zeile teilt dieselbe Instanz.
        Assert.Equal(new[] { "CPU", "Gehäuse" }, ctrl.AvailableGroups);
        Assert.Same(ctrl.AvailableGroups, ctrl.Sensors[0].AvailableGroups);
        Assert.Same(ctrl.AvailableGroups, ctrl.Fans[0].AvailableGroups);
    }

    [Fact]
    public void AvailableGroups_ExcludeEmpty_AndDedupeCaseInsensitively()
    {
        var config = new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon6/temp1", Name = "A", Group = "CPU" },
                new SensorConfig { SensorId = "hwmon7/temp1", Name = "B", Group = "  " }, // leer → kein Vorschlag
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "F", Group = "cpu" } }, // Dublette (Groß/klein)
        };

        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));

        Assert.Equal(new[] { "CPU" }, ctrl.AvailableGroups); // nur einmal, Whitespace raus
    }

    [Fact]
    public void EditingGroup_AddsNewName_ToAvailableGroups()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(ConfigWithGroups()));

        ctrl.Fans[0].Group = "Gehäuse oben"; // neuer Name auf einer Lüfter-Zeile

        Assert.Contains("Gehäuse oben", ctrl.AvailableGroups); // steht den anderen Zeilen sofort als Vorschlag bereit
    }

    [Fact]
    public void ProfileNaming_ClosesOnFinish_AndOnRealProfileSwitch()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));

        ctrl.RenameProfileCommand.Execute(null);
        Assert.True(ctrl.IsNamingProfile);
        ctrl.FinishNamingProfileCommand.Execute(null);
        Assert.False(ctrl.IsNamingProfile);

        ctrl.AddProfileCommand.Execute(null);   // öffnet das Namensfeld
        Assert.True(ctrl.IsNamingProfile);
        ctrl.SelectedProfile = ctrl.Profiles.First(); // echter Wechsel → schließt es
        Assert.False(ctrl.IsNamingProfile);
    }

    // --- Geräte-Tab-Filter (Suche + „Versteckte ausblenden") -----------------------------------

    // Drei Sensoren (einer per Config versteckt) + zwei Lüfter (einer versteckt) für die Filtertests.
    private static MonitorSnapshot FilterSnapshot() => new(
        "test",
        new[]
        {
            new SensorReading("hwmon6/temp1", "k10temp Tctl", SensorKind.Temperature, "°C", 41),
            new SensorReading("hwmon11/temp1", "amdgpu edge", SensorKind.Temperature, "°C", 38),
            new SensorReading("hwmon9/temp1", "spd5118 RAM", SensorKind.Temperature, "°C", 35),
        },
        new[]
        {
            new FanReading("cpu", "CPU", 1500, 128, FanMode.Auto, true),
            new FanReading("front", "Front", 1000, 128, FanMode.Auto, true),
        },
        new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon9/temp1", Name = "spd5118 RAM", Hidden = true } },
            Fans = new[] { new FanConfig { FanId = "front", Name = "Front", Hidden = true } },
        });

    [Fact]
    public void FilteredLists_InitiallyContainAllDevices()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        Assert.Equal(3, ctrl.FilteredSensors.Count);
        Assert.Equal(2, ctrl.FilteredFans.Count);
    }

    [Theory]
    [InlineData("amd")]
    [InlineData("AMD")] // case-insensitive
    public void SensorSearch_FiltersByName(string query)
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        ctrl.SensorSearch = query;

        SensorOption only = Assert.Single(ctrl.FilteredSensors);
        Assert.Equal("amdgpu edge", only.Name);
    }

    [Fact]
    public void SensorSearch_AlsoMatchesHardwareId()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        ctrl.SensorSearch = "hwmon9"; // nur über die Id (Name enthält „hwmon9" nicht)

        Assert.Equal("hwmon9/temp1", Assert.Single(ctrl.FilteredSensors).Id);
    }

    [Fact]
    public void SensorSearch_ClearedRestoresAll()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        ctrl.SensorSearch = "amd";
        Assert.Single(ctrl.FilteredSensors);

        ctrl.SensorSearch = "   "; // whitespace zählt wie leer
        Assert.Equal(3, ctrl.FilteredSensors.Count);
    }

    [Fact]
    public void HideHiddenSensors_ExcludesInvisible()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        ctrl.HideHiddenSensors = true;

        Assert.Equal(2, ctrl.FilteredSensors.Count);
        Assert.DoesNotContain(ctrl.FilteredSensors, s => s.Id == "hwmon9/temp1");
    }

    [Fact]
    public void TogglingVisibility_WhileHidingHidden_UpdatesFilteredSensors()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());
        ctrl.HideHiddenSensors = true;
        Assert.Equal(2, ctrl.FilteredSensors.Count);

        SensorOption k10 = ctrl.Sensors.First(s => s.Id == "hwmon6/temp1");
        k10.Visible = false; // jetzt fällt auch dieser aus der gefilterten Sicht
        Assert.Single(ctrl.FilteredSensors);

        k10.Visible = true;
        Assert.Equal(2, ctrl.FilteredSensors.Count);
    }

    [Fact]
    public void FanSearch_FiltersByName()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        ctrl.FanSearch = "front";

        Assert.Equal("front", Assert.Single(ctrl.FilteredFans).FanId);
    }

    [Fact]
    public void HideHiddenFans_ExcludesInvisible_AndReactsToToggle()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        ctrl.HideHiddenFans = true;
        Assert.Equal("cpu", Assert.Single(ctrl.FilteredFans).FanId); // „front" ist versteckt

        ctrl.Fans.First(f => f.FanId == "front").Visible = true;
        Assert.Equal(2, ctrl.FilteredFans.Count);
    }

    // --- Hidden-Fans in der Kurven-Zuordnungsliste („Lüfter dieser Kurve") -----------------------

    // Eine Kurve + zwei Lüfter, „front" ist global versteckt (optional der Kurve zugeordnet).
    private static MonitorSnapshot HiddenFanCurveSnapshot(string? hiddenAssignedCurveId = null) => new(
        "test",
        new[] { new SensorReading("hwmon6/temp1", "k10temp Tctl", SensorKind.Temperature, "°C", 41) },
        new[]
        {
            new FanReading("cpu", "CPU", 1500, 128, FanMode.Auto, true),
            new FanReading("front", "Front", 1000, 128, FanMode.Auto, true),
        },
        new AppConfig
        {
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "K", SourceSensorIds = new[] { "hwmon6/temp1" },
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[]
            {
                new FanConfig { FanId = "cpu", Name = "CPU" },
                new FanConfig { FanId = "front", Name = "Front", Hidden = true, AssignedCurveId = hiddenAssignedCurveId },
            },
        });

    [Fact]
    public void SelectedCurveFans_ExcludeGloballyHiddenFans()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(HiddenFanCurveSnapshot());

        Assert.NotNull(ctrl.SelectedCurve);
        Assert.Equal("cpu", Assert.Single(ctrl.SelectedCurveFans).Fan.FanId);
        Assert.Equal("cpu", Assert.Single(ctrl.SelectedCurveFanGroups.SelectMany(g => g.Fans)).Fan.FanId);
    }

    [Fact]
    public void SelectedCurveFans_KeepHiddenFan_WhileAssignedToSelectedCurve()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(HiddenFanCurveSnapshot(hiddenAssignedCurveId: "c1"));

        FanCurveCheck front = ctrl.SelectedCurveFans.Single(c => c.Fan.FanId == "front");
        Assert.True(front.Assigned);

        // Abwahl entfernt die Zeile bewusst NICHT sofort (kommt aus der Checkbox dieser Liste) —
        // erst der nächste Listen-Aufbau (z. B. Kurvenwechsel) filtert sie heraus.
        front.Assigned = false;
        Assert.Contains(ctrl.SelectedCurveFans, c => c.Fan.FanId == "front");

        ctrl.SelectedCurve = null;
        ctrl.SelectedCurve = ctrl.Curves.First();
        Assert.DoesNotContain(ctrl.SelectedCurveFans, c => c.Fan.FanId == "front");
    }

    [Fact]
    public void ToggleFanVisible_UpdatesSelectedCurveFans_Live()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(HiddenFanCurveSnapshot());
        Assert.Single(ctrl.SelectedCurveFans);

        FanAssignRow front = ctrl.Fans.First(f => f.FanId == "front");
        front.Visible = true; // Auge-Toggle im Geräte-Tab → sofort in der Kurven-Zuordnungsliste
        Assert.Equal(2, ctrl.SelectedCurveFans.Count);

        front.Visible = false; // nicht zugeordnet → verschwindet wieder
        Assert.Equal("cpu", Assert.Single(ctrl.SelectedCurveFans).Fan.FanId);
    }

    // --- Gruppierung im Kurven-Tab (Lüfter-Zuordnung + Quell-Sensoren), wie im Dashboard ---------

    // Drei Lüfter: einer mit eigenem Gruppennamen, einer per Position, einer ohne (→ „Ungruppiert").
    private static MonitorSnapshot MixedFanSnapshot()
    {
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl" } },
            Fans = new[]
            {
                new FanConfig { FanId = "front", Name = "Front", Location = FanLocation.CaseFrontIntake },
                new FanConfig { FanId = "loose", Name = "Loose" }, // Unspecified, keine Gruppe
                new FanConfig { FanId = "named", Name = "Named", Location = FanLocation.CaseRearExhaust, Group = "Custom" },
            },
        };
        return new MonitorSnapshot(
            "test",
            new[] { new SensorReading("hwmon6/temp1", "Tctl", SensorKind.Temperature, "°C", 40) },
            new[]
            {
                new FanReading("front", "Front", 1000, 128, FanMode.Auto, true),
                new FanReading("loose", "Loose", 900, 128, FanMode.Auto, true),
                new FanReading("named", "Named", 1100, 128, FanMode.Auto, true),
            },
            config);
    }

    [Fact]
    public void SelectedCurveFanGroups_GroupsByPosition_CustomGroupWins_UngroupedLast()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(MixedFanSnapshot());
        ctrl.AddCurveCommand.Execute(null); // SelectedCurve gesetzt → Checkboxen + Gruppen aufgebaut

        // Eigener Gruppenname schlägt Position; reine Position bekommt den kurzen Namen; ohne beides „Ungruppiert" zuletzt.
        Assert.Equal(new[] { "Custom", "Front · Einlass", "Ungruppiert" },
            ctrl.SelectedCurveFanGroups.Select(g => g.Name).ToArray());
        Assert.All(ctrl.SelectedCurveFanGroups, g => Assert.Single(g.Fans));
    }

    [Fact]
    public void SelectedCurveFanGroups_FollowPositionEdit()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(MixedFanSnapshot());
        ctrl.AddCurveCommand.Execute(null);

        // „loose" (bisher Ungruppiert) eine Position geben → wandert live in die passende Positionsgruppe.
        ctrl.Fans.First(f => f.FanId == "loose").Location = FanLocationOption.For(FanLocation.CaseTopExhaust);

        Assert.Equal(new[] { "Custom", "Front · Einlass", "Oben · Auslass" },
            ctrl.SelectedCurveFanGroups.Select(g => g.Name).ToArray());
        Assert.DoesNotContain(ctrl.SelectedCurveFanGroups, g => g.Name == "Ungruppiert");
    }

    [Fact]
    public void CurveSourceSensors_AreGroupedByGroup_UngroupedLast()
    {
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl", Group = "CPU" } },
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "K", SourceSensorIds = new[] { "hwmon6/temp1" },
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "Fan" } },
        };

        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        // hwmon6 → Gruppe „CPU"; hwmon7 ohne Gruppe → „Ungruppiert" zuletzt.
        Assert.Equal(new[] { "CPU", "Ungruppiert" },
            curve.DisplayedSensorGroups.Select(g => g.Name).ToArray());
    }
}
