// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Specialized;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;
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

        // Bearbeiten: umbenennen, Punkt hinzufügen, Lüfter zuordnen + Position + Namen setzen.
        curve.Name = "Silent";
        curve.AddPointRow(55, 50);
        FanAssignRow fanRow = Assert.Single(ctrl.Fans);
        fanRow.Selected = curve;
        fanRow.Location = FanLocationOption.For(FanLocation.CaseRearExhaust);
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

        // „Verwerfen" stellt aus der Baseline wieder her - der live persistierte Toggle bleibt erhalten.
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
    public async Task From_GloballyHiddenSensor_StaysOffered_WhileCurveSource()
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

        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(config));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        // The hidden source stays offered and checked (mirror of the fan list): hidden is display-only,
        // so an active source must remain visible/removable - never silently dropped on save.
        SensorCheck hidden = curve.SensorChecks.Single(c => c.Sensor.Id == "hwmon6/temp1");
        Assert.True(hidden.Selected);
        Assert.Contains(curve.SensorChecks, c => c.Sensor.Id == "hwmon7/temp1");

        await ctrl.SaveCommand.ExecuteAsync(null);
        CurveConfig saved = Assert.Single(Assert.Single(sink.Saved).Curves);
        Assert.Contains("hwmon6/temp1", saved.SourceSensorIds);
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
        Assert.Equal("p-silent", ctrl.ActiveProfile!.Id);
        Assert.Equal("quiet", Assert.Single(ctrl.Fans).Selected!.Id);

        ctrl.SelectedProfile = ctrl.Profiles.First(p => p.Id == "p-perf"); // nur ansehen/bearbeiten
        Assert.Equal("loud", Assert.Single(ctrl.Fans).Selected!.Id);       // Zuordnungen des Profils geladen
        Assert.Empty(activated);                                           // aber NICHT live umgeschaltet
        Assert.Equal("p-silent", ctrl.ActiveProfile!.Id);

        // Übernehmen, dann ausdrücklich aktivieren - der Schalter ist gesperrt, solange etwas offen ist.
        await ctrl.SaveCommand.ExecuteAsync(null);
        Assert.Equal("p-silent", sink.Saved[^1].ActiveProfileId);          // gespeichert wird das laufende Profil
        Assert.Equal(2, sink.Saved[^1].Profiles.Count);

        Assert.True(ctrl.CanActivateSelectedProfile);
        ctrl.SelectedProfileActive = true;
        Assert.Equal("p-perf", Assert.Single(activated));
        // Den Wechsel persistiert der Daemon selbst - er darf den „Nicht gespeichert"-Hinweis nicht zünden.
        Assert.False(ctrl.HasUnsavedChanges);

        await ctrl.SaveCommand.ExecuteAsync(null);
        Assert.Equal("p-perf", sink.Saved[^1].ActiveProfileId);
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

        Assert.Contains("nicht erreichbar", ctrl.Status.Text);
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

        Assert.True(ctrl.HasUnsavedChanges); // sofort dirty - ohne auf den nächsten Tick zu warten
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
    // bewusst NICHT in der Whitelist der On*RowChanged-/ConfigChanged-Handler - ein Tick darf den Editor NIE
    // als „ungespeichert" markieren (sonst kehrte die Pro-Tick-Serialisierung zurück).
    [Fact]
    public void UpdateLive_WithChangedLiveValuesAndStatus_DoesNotMarkDirty()
    {
        AppConfig config = ConfigWithProfile();
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));
        Assert.False(ctrl.HasUnsavedChanges);

        // Ein Tick mit ABWEICHENDEN Live-Werten (andere Temperaturen + Drehzahl) plus laufendem Kalibrier-
        // und Identify-Status - gleiche Config.
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
    // Dirty-Erkennung je Edit-Pfad - edit-getrieben (nicht pro Tick): sofort dirty, OHNE UpdateLive.
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
    // Live-Tick nachgezogen - Bedeutung/Speicherzeitpunkt bleiben gleich, nur der Vergleich läuft nicht je Sample.
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
    public async Task Edit_InNonActiveProfile_KeepsTheRunningCurvesUntouched()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.AddProfileCommand.Execute(null);       // zweites Profil, ausgewählt aber nicht aktiv
        ctrl.Curves.Single().Name = "Nur im Entwurf";

        await ctrl.SaveCommand.ExecuteAsync(null);

        AppConfig sent = Assert.Single(sink.Saved);
        Assert.Equal("p1", sent.ActiveProfileId);                       // das laufende Profil bleibt
        Assert.Equal("Quiet", Assert.Single(sent.Curves).Name);         // und regelt weiter mit seiner Kurve
        Profile draft = sent.Profiles.Single(p => p.Id != "p1");
        Assert.Equal("Nur im Entwurf", Assert.Single(draft.Curves).Name); // die Bearbeitung landet im Entwurf
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

    /// <summary>
    /// Der Sperr-Hinweis nennt nur den Grund, den der Schalter nicht selbst zeigt: offene Änderungen.
    /// Beim laufenden Profil ist er gesperrt, WEIL es aktiv ist - dort bliebe der Hinweis irreführend.
    /// </summary>
    [Fact]
    public void ShowActivationBlockedHint_OnlyForAnotherProfile_WhileDirty()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(TwoProfileConfig()));

        Assert.False(ctrl.HasUnsavedChanges);
        Assert.False(ctrl.ShowActivationBlockedHint);

        ctrl.Fans.Single().Name = "geändert"; // dirty, aber das gezeigte Profil ist das laufende
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.False(ctrl.ShowActivationBlockedHint);

        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2"); // gezeigt ≠ laufend → Grund anzeigen
        Assert.False(ctrl.CanActivateSelectedProfile);
        Assert.True(ctrl.ShowActivationBlockedHint);
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
    // Kalibrier-Ergebnis (MinPwm) aus dem Geräte-Tab - der Daemon ändert die Config hier ohne
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

    // „Verwerfen" darf das übernommene Kalibrier-Ergebnis nicht auf den Vor-Kalibrier-Wert zurückdrehen -
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

    // ---------------------------------------------------------------------------
    // Airflow-Ergebnis („schon durchgeführt")
    // ---------------------------------------------------------------------------

    /// <summary>Bereits getunter Stand, wie ihn Onboarding oder ein früheres „Übernehmen" hinterlässt.</summary>
    private static MonitorSnapshot TunedAirflowSnapshot()
    {
        MonitorSnapshot fresh = AirflowSnapshot();
        return fresh with { Config = AirflowTuneService.Apply(fresh.Config, AirflowTuneService.Analyze(fresh.Config)) };
    }

    [Fact]
    public void AirflowStatus_WithoutTuning_StaysEmpty()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshot());

        Assert.False(ctrl.HasAirflowStatus);
        Assert.Empty(ctrl.AirflowStatus);
        Assert.Equal("", ctrl.AirflowStatusPressureText);
    }

    // Der Kernfall: eine gespeicherte, bereits getunte Config zeigt das Ergebnis ohne neue Analyse.
    [Fact]
    public void AirflowStatus_FromStoredConfig_ListsFansAndPressure()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(TunedAirflowSnapshot());

        Assert.True(ctrl.HasAirflowStatus);
        Assert.Equal(3, ctrl.AirflowStatus.Count);
        Assert.Contains("ausgeglichen", ctrl.AirflowStatusPressureText); // 1 Einlass : 1 Auslass
        AirflowStatusRow cpu = ctrl.AirflowStatus.First(r => r.Fan.FanId == "cpu");
        Assert.Equal("airflow-cpu", cpu.Curve.Id);
        Assert.Equal("CPU", cpu.Fan.DisplayName);
    }

    [Fact]
    public void AirflowStatus_AfterApply_FollowsTheEditor()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshot());

        ctrl.AnalyzeAirflowCommand.Execute(null);
        ctrl.ApplyAirflowCommand.Execute(null);

        Assert.True(ctrl.HasAirflowStatus);
        Assert.Equal(3, ctrl.AirflowStatus.Count);
    }

    [Fact]
    public void AirflowStatus_ReassignedFan_DropsOut()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(TunedAirflowSnapshot());

        ctrl.Fans.First(f => f.FanId == "cpu").Selected = null; // zurück auf Hardware-Auto

        Assert.Equal(2, ctrl.AirflowStatus.Count);
        Assert.DoesNotContain(ctrl.AirflowStatus, r => r.Fan.FanId == "cpu");
    }

    // Ausgeblendete Lüfter sind für die Analyse „nicht vorhanden" - im Ergebnis ebenso wenig.
    [Fact]
    public void AirflowStatus_HiddenFan_IsExcluded()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(TunedAirflowSnapshot());

        ctrl.Fans.First(f => f.FanId == "front").Visible = false;

        Assert.DoesNotContain(ctrl.AirflowStatus, r => r.Fan.FanId == "front");
        Assert.Contains("Unterdruck", ctrl.AirflowStatusPressureText); // ohne Einlass-Lüfter kippt die Bilanz
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
        Assert.Contains("Gespeichert", ctrl.Status.Text);
    }

    // --- Status toast severity + unsaved toast lifecycle ---------------------------------------

    [Fact]
    public async Task Save_Failure_SetsErrorStatus_DismissClears()
    {
        var sink = new SaveSink { Result = false };
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.NotEqual("", ctrl.Status.Text);
        Assert.True(ctrl.Status.IsError);

        ctrl.Status.DismissCommand.Execute(null);
        Assert.Equal("", ctrl.Status.Text);
    }

    [Fact]
    public async Task Save_Success_SetsNonErrorStatus()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Gespeichert", ctrl.Status.Text);
        Assert.False(ctrl.Status.IsError);
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
    public void AddProfile_IsEmpty_OneDefaultCurve_NoAssignments_OpensProfileEditor()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.Fans.Single().Selected = ctrl.Curves.Single(); // vorher zuordnen

        int profilesBefore = ctrl.Profiles.Count;
        ctrl.AddProfileCommand.Execute(null);

        Assert.Equal(profilesBefore + 1, ctrl.Profiles.Count);
        Assert.Equal(CurveTabPane.Profile, ctrl.Pane);  // Profil-Editor offen (dort steht das Namensfeld)
        Assert.False(ctrl.SelectedProfileIsActive);     // ein neues Profil regelt nicht von selbst
        CurveEditRow curve = Assert.Single(ctrl.Curves); // genau eine Default-Kurve
        Assert.Equal("Neue Kurve", curve.Name);
        Assert.Equal(5, curve.Points.Count);            // Standard-Stützpunkte
        Assert.Null(ctrl.Fans.Single().Selected);       // leeres Profil → keine Zuordnung
    }

    [Fact]
    public void DuplicateProfile_CopiesCurrentState_WithKopieName_OpensProfileEditor()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        CurveEditRow curve = ctrl.Curves.Single();
        ctrl.Fans.Single().Selected = curve; // Zuordnung, die mitkopiert werden soll
        string activeName = ctrl.SelectedProfile!.Name;

        ctrl.DuplicateProfileCommand.Execute(null);

        Assert.Equal(CurveTabPane.Profile, ctrl.Pane);
        Assert.False(ctrl.SelectedProfileIsActive);     // die Kopie übernimmt nicht die Regelung
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
        Sensors = new[]
        {
            new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl", Group = "CPU" },
            new SensorConfig { SensorId = "hwmon7/temp1", Name = "Board", Group = "Gehäuse" },
        },
        Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "Fan" } },
    };

    [Fact]
    public void Initialize_PopulatesAvailableGroups_FromSensors_DistinctSorted()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(ConfigWithGroups()));

        // Distinct sensor groups, sorted; every sensor row shares the one controller instance.
        Assert.Equal(new[] { "CPU", "Gehäuse" }, ctrl.AvailableGroups);
        Assert.Same(ctrl.AvailableGroups, ctrl.Sensors[0].AvailableGroups);
    }

    [Fact]
    public void AvailableGroups_ExcludeEmpty_AndDedupeCaseInsensitively()
    {
        var config = new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon6/temp1", Name = "A", Group = "CPU" },
                new SensorConfig { SensorId = "hwmon7/temp1", Name = "B", Group = "cpu" }, // Dublette (Groß/klein)
                new SensorConfig { SensorId = "hwmon8/temp1", Name = "C", Group = "  " }, // leer → kein Vorschlag
            },
        };
        // Dedicated snapshot with three temperature readings - Snapshot() only yields two sensor rows.
        var snap = new MonitorSnapshot(
            "test",
            new[]
            {
                new SensorReading("hwmon6/temp1", "A", SensorKind.Temperature, "°C", 40),
                new SensorReading("hwmon7/temp1", "B", SensorKind.Temperature, "°C", 41),
                new SensorReading("hwmon8/temp1", "C", SensorKind.Temperature, "°C", 42),
            },
            Array.Empty<FanReading>(),
            config);

        var ctrl = new CurveEditorController();
        ctrl.Initialize(snap);

        Assert.Equal(new[] { "CPU" }, ctrl.AvailableGroups); // nur einmal, Whitespace raus
    }

    [Fact]
    public void EditingGroup_AddsNewName_ToAvailableGroups()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(ConfigWithGroups()));

        ctrl.Sensors[0].Group = "Gehäuse oben"; // neuer Name auf einer Sensor-Zeile

        Assert.Contains("Gehäuse oben", ctrl.AvailableGroups); // steht den anderen Zeilen sofort als Vorschlag bereit
    }

    [Fact]
    public void RefreshAvailableGroups_NeverRaisesReset()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(ConfigWithGroups())); // groups: CPU, Gehäuse

        // A Reset on the shared ItemsSource would rebuild every bound suggestion popup mid-edit
        // (the historical "clicking a suggestion creates a new group" bug) -> diff-only updates.
        var actions = new List<NotifyCollectionChangedAction>();
        ctrl.AvailableGroups.CollectionChanged += (_, e) => actions.Add(e.Action);

        ctrl.Sensors[0].Group = "Aggregat"; // rename CPU -> Aggregat
        ctrl.Sensors[1].Group = "";         // drop Gehäuse
        ctrl.Sensors[1].Group = "Zone";     // add a fresh name

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(new[] { "Aggregat", "Zone" }, ctrl.AvailableGroups);
    }

    [Fact]
    public void RefreshAvailableGroups_CasingOnlyRename_UpdatesSuggestion()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(ConfigWithGroups()));

        // The change gate is deliberately case-sensitive: a casing-only rename must reach the list.
        ctrl.Sensors[0].Group = "cpu";

        Assert.Contains("cpu", ctrl.AvailableGroups);
        Assert.DoesNotContain("CPU", ctrl.AvailableGroups);
    }

    [Fact]
    public async Task SelectingProfile_DoesNotActivate_AndLeavesEditorClean()
    {
        var sink = new SaveSink();
        var activated = new List<string>();
        var ctrl = new CurveEditorController(sink.SaveAsync, id => { activated.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.AddProfileCommand.Execute(null);          // zweites Profil, ausgewählt aber nicht aktiv
        ProfileRow added = ctrl.Profiles.Last();
        await ctrl.SaveCommand.ExecuteAsync(null);     // Ausgangslage: gespeichert
        activated.Clear();

        ctrl.SelectedProfile = ctrl.Profiles.First();  // hin …
        ctrl.SelectedProfile = added;                  // … und zurück

        Assert.Empty(activated);                       // die Auswahl allein schaltet nichts um
        Assert.False(ctrl.HasUnsavedChanges);          // und ändert die Konfiguration nicht
        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
    }

    [Fact]
    public void ActivateProfile_IsBlockedWhileUnsaved_AndDoesNotDirtyOnceApplied()
    {
        var activated = new List<string>();
        var ctrl = new CurveEditorController(activateProfile: id => { activated.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.AddProfileCommand.Execute(null);          // neues Profil = ungespeicherte Änderung

        Assert.True(ctrl.HasUnsavedChanges);
        Assert.False(ctrl.CanActivateSelectedProfile); // gesperrt, bis übernommen wurde
        ctrl.SelectedProfileActive = true;
        Assert.Empty(activated);                       // der abgelehnte Schalter schickt nichts
    }

    [Fact]
    public void DeletingActiveProfile_HandsTheFansToTheNextOne()
    {
        var activated = new List<string>();
        var ctrl = new CurveEditorController(activateProfile: id => { activated.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.AddProfileCommand.Execute(null);
        ProfileRow added = ctrl.Profiles.Last();
        ctrl.SelectedProfile = ctrl.Profiles.First(p => p.Id == "p1"); // das aktive auswählen
        activated.Clear();

        ctrl.DeleteProfileCommand.Execute(null);

        Assert.Same(added, ctrl.ActiveProfile);        // das verbliebene Profil regelt jetzt
        Assert.Equal(added.Id, Assert.Single(activated));
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

        // Abwahl entfernt die Zeile bewusst NICHT sofort (kommt aus der Checkbox dieser Liste) -
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

    // --- Hidden sensors in the curve source checkbox list (mirror of the fan section above) -------

    // Two sensors, "hwmon6/temp2" is globally hidden (optionally the curve's source).
    private static MonitorSnapshot HiddenSensorCurveSnapshot(bool hiddenIsSource = false) => new(
        "test",
        new[]
        {
            new SensorReading("hwmon6/temp1", "Tctl", SensorKind.Temperature, "°C", 41),
            new SensorReading("hwmon6/temp2", "Tccd1", SensorKind.Temperature, "°C", 39),
        },
        new[] { new FanReading("cpu", "CPU", 1500, 128, FanMode.Auto, true) },
        new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl" },
                new SensorConfig { SensorId = "hwmon6/temp2", Name = "Tccd1", Hidden = true },
            },
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "K",
                    SourceSensorIds = new[] { hiddenIsSource ? "hwmon6/temp2" : "hwmon6/temp1" },
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[] { new FanConfig { FanId = "cpu", Name = "CPU" } },
        });

    [Fact]
    public void SensorChecks_ExcludeGloballyHiddenSensors()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(HiddenSensorCurveSnapshot());

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        Assert.Equal("hwmon6/temp1", Assert.Single(curve.SensorChecks).Sensor.Id);
        Assert.Equal("hwmon6/temp1",
            Assert.Single(curve.DisplayedSensorGroups.SelectMany(g => g.Sensors)).Sensor.Id);
    }

    [Fact]
    public void SensorChecks_KeepHiddenSensor_WhileCurveSource()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(HiddenSensorCurveSnapshot(hiddenIsSource: true));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        SensorCheck hidden = curve.SensorChecks.Single(c => c.Sensor.Id == "hwmon6/temp2");
        Assert.True(hidden.Selected);

        // Unchecking does NOT remove the row immediately (the write comes from this very checkbox) -
        // the next rebuild (any visibility flip) filters it out.
        hidden.Selected = false;
        Assert.Contains(curve.SensorChecks, c => c.Sensor.Id == "hwmon6/temp2");

        SensorOption tctl = ctrl.Sensors.First(s => s.Id == "hwmon6/temp1");
        tctl.Visible = false;
        tctl.Visible = true;
        Assert.DoesNotContain(curve.SensorChecks, c => c.Sensor.Id == "hwmon6/temp2");
    }

    [Fact]
    public void ToggleSensorVisible_UpdatesSensorChecks_Live()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(HiddenSensorCurveSnapshot());

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        Assert.Single(curve.SensorChecks);

        SensorOption tccd = ctrl.Sensors.First(s => s.Id == "hwmon6/temp2");
        tccd.Visible = true; // eye toggle in the devices tab → immediately offered as source
        Assert.Equal(2, curve.SensorChecks.Count);

        tccd.Visible = false; // not a source → disappears again
        Assert.Equal("hwmon6/temp1", Assert.Single(curve.SensorChecks).Sensor.Id);
    }

    // --- Gruppierung im Kurven-Tab (Lüfter-Zuordnung + Quell-Sensoren), wie im Dashboard ---------

    // Drei Lüfter: zwei mit Position, einer ohne (→ „Ungruppiert").
    private static MonitorSnapshot MixedFanSnapshot()
    {
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl" } },
            Fans = new[]
            {
                new FanConfig { FanId = "front", Name = "Front", Location = FanLocation.CaseFrontIntake },
                new FanConfig { FanId = "loose", Name = "Loose" }, // Unspecified, keine Gruppe
                new FanConfig { FanId = "named", Name = "Named", Location = FanLocation.CaseRearExhaust },
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
    public void SelectedCurveFanGroups_GroupsByPosition_UngroupedLast()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(MixedFanSnapshot());
        ctrl.AddCurveCommand.Execute(null); // SelectedCurve gesetzt → Checkboxen + Gruppen aufgebaut

        // Position bestimmt den kurzen Gruppennamen; ohne Position „Ungruppiert" zuletzt.
        Assert.Equal(new[] { "Front · Einlass", "Hinten · Auslass", "Ungruppiert" },
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

        Assert.Equal(new[] { "Front · Einlass", "Hinten · Auslass", "Oben · Auslass" },
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

    [Fact]
    public void CurveSourceSensors_MergeGroupsCaseInsensitively()
    {
        var config = new AppConfig
        {
            Sensors = new[]
            {
                new SensorConfig { SensorId = "hwmon6/temp1", Name = "Tctl", Group = "CPU" },
                new SensorConfig { SensorId = "hwmon7/temp1", Name = "Board", Group = "cpu" },
            },
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "K", SourceSensorIds = new[] { "hwmon6/temp1" },
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
        };

        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));

        CurveEditRow curve = Assert.Single(ctrl.Curves);
        // Free-typed sensor groups can differ only in casing - they must land in ONE block.
        SensorCheckGroup group = Assert.Single(curve.DisplayedSensorGroups);
        Assert.Equal("CPU", group.Name);
        Assert.Equal(2, group.Sensors.Count);
    }
}
