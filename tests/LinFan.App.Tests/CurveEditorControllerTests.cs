// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Localization;
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
        public List<IReadOnlyDictionary<string, byte>> Bases { get; } = new();
        public bool Result { get; set; } = true;

        public Task<bool> SaveAsync(AppConfig config, IReadOnlyDictionary<string, byte> baseMinPwm)
        {
            Saved.Add(config);
            Bases.Add(baseMinPwm);
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
            new FanReading("hwmon7/pwm1", "thinkpad pwm1", 1900, 255, true),
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
        ctrl.IsConnected = true; // a running curve is switched in the daemon

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

    private static AppConfig RunningCurve() => new()
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

    [Fact]
    public void SetCurveEnabled_RunningCurve_Offline_IsRefused_AndTheSwitchSpringsBack()
    {
        var sink = new SaveSink();
        var toggles = new List<(string Id, bool Enabled)>();
        var ctrl = new CurveEditorController(sink.SaveAsync,
            setCurveEnabled: (id, enabled) => { toggles.Add((id, enabled)); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(RunningCurve()));
        ctrl.IsConnected = false;
        var notified = new List<string?>();
        ctrl.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        ctrl.SetCurveEnabled(ctrl.LiveCurves.Single(), false);
        ctrl.SelectedCurveEnabled = false; // the curves tab's switch takes the same path

        Assert.Empty(toggles);                                  // nothing sent that could not arrive …
        Assert.True(ctrl.LiveCurves.Single().Enabled);          // … and nothing pretends it did
        Assert.True(ctrl.Curves.Single().Enabled);
        Assert.True(ctrl.SelectedCurveEnabled);
        Assert.Contains(nameof(CurveEditorController.SelectedCurveEnabled), notified); // the switch is told to spring back

        Assert.False(ctrl.HasUnsavedChanges); // nothing unsaved either - no stale flag waiting for the next save
    }

    [Fact]
    public void Reconnect_RebuildsTheRunningCurves_FromTheNextSnapshot()
    {
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.IsConnected = true;
        ctrl.Initialize(Snapshot(RunningCurve()));
        int revision = ctrl.LiveCurvesRevision;
        ctrl.UpdateLive(Snapshot(RunningCurve()));
        Assert.Equal(revision, ctrl.LiveCurvesRevision); // same config: rows kept between ticks

        ctrl.IsConnected = false;
        ctrl.UpdateOffline();
        ctrl.IsConnected = true;
        ctrl.UpdateLive(Snapshot(RunningCurve()));

        Assert.Equal(revision + 1, ctrl.LiveCurvesRevision);
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
        ctrl.IsConnected = true; // activating needs the service

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

        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced);
        Assert.Single(ctrl.Curves);
        Assert.NotNull(ctrl.SelectedCurve);
        Assert.Equal(5, ctrl.SelectedCurve!.Points.Count); // the points of the "Balanced" template

        ctrl.DeleteCurveCommand.Execute(null);
        Assert.Empty(ctrl.Curves);
        Assert.Null(ctrl.SelectedCurve);
    }

    [Fact]
    public void DeleteCurve_ClearsAssignmentsReferencingIt()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot());
        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced);
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
            new[] { new FanReading("hwmon7/pwm1", "thinkpad pwm1", 2600, 255, true) },
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
        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced);
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
    // Löschen ohne Rückfrage: eine reine Entwurfsänderung (kein Auto-Save mehr), ein eigener Verlaufsschritt
    // mit „Rückgängig" im Toast; das laufende und das letzte Profil sind nicht löschbar.
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
        var unassigned = new[] { new ProfileAssignment("hwmon7/pwm1", null) };
        return new AppConfig
        {
            Curves = new[] { curveA },
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1", MinPwm = 40, MaxPwm = 255 } },
            Profiles = new[]
            {
                new Profile { Id = "p1", Name = "Eins", Curves = new[] { curveA }, Assignments = unassigned },
                new Profile { Id = "p2", Name = "Zwei", Curves = new[] { curveB }, Assignments = unassigned },
            },
            ActiveProfileId = "p1",
        };
    }

    [Fact]
    public void DeleteCurve_IsADraftChange_NotSaved_AndUndoBringsItBack()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.DeleteCurveCommand.Execute(null);

        Assert.Empty(ctrl.Curves);
        Assert.Empty(sink.Saved);            // no auto-save any more - it waits for „Übernehmen"
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.Equal(CurveTabPane.Profile, ctrl.Pane); // nothing left to edit → the profile
        Assert.Equal("Kurve „Quiet“ gelöscht.", ctrl.Status.Text);
        Assert.Same(ctrl.UndoCommand, ctrl.Status.ActionCommand); // the toast's „Rückgängig" is the history undo
        Assert.Equal("Rückgängig", ctrl.Status.ActionLabel);

        ctrl.Status.ActionCommand!.Execute(null);

        CurveEditRow back = Assert.Single(ctrl.Curves);
        Assert.Equal("c1", back.Id);
        Assert.Same(back, ctrl.SelectedCurve); // the selection where the delete happened comes back too
        Assert.Equal(CurveTabPane.Curve, ctrl.Pane);
        Assert.False(ctrl.HasUnsavedChanges);
        Assert.False(ctrl.Status.HasAction);
        Assert.Equal("Rückgängig gemacht.", ctrl.Status.Text);
    }

    [Fact]
    public void DeleteCurve_WhenDirty_KeepsTheOtherEdit_AndIsItsOwnStep()
    {
        var clock = new ManualClock();
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync, clock: clock);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.Fans.Single().Name = "vorher geändert";

        ctrl.DeleteCurveCommand.Execute(null); // straight after the edit - still a step of its own

        Assert.Empty(ctrl.Curves);
        ctrl.UndoCommand.Execute(null);
        Assert.Single(ctrl.Curves);                                // delete undone …
        Assert.Equal("vorher geändert", ctrl.Fans.Single().Name); // … the edit before it is not
        ctrl.UndoCommand.Execute(null);
        Assert.Equal("thinkpad pwm1", ctrl.Fans.Single().Name);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void DeleteToast_IsDropped_ByTheNextEdit()
    {
        // Its „Rückgängig" is the history undo - after another edit it would undo that edit, not the delete.
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync, clock: new ManualClock());
        ctrl.Initialize(Snapshot(TwoProfileConfig()));
        ctrl.DeleteCurveCommand.Execute(null);
        Assert.True(ctrl.Status.HasAction);

        ctrl.Fans.Single().Name = "danach geändert";

        Assert.False(ctrl.Status.HasAction);
        Assert.Equal("", ctrl.Status.Text);
    }

    [Fact]
    public void DeleteToast_IsDropped_ByTheNextOwnStep()
    {
        // Duplicating is a step of its own that bypasses MarkDirty - the toast's undo would take it back instead.
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync, clock: new ManualClock());
        ctrl.Initialize(Snapshot(TwoProfileConfig()));
        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced);
        ctrl.DeleteCurveCommand.Execute(null);
        Assert.True(ctrl.Status.HasAction);

        ctrl.DuplicateCurveCommand.Execute(null);

        Assert.False(ctrl.Status.HasAction);
        Assert.Equal("", ctrl.Status.Text);
    }

    // --- Profiles whose assignments do not list every fan (fan added later, hand edit, another tool) ---------

    private static AppConfig ProfileWith(params ProfileAssignment[] p2Assignments) => TwoProfileConfig() with
    {
        Fans = new[]
        {
            new FanConfig { FanId = "hwmon7/pwm1", Name = "thinkpad pwm1" },
            new FanConfig { FanId = "hwmon7/pwm2", Name = "back" },
        },
        Profiles = new[]
        {
            TwoProfileConfig().Profiles[0],
            TwoProfileConfig().Profiles[1] with { Assignments = p2Assignments },
        },
    };

    private static MonitorSnapshot TwoFanSnapshot(AppConfig config) => Snapshot(config) with
    {
        Fans = new[]
        {
            new FanReading("hwmon7/pwm1", "thinkpad pwm1", 1900, 255, true),
            new FanReading("hwmon7/pwm2", "back", 900, 120, true),
        },
    };

    [Theory]
    [InlineData(false)] // no entry at all
    [InlineData(true)]  // only the second fan, so the order differs as well
    public void OpeningAProfile_ThatDoesNotListEveryFan_IsNoUnsavedChange(bool listsTheSecondFan)
    {
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.Initialize(TwoFanSnapshot(ProfileWith(
            listsTheSecondFan ? new[] { new ProfileAssignment("hwmon7/pwm2", "cb") } : Array.Empty<ProfileAssignment>())));

        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");

        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Null(ctrl.Fans[0].Selected); // a fan the profile does not list: no curve, as the editor shows it
        Assert.Equal(listsTheSecondFan ? "cb" : null, ctrl.Fans[1].Selected?.Id);
    }

    [Fact]
    public async Task Save_KeepsTheAssignmentOfAFanThatIsNotDetected()
    {
        // A fan of a controller that is unplugged right now: no row in the editor, but its curve belongs to the profile.
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(TwoFanSnapshot(ProfileWith(
            new ProfileAssignment("usb/fan1", "cb"), new ProfileAssignment("hwmon7/pwm1", "cb"))));
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");
        Assert.False(ctrl.HasUnsavedChanges);

        ctrl.Fans[1].Selected = ctrl.Curves.Single(); // an edit in the profile, then save
        await ctrl.SaveCommand.ExecuteAsync(null);

        Profile p2 = Assert.Single(sink.Saved).Profiles.Single(p => p.Id == "p2");
        Assert.Equal(
            new (string, string?)[] { ("hwmon7/pwm1", "cb"), ("hwmon7/pwm2", "cb"), ("usb/fan1", "cb") },
            p2.Assignments.Select(a => (a.FanId, a.CurveId)).ToArray());
    }

    [Fact]
    public async Task AProfileLeftForAnother_KeepsTheAssignmentOfAFanThatIsNotDetected()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(TwoFanSnapshot(ProfileWith(new ProfileAssignment("usb/fan1", "cb"))));
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p1"); // p2's state goes back into its row

        ctrl.Fans[0].Name = "Umbenannt";
        await ctrl.SaveCommand.ExecuteAsync(null);

        Profile p2 = Assert.Single(sink.Saved).Profiles.Single(p => p.Id == "p2");
        Assert.Contains(p2.Assignments, a => a.FanId == "usb/fan1" && a.CurveId == "cb");
    }

    [Fact]
    public void AProfileListingAFanTwice_TakesTheFirstEntry_AndOpensClean()
    {
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.Initialize(TwoFanSnapshot(ProfileWith(
            new ProfileAssignment("hwmon7/pwm1", "cb"), new ProfileAssignment("hwmon7/pwm1", null))));

        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");

        Assert.Equal("cb", ctrl.Fans[0].Selected?.Id); // first entry per fan, as in the daemon
        Assert.False(ctrl.HasUnsavedChanges);
    }

    // A fan a profile does not list has no curve in it - what the editor always showed. The next save writes that down for
    // every profile, opened or not, so the daemon no longer keeps the fan on whatever curve it had when switching there.
    [Fact]
    public async Task Save_WritesAFanAProfileDoesNotListAsUnassigned_AlsoInAProfileNeverOpened()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(TwoFanSnapshot(ProfileWith()));

        ctrl.Fans[0].Name = "Umbenannt"; // an edit in p1, the running profile
        await ctrl.SaveCommand.ExecuteAsync(null);

        Profile p2 = Assert.Single(sink.Saved).Profiles.Single(p => p.Id == "p2");
        Assert.Equal(
            new (string, string?)[] { ("hwmon7/pwm1", null), ("hwmon7/pwm2", null) },
            p2.Assignments.Select(a => (a.FanId, a.CurveId)).ToArray());
    }

    [Fact]
    public void DeleteProfile_IsADraftChange_AndUndoBringsItBack()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(TwoProfileConfig()));
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");
        Assert.True(ctrl.DeleteProfileCommand.CanExecute(null));

        ctrl.DeleteProfileCommand.Execute(null);

        ProfileRow left = Assert.Single(ctrl.Profiles);
        Assert.Equal("p1", left.Id);
        Assert.Same(left, ctrl.SelectedProfile); // the editor falls back to the running profile
        Assert.Same(left, ctrl.ActiveProfile);
        Assert.Empty(sink.Saved);
        Assert.True(ctrl.HasUnsavedChanges);
        Assert.Equal("Profil „Zwei“ gelöscht.", ctrl.Status.Text);

        ctrl.UndoCommand.Execute(null);

        Assert.Equal(new[] { "p1", "p2" }, ctrl.Profiles.Select(p => p.Id));
        Assert.Equal("p2", ctrl.SelectedProfile!.Id);
        Assert.Equal("cb", Assert.Single(ctrl.Curves).Id);
        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public void DeleteProfile_RunningOrLastProfile_CannotBeDeleted()
    {
        var activated = new List<string>();
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync, id => { activated.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(TwoProfileConfig()));
        ctrl.IsConnected = true; // activating needs the service
        int notified = 0;
        ctrl.DeleteProfileCommand.CanExecuteChanged += (_, _) => notified++;

        Assert.Equal("p1", ctrl.SelectedProfile!.Id); // shown = running
        Assert.False(ctrl.DeleteProfileCommand.CanExecute(null));
        ctrl.DeleteProfileCommand.Execute(null);      // a direct call is refused as well
        Assert.Equal(2, ctrl.Profiles.Count);

        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p2");
        Assert.True(notified > 0); // the view re-queries the button
        Assert.True(ctrl.DeleteProfileCommand.CanExecute(null));

        notified = 0;
        ctrl.SelectedProfileActive = true; // p2 runs now → it is the one that cannot go
        Assert.Equal("p2", Assert.Single(activated));
        Assert.True(notified > 0);
        Assert.False(ctrl.DeleteProfileCommand.CanExecute(null));

        ctrl.UpdateLive(Snapshot(ProfileService.Apply(TwoProfileConfig(), "p2"))); // the daemon confirms the switch
        ctrl.SelectedProfile = ctrl.Profiles.Single(p => p.Id == "p1");
        ctrl.DeleteProfileCommand.Execute(null); // p1 is neither running nor the last
        Assert.Single(ctrl.Profiles);
        Assert.False(ctrl.DeleteProfileCommand.CanExecute(null)); // the last one stays
    }

    [Fact]
    public void DeleteCurve_CanExecute_FollowsTheSelection()
    {
        var ctrl = new CurveEditorController(new SaveSink().SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        Assert.True(ctrl.DeleteCurveCommand.CanExecute(null));

        ctrl.DeleteCurveCommand.Execute(null);

        Assert.Null(ctrl.SelectedCurve);
        Assert.False(ctrl.DeleteCurveCommand.CanExecute(null));
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
        Assert.Equal(96, Assert.Single(sink.Bases)["hwmon7/pwm1"]);
    }

    // A start point the user left alone goes out as its own base - the daemon then keeps its value, also one a calibration
    // set before the result reached the editor (ConfigMapperTests.Merge_MinPwmTheSenderLeftAlone_KeepsTheCalibratedStartPoint).
    [Fact]
    public async Task Save_WithAnUntouchedMinPwm_SendsItAsItsBase()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig())); // MinPwm 40
        ctrl.Fans.Single().Name = "Umbenannt";

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Equal(40, Assert.Single(sink.Saved).Fans.Single().MinPwm);
        Assert.Equal(40, Assert.Single(sink.Bases)["hwmon7/pwm1"]);
    }

    [Fact]
    public async Task Save_WithAnEditedMinPwm_SendsTheDaemonsValueAsItsBase()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig())); // MinPwm 40
        ctrl.Fans.Single().MinPwm = 77;

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Equal(77, Assert.Single(sink.Saved).Fans.Single().MinPwm);
        Assert.Equal(40, Assert.Single(sink.Bases)["hwmon7/pwm1"]); // changed by the user: the daemon takes 77
    }

    // The base is what the daemon reported, not the baseline the editor moves on each save: the daemon keeps only the last of
    // two saves within one tick, and with the baseline as base that one would report 77 as untouched and the daemon would
    // stay on 40 while the editor showed 77 as saved.
    [Fact]
    public async Task TwoSaves_BeforeTheDaemonReports_BothSendTheReportedStartPointAsBase()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(CurveAndFanConfig())); // MinPwm 40
        FanAssignRow fan = ctrl.Fans.Single();

        fan.MinPwm = 77;
        await ctrl.SaveCommand.ExecuteAsync(null);
        fan.Name = "Umbenannt";
        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new byte[] { 77, 77 }, sink.Saved.Select(c => c.Fans.Single().MinPwm));
        Assert.Equal(new byte[] { 40, 40 }, sink.Bases.Select(b => b["hwmon7/pwm1"]));
    }

    // A calibrated start point reported while a save is on its way is taken over after it: adopted in between, the save's
    // baseline would replace it again - the editor would show the old value as an unsaved change, and discarding it would
    // leave the row below the daemon's start point for good.
    [Fact]
    public async Task ACalibrationReportedDuringASave_IsAdoptedAfterIt()
    {
        AppConfig config = CurveAndFanConfig(); // MinPwm 40
        var pending = new TaskCompletionSource<bool>();
        var ctrl = new CurveEditorController((_, _) => pending.Task);
        ctrl.Initialize(Snapshot(config));
        FanAssignRow fan = ctrl.Fans.Single();
        fan.Name = "Umbenannt";

        Task save = ctrl.SaveCommand.ExecuteAsync(null);
        ctrl.UpdateLive(Snapshot(WithMinPwm(config, "hwmon7/pwm1", 96)));
        Assert.Equal(40, fan.MinPwm); // not yet - the save is on its way
        pending.SetResult(true);
        await save;

        ctrl.UpdateLive(Snapshot(WithMinPwm(config, "hwmon7/pwm1", 96)));

        Assert.Equal(96, fan.MinPwm);
        Assert.Equal(96, fan.DaemonMinPwm);
        Assert.False(ctrl.HasUnsavedChanges);
    }

    // --- Names: „Lüfter N" for a fan without one, no own name for a sensor named like its hardware ------------------

    private static MonitorSnapshot WithSensorLabels(MonitorSnapshot snapshot) => snapshot with
    {
        Sensors = snapshot.Sensors.Select(s => s with { Label = s.Name }).ToList(),
    };

    [Fact]
    public async Task Save_FillsAnEmptyFanName_WithItsPlaceholder()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        AppConfig config = CurveAndFanConfig();
        ctrl.Initialize(Snapshot(config with { Fans = config.Fans.Select(f => f with { Name = "" }).ToList() }));
        FanAssignRow fan = ctrl.Fans.Single();
        Assert.Equal(("", "Lüfter 1"), (fan.Name, fan.DefaultName));
        Assert.False(ctrl.HasUnsavedChanges); // no own name is no change

        ctrl.Sensors[0].Visible = false; // any edit, then save
        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Lüfter 1", Assert.Single(sink.Saved).Fans.Single().Name);
        Assert.Equal("Lüfter 1", fan.Name); // shown as its value from now on, as after the setup assistant
        Assert.False(ctrl.HasUnsavedChanges);
    }

    [Fact]
    public async Task Save_SendsASensorNamedLikeItsHardware_WithoutAnOwnName()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(WithSensorLabels(Snapshot(CurveAndFanConfig())));
        ctrl.Sensors.Single(s => s.Id == "hwmon7/temp1").Name = "Gehäuse";

        await ctrl.SaveCommand.ExecuteAsync(null);

        Dictionary<string, string> names = Assert.Single(sink.Saved).Sensors.ToDictionary(s => s.SensorId, s => s.Name);
        Assert.Equal("", names["hwmon6/temp1"]);       // the hardware label follows the hardware
        Assert.Equal("Gehäuse", names["hwmon7/temp1"]); // an own name stays
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
    // Settings › Airflow: proposal per target profile
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
                new FanReading("cpu", "CPU", 1500, 128, true),
                new FanReading("front", "Front", 1000, 128, true),
                new FanReading("rear", "Rear", 1200, 128, true),
            },
            config);
    }

    // The same fans with a second profile "quiet" that is not running and has its own curve on every fan.
    private static MonitorSnapshot AirflowSnapshotWithQuiet()
    {
        MonitorSnapshot snapshot = AirflowSnapshot();
        var manual = new CurveConfig
        {
            Id = "manual",
            Name = "Handarbeit",
            SourceSensorId = "hwmon6/temp1",
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var quiet = new Profile
        {
            Id = "quiet",
            Name = "Leise",
            Curves = new[] { manual },
            Assignments = new[] { new ProfileAssignment("cpu", "manual"), new ProfileAssignment("front", "manual"), new ProfileAssignment("rear", "manual") },
        };
        return snapshot with { Config = snapshot.Config with { Profiles = [.. snapshot.Config.Profiles, quiet] } };
    }

    [Fact]
    public void Airflow_ProposesForTheRunningProfile_OnlyWhatChanges()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshot());

        Assert.Equal("default", ctrl.AirflowTarget!.Id);
        Assert.True(ctrl.AirflowTargetIsRunning);
        Assert.Equal(new[] { ("cpu", "airflow-cpu"), ("front", "airflow-intake"), ("rear", "airflow-exhaust") },
            ctrl.AirflowChanges.Select(r => (r.Change.FanId, r.Change.ProposedCurveId!)));
        Assert.All(ctrl.AirflowChanges, r => Assert.True(r.IsNewCurve && r.Apply));
        Assert.Equal("3 Änderungen", ctrl.AirflowChangesText);
        Assert.True(ctrl.AirflowBalance.HasCaseFans);
        Assert.Equal(9, ctrl.AirflowZones.Count);
        Assert.Equal(5, ctrl.AirflowRoles.Count);
    }

    // Sensors saved without own names are matched by their hardware labels: the SoC in group "CPU" stays out of the CPU curve.
    [Fact]
    public async Task ApplyAirflow_MatchesSensorsWithoutOwnNamesByTheirLabels()
    {
        const string tctl = "/amdcpu/0/temperature/2", soc = "/amdcpu/0/temperature/3";
        MonitorSnapshot snapshot = AirflowSnapshot();
        snapshot = snapshot with
        {
            Sensors =
            [
                new SensorReading(tctl, "AMD Ryzen 7 5800X Core (Tctl/Tdie)", SensorKind.Temperature, "°C", 70, "AMD Ryzen 7 5800X Core (Tctl/Tdie)"),
                new SensorReading(soc, "AMD Ryzen 7 5800X SoC", SensorKind.Temperature, "°C", 45, "AMD Ryzen 7 5800X SoC"),
            ],
            Config = snapshot.Config with
            {
                Sensors =
                [
                    new SensorConfig { SensorId = tctl, Name = "", Group = "CPU" },
                    new SensorConfig { SensorId = soc, Name = "", Group = "CPU" },
                ],
            },
        };
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(snapshot);

        ctrl.ApplyAirflowToTargetCommand.Execute(null);
        await ctrl.SaveCommand.ExecuteAsync(null);

        CurveConfig cpu = Assert.Single(sink.Saved).Curves.Single(c => c.Id == "airflow-cpu");
        Assert.Equal(new[] { tctl }, cpu.SourceSensorIds);
    }

    [Fact]
    public async Task ApplyAirflow_IntoTheShownProfile_GoesThroughTheEditor_AndIsSaved()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(AirflowSnapshot());

        ctrl.ApplyAirflowToTargetCommand.Execute(null);

        Assert.True(ctrl.HasUnsavedChanges);
        Assert.Equal("airflow-intake", ctrl.Fans.Single(f => f.FanId == "front").Selected?.Id);
        Assert.Empty(ctrl.AirflowChanges); // the profile follows the proposal now
        Assert.Equal("„Standard“ folgt bereits dem Vorschlag.", ctrl.AirflowFollowsText);

        await ctrl.SaveCommand.ExecuteAsync(null);
        AppConfig saved = Assert.Single(sink.Saved);
        Assert.Equal("airflow-exhaust", saved.Fans.Single(f => f.FanId == "rear").AssignedCurveId);
        Assert.Contains(saved.Curves, c => c.Id == "airflow-cpu");
    }

    /// <summary>
    /// Regression: applying used to take the running profile's curves as the base and load them into the profile shown
    /// in the editor. Now the proposal goes into its own target, and every other profile stays as it was.
    /// </summary>
    [Fact]
    public void ApplyAirflow_IntoAProfileThatIsNotRunning_LeavesTheRunningOneAlone()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshotWithQuiet());
        ProfileRow quiet = ctrl.Profiles.Single(p => p.Id == "quiet");
        ctrl.AirflowTarget = quiet;
        Assert.False(ctrl.AirflowTargetIsRunning);
        Assert.Equal("Handarbeit", ctrl.AirflowChanges[0].FromText);

        ctrl.AirflowChanges.Single(r => r.Change.FanId == "rear").Apply = false; // stays on its own curve
        ctrl.ApplyAirflowToTargetCommand.Execute(null);

        Assert.Equal(new[] { ("cpu", (string?)"airflow-cpu"), ("front", "airflow-intake"), ("rear", "manual") },
            quiet.Assignments.Select(a => (a.FanId, a.CurveId)));
        Assert.Equal(new[] { "manual", "airflow-cpu", "airflow-intake" }, quiet.Curves.Select(c => c.Id));
        Assert.Empty(ctrl.Curves); // the running profile shown in the editor is untouched
        Assert.All(ctrl.Fans, f => Assert.Null(f.Selected));
        Assert.True(ctrl.HasUnsavedChanges);

        // Undo rebuilds the profile rows - the page stays on the same target.
        ctrl.UndoCommand.Execute(null);
        Assert.Equal("quiet", ctrl.AirflowTarget?.Id);
        Assert.Equal("Handarbeit", ctrl.AirflowChanges[0].FromText);
    }

    [Fact]
    public void CreateAirflowProfile_CopiesTheTargetWithTheChanges_AndNumbersTheName()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshotWithQuiet());
        ProfileRow quiet = ctrl.Profiles.Single(p => p.Id == "quiet");
        ctrl.AirflowTarget = quiet;

        ctrl.CreateAirflowProfileCommand.Execute(null);

        ProfileRow created = ctrl.Profiles.Last();
        Assert.Equal("Leise · Airflow", created.Name);
        Assert.Same(created, ctrl.AirflowTarget);
        Assert.DoesNotContain(created.Curves, c => c.Id == "manual"); // lost all its fans
        Assert.Equal("manual", quiet.Assignments.First().CurveId);    // the original stays

        ctrl.AirflowTarget = quiet;
        ctrl.CreateAirflowProfileCommand.Execute(null);
        Assert.Equal("Leise · Airflow 2", ctrl.Profiles.Last().Name);
    }

    [Fact]
    public void Airflow_WithNothingTicked_OrNoRoomForAProfile_LocksTheButtons()
    {
        MonitorSnapshot snapshot = AirflowSnapshot();
        var many = Enumerable.Range(0, ConfigLimits.MaxProfiles - 1)
            .Select(i => new Profile { Id = $"p{i}", Name = $"P{i}" })
            .ToList();
        var ctrl = new CurveEditorController();
        ctrl.Initialize(snapshot with { Config = snapshot.Config with { Profiles = [.. snapshot.Config.Profiles, .. many] } });

        Assert.True(ctrl.ApplyAirflowToTargetCommand.CanExecute(null));
        Assert.False(ctrl.CreateAirflowProfileCommand.CanExecute(null));
        Assert.Contains("32", ctrl.AirflowCreateTooltip);

        foreach (AirflowChangeRow row in ctrl.AirflowChanges)
            row.Apply = false;
        Assert.False(ctrl.ApplyAirflowToTargetCommand.CanExecute(null));
        Assert.Equal("Nichts angehakt.", ctrl.AirflowApplyTooltip);
        Assert.Equal("3 Änderungen · 0 gewählt", ctrl.AirflowChangesText);
    }

    [Fact]
    public void Airflow_FollowsAPositionChange_AndOffersToSetMissingPositions()
    {
        MonitorSnapshot snapshot = AirflowSnapshot();
        var ctrl = new CurveEditorController();
        ctrl.Initialize(snapshot with
        {
            Config = snapshot.Config with
            {
                Fans = snapshot.Config.Fans.Select(f => f.FanId == "rear" ? f with { Location = FanLocation.Unspecified } : f).ToList(),
            },
        });
        FanAssignRow? asked = null;
        ctrl.LocationDialogRequested += (_, row) => asked = row;

        AirflowHintRow noPosition = ctrl.AirflowHints.First(h => h.Text.Contains("„Rear“"));
        noPosition.Action!.Execute(null);
        Assert.Equal("rear", asked?.FanId);
        Assert.Contains("Nur Einlass", ctrl.AirflowBalance.VerdictText);

        ctrl.SetFanLocations(new[] { "rear" }, FanLocation.CaseRearExhaust);

        Assert.DoesNotContain(ctrl.AirflowHints, h => h.Text.Contains("„Rear“"));
        Assert.Equal("airflow-exhaust", ctrl.AirflowChanges.Single(r => r.Change.FanId == "rear").Change.ProposedCurveId);
    }

    [Fact]
    public void Airflow_FollowsCurveAndProfileEdits()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshot());
        ctrl.ApplyAirflowToTargetCommand.Execute(null); // the running profile now follows the proposal
        Assert.Empty(ctrl.AirflowChanges);
        Assert.All(ctrl.AirflowRoles.Where(r => !r.IsUnused), r => Assert.True(r.InProfile));

        // Deleting a role curve: the fan is back on the list, and the curve would come fresh from the template.
        ctrl.SelectedCurve = ctrl.Curves.Single(c => c.Id == "airflow-exhaust");
        ctrl.DeleteCurveCommand.Execute(null);
        AirflowChangeRow rear = ctrl.AirflowChanges.Single();
        Assert.Equal("rear", rear.Change.FanId);
        Assert.True(rear.IsNewCurve);
        Assert.False(ctrl.AirflowRoles.Single(r => r.CurveName == rear.ToText).InProfile);

        // Renaming the target profile shows in its menu entry and the apply button.
        ctrl.AirflowTarget!.Name = "Alltag";
        Assert.Equal("In „Alltag“ übernehmen", ctrl.AirflowApplyLabel);
        Assert.Contains(ctrl.AirflowTargetMenu, m => m.Header == "Alltag");

        // A duplicated profile can be a target right away.
        ctrl.DuplicateProfileCommand.Execute(null);
        Assert.Equal(ctrl.Profiles.Count, ctrl.AirflowTargetMenu.Count);
    }

    /// <summary>
    /// In an editor that is unsaved already, an edit that continues a burst (typing, a drag) defers the comparison and
    /// the airflow page with it - to the next tick, offline as well: the GUI ticks every second without a connection.
    /// </summary>
    [Fact]
    public void Airflow_FollowsABurstEdit_WithTheNextTick_EvenOffline()
    {
        var ctrl = new CurveEditorController(clock: new ManualClock()); // time stands still: one burst for sure
        ctrl.Initialize(AirflowSnapshot());
        ctrl.ApplyAirflowToTargetCommand.Execute(null);
        Assert.True(ctrl.HasUnsavedChanges);
        CurveEditRow cpu = ctrl.Curves.Single(c => c.Id == AirflowTuneService.CpuCurveId);

        cpu.Name = "CPU-Kurve";   // opens a history step: compared at once
        cpu.Name = "CPU-Kurve 2"; // the same burst: deferred
        Assert.DoesNotContain(ctrl.AirflowRoles, r => r.CurveName == "CPU-Kurve 2");

        ctrl.IsConnected = false;
        ctrl.UpdateOffline();

        Assert.Contains(ctrl.AirflowRoles, r => r.CurveName == "CPU-Kurve 2");
    }

    [Fact]
    public void LocationPick_ShowsTheOtherVisibleFans_ByTheirShownName()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshot());
        FanAssignRow cpu = ctrl.Fans.Single(f => f.FanId == "cpu");
        ctrl.Fans.Single(f => f.FanId == "rear").Visible = false;
        ctrl.Fans.Single(f => f.FanId == "front").Name = "Vorne";

        LocationPickController pick = ctrl.CreateLocationPick(cpu);

        IEnumerable<CaseFan> others = pick.Zones.Concat(pick.Tray).SelectMany(z => z.Fans).Where(f => !f.IsSelf);
        Assert.Equal(["Vorne"], others.Select(f => f.Name));
        Assert.Same(cpu.Manual, pick.Manual);
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

        ctrl.ApplyAirflowToTargetCommand.Execute(null); // setzt einen Auto-Hide-Status (CTS #1)
        await Task.Delay(150);                          // Auto-Hide läuft ab → CTS #1 wird entsorgt

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
    public async Task Save_OfAProfileThatIsNotRunning_SaysItWaitsForTheSwitch()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(AirflowSnapshotWithQuiet());
        ctrl.AirflowTarget = ctrl.Profiles.Single(p => p.Id == "quiet");
        ctrl.ApplyAirflowToTargetCommand.Execute(null);

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Gespeichert - „Leise“ läuft gerade nicht; die Änderungen wirken nach dem Wechsel auf dieses Profil.",
            ctrl.Status.Text);

        // The running profile: the changes apply now.
        ctrl.AirflowTarget = ctrl.ActiveProfile;
        ctrl.ApplyAirflowToTargetCommand.Execute(null);
        await ctrl.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Gespeichert - Änderungen werden übernommen", ctrl.Status.Text);
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

    // --- Sensor group list (AppConfig.SensorGroups) ------------------------------------------------

    private static AppConfig ConfigWithGroupList() => ConfigWithGroups() with { SensorGroups = new[] { "Leer", "CPU", "Gehäuse" } };

    [Fact]
    public async Task Save_KeepsTheSensorGroups()
    {
        // BuildConfig assembles a fresh AppConfig; without carrying the list, every save from the editor would send
        // an empty one - and the daemon takes an empty list as "the user deleted all groups".
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(ConfigWithGroupList()));
        ctrl.Fans[0].Name = "Edited";

        await ctrl.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Leer", "CPU", "Gehäuse" }, Assert.Single(sink.Saved).SensorGroups);
    }

    [Fact]
    public async Task Revert_RestoresTheSensorGroups_AndTheNextSaveStillSendsThem()
    {
        var sink = new SaveSink();
        var ctrl = new CurveEditorController(sink.SaveAsync);
        ctrl.Initialize(Snapshot(ConfigWithGroupList()));
        Assert.False(ctrl.HasUnsavedChanges); // the list is part of the baseline, not a change
        ctrl.Sensors[0].Group = "Gehäuse";

        ctrl.RevertCommand.Execute(null);

        Assert.False(ctrl.HasUnsavedChanges);
        Assert.Equal(new[] { "Leer", "CPU", "Gehäuse" }, ctrl.SensorGroups);
        await ctrl.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Leer", "CPU", "Gehäuse" }, Assert.Single(sink.Saved).SensorGroups);
    }

    [Fact]
    public void Resync_TakesTheSensorGroupsOfTheNewConfig()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(ConfigWithGroupList()));

        ctrl.Resync(Snapshot(ConfigWithGroups() with { SensorGroups = new[] { "Import" } }));

        Assert.Equal(new[] { "Import" }, ctrl.SensorGroups);
    }

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
    public void ActivateProfile_WhileUnsaved_AsksInsteadOfSwitching()
    {
        var activated = new List<string>();
        var ctrl = new CurveEditorController(activateProfile: id => { activated.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.IsConnected = true; // activating needs the service
        ctrl.AddProfileCommand.Execute(null);          // a new profile = an unsaved change
        var asked = new List<ProfileSwitchRequest>();
        ctrl.ProfileSwitchConfirmationRequested += (_, r) => asked.Add(r);
        var notified = new List<string?>();
        ctrl.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Assert.True(ctrl.HasUnsavedChanges);
        Assert.True(ctrl.CanActivateSelectedProfile);  // not locked any more - the switch asks first
        ctrl.SelectedProfileActive = true;

        Assert.Equal(ctrl.SelectedProfile!.Id, Assert.Single(asked).ProfileId);
        Assert.Empty(activated);                       // nothing reaches the service before the answer
        Assert.False(ctrl.SelectedProfileActive);      // the switch snaps back …
        Assert.Contains(nameof(CurveEditorController.SelectedProfileActive), notified); // … and the view hears of it
    }

    [Fact]
    public void DeletingTheRunningProfile_IsRefused_NothingIsActivated()
    {
        // It used to hand the fans to the next profile live - a switch no undo could take back.
        var activated = new List<string>();
        var ctrl = new CurveEditorController(activateProfile: id => { activated.Add(id); return Task.CompletedTask; });
        ctrl.Initialize(Snapshot(CurveAndFanConfig()));
        ctrl.AddProfileCommand.Execute(null);
        ctrl.SelectedProfile = ctrl.Profiles.First(p => p.Id == "p1"); // das aktive auswählen

        ctrl.DeleteProfileCommand.Execute(null);

        Assert.Equal(2, ctrl.Profiles.Count);
        Assert.Equal("p1", ctrl.ActiveProfile!.Id);
        Assert.Empty(activated);
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
            new FanReading("cpu", "CPU", 1500, 128, true),
            new FanReading("front", "Front", 1000, 128, true),
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
    public void ShowHiddenSensorsOff_ExcludesInvisible()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        ctrl.ShowHiddenSensors = false;

        Assert.Equal(2, ctrl.FilteredSensors.Count);
        Assert.DoesNotContain(ctrl.FilteredSensors, s => s.Id == "hwmon9/temp1");
    }

    [Fact]
    public void TogglingVisibility_WhileHidingHidden_UpdatesFilteredSensors()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());
        ctrl.ShowHiddenSensors = false;
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
    public void ShowHiddenFansOff_ExcludesInvisible_AndReactsToToggle()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());

        ctrl.ShowHiddenFans = false;
        Assert.Equal("cpu", Assert.Single(ctrl.FilteredFans).FanId); // „front" ist versteckt

        ctrl.Fans.First(f => f.FanId == "front").Visible = true;
        Assert.Equal(2, ctrl.FilteredFans.Count);
    }

    [Fact]
    public void Search_CountsHits_AndClearsOnlyWhileSearching()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());
        Assert.False(ctrl.ClearSensorSearchCommand.CanExecute(null)); // Esc goes on to the window

        ctrl.SensorSearch = "amd";
        Assert.True(ctrl.IsSearchingSensors);
        Assert.Equal("1 Treffer", ctrl.SensorHitsText);
        ctrl.SensorSearch = "hwmon";
        Assert.Equal("3 Treffer", ctrl.SensorHitsText);

        ctrl.ClearSensorSearchCommand.Execute(null);
        Assert.Equal("", ctrl.SensorSearch);
        Assert.False(ctrl.ClearSensorSearchCommand.CanExecute(null));
    }

    [Fact]
    public void ShowHidden_CountsTheHiddenOnes_AndLocksWithNothingToHide()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot()); // „front" is hidden
        Assert.Equal("Ausgeblendete anzeigen (1)", ctrl.ShowHiddenFansLabel);
        Assert.True(ctrl.CanToggleHiddenFans);

        ctrl.Fans.Single(f => f.FanId == "front").Visible = true;
        Assert.Equal("Ausgeblendete anzeigen (0)", ctrl.ShowHiddenFansLabel);
        Assert.False(ctrl.CanToggleHiddenFans);

        ctrl.Fans.Single(f => f.FanId == "front").Visible = false;
        ctrl.ShowHiddenFans = false;
        ctrl.Fans.Single(f => f.FanId == "front").Visible = true;
        Assert.True(ctrl.CanToggleHiddenFans); // left out right now, so it can go back on
    }

    [Fact]
    public void SearchAndHiddenTexts_FollowTheLanguage()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(FilterSnapshot());
        ctrl.FanSearch = "front";
        var changed = new List<string?>();
        ctrl.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);

            Assert.Equal("Show hidden (1)", ctrl.ShowHiddenFansLabel);
            Assert.Contains(nameof(ctrl.ShowHiddenFansLabel), changed);
            Assert.Contains(nameof(ctrl.ShowHiddenSensorsLabel), changed);
            Assert.Contains(nameof(ctrl.FanHitsText), changed);
            Assert.Contains(nameof(ctrl.SensorHitsText), changed);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // the pinned test culture
        }
    }

    // --- Hidden-Fans in der Kurven-Zuordnungsliste („Lüfter dieser Kurve") -----------------------

    // Eine Kurve + zwei Lüfter, „front" ist global versteckt (optional der Kurve zugeordnet).
    private static MonitorSnapshot HiddenFanCurveSnapshot(string? hiddenAssignedCurveId = null) => new(
        "test",
        new[] { new SensorReading("hwmon6/temp1", "k10temp Tctl", SensorKind.Temperature, "°C", 41) },
        new[]
        {
            new FanReading("cpu", "CPU", 1500, 128, true),
            new FanReading("front", "Front", 1000, 128, true),
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
        new[] { new FanReading("cpu", "CPU", 1500, 128, true) },
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
            Assert.Single(ctrl.SourceSensorSections.SelectMany(g => g.Sensors)).Sensor.Id);
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
                new FanReading("front", "Front", 1000, 128, true),
                new FanReading("loose", "Loose", 900, 128, true),
                new FanReading("named", "Named", 1100, 128, true),
            },
            config);
    }

    [Fact]
    public void SelectedCurveFanGroups_GroupsByPosition_UngroupedLast()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(MixedFanSnapshot());
        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced); // SelectedCurve gesetzt → Checkboxen + Gruppen aufgebaut

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
        ctrl.AddCurveFromTemplateCommand.Execute(CurveTemplate.Balanced);

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
            ctrl.SourceSensorSections.Select(g => g.Name).ToArray());
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
        SensorCheckGroup group = Assert.Single(ctrl.SourceSensorSections);
        Assert.Equal("CPU", group.Name);
        Assert.Equal(2, group.Sensors.Count);
    }

    // ---------------------------------------------------------------------------
    // Follow-ups: position dialog on resync, airflow notices, missing tach names, language switch
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A resync rebuilds the fan rows: an open „Position wählen" would keep a manual control no row owns any more (no
    /// lock, no live updates). It is asked to close as a cancel - once; a later resync does not ask it again.
    /// </summary>
    [Fact]
    public void Resync_ClosesAnOpenPositionDialog()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshot());
        LocationPickController pick = ctrl.CreateLocationPick(ctrl.Fans.Single(f => f.FanId == "front"));
        int requests = 0;
        pick.CloseRequested += (_, _) => requests++;

        ctrl.Resync(AirflowSnapshot());
        Assert.Equal(1, requests);

        ctrl.Resync(AirflowSnapshot());
        Assert.Equal(1, requests);
    }

    /// <summary>
    /// The notice about uncalibrated case fans offers „Kalibrieren" only while a calibration can start: offline, in a dry
    /// run and while another device action runs the daemon would refuse it. The notice itself stays - the balance is
    /// still only estimated.
    /// </summary>
    [Fact]
    public void AirflowCalibrateNotice_OffersTheActionOnlyWhileCalibratingCanStart()
    {
        var ctrl = new CurveEditorController();
        ctrl.Initialize(AirflowSnapshot());
        AirflowHintRow Notice() => ctrl.AirflowHints.Single(h => h.Text.Contains("nicht kalibriert"));
        Assert.False(Notice().HasAction); // not connected yet

        ctrl.IsConnected = true;
        Assert.True(Notice().HasAction);

        ctrl.UpdateLive(AirflowSnapshot() with { DryRun = true });
        Assert.False(Notice().HasAction);
        Assert.Contains("nur nach Anzahl geschätzt", Notice().Text);

        ctrl.UpdateLive(AirflowSnapshot());
        Assert.True(Notice().HasAction);

        ctrl.UpdateLive(AirflowSnapshot() with { Identify = new IdentifyStatus("rear", Running: true, FailReason: null) });
        Assert.False(Notice().HasAction);
    }

    /// <summary>
    /// The proposal takes a role curve the profile already has as it is - switched off too, so the fan would land on
    /// hardware auto. The change row and the role tile say so instead of promising the curve.
    /// </summary>
    [Fact]
    public void Airflow_MarksARoleCurveTheProfileHasSwitchedOff()
    {
        MonitorSnapshot snapshot = AirflowSnapshot();
        var cpuCurve = new CurveConfig
        {
            Id = "airflow-cpu",
            Name = "Airflow · CPU",
            Enabled = false,
            SourceSensorId = "hwmon6/temp1",
            Points = new[] { new CurvePoint(40, 25), new CurvePoint(85, 100) },
        };
        Profile running = snapshot.Config.Profiles.Single() with { Curves = new[] { cpuCurve } };
        var ctrl = new CurveEditorController();
        ctrl.Initialize(snapshot with { Config = snapshot.Config with { Curves = new[] { cpuCurve }, Profiles = new[] { running } } });

        AirflowChangeRow cpu = ctrl.AirflowChanges.Single(r => r.Change.FanId == "cpu");
        Assert.True(cpu.IsCurveOff);
        Assert.False(cpu.IsNewCurve);
        Assert.True(cpu.ShowCurveOff);
        Assert.Contains("ausgeschaltet", cpu.AutomationName);
        Assert.False(ctrl.AirflowChanges.Single(r => r.Change.FanId == "front").IsCurveOff); // comes from the template

        cpu.Apply = false; // unticked, nothing changes - no mark
        Assert.False(cpu.ShowCurveOff);

        AirflowRoleTile tile = ctrl.AirflowRoles.Single(r => r.CurveName == "Airflow · CPU");
        Assert.Equal("ausgeschaltet", tile.StateText);
        Assert.Equal(StatusTone.Warn, tile.StateTone);
        Assert.NotNull(tile.StateTooltip);
        AirflowRoleTile intake = ctrl.AirflowRoles.Single(r => r.CurveName == "Airflow · Einlass");
        Assert.Equal(("neu aus Vorlage", StatusTone.Muted), (intake.StateText, intake.StateTone));
        Assert.Null(intake.StateTooltip);
    }

    /// <summary>A coupled speed sensor that is not detected right now is named by its own name from the config, else by its id.</summary>
    [Fact]
    public void ACoupledSensorThatIsNotDetected_IsNamedByItsOwnName()
    {
        MonitorSnapshot snapshot = AirflowSnapshot();
        AppConfig config = snapshot.Config with
        {
            Sensors = [.. snapshot.Config.Sensors, new SensorConfig { SensorId = "nct6798/fan7", Name = "AIO-Pumpe" }],
            Fans = snapshot.Config.Fans.Select(f => f.FanId switch
            {
                "cpu" => f with { RpmSource = "nct6798/fan7" },
                "front" => f with { RpmSource = "nct6798/fan9" },
                _ => f,
            }).ToList(),
        };
        var ctrl = new CurveEditorController();
        ctrl.Initialize(snapshot with { Config = config });
        ctrl.UpdateLive(snapshot with { Config = config });

        Assert.Equal("Nicht erkannt: AIO-Pumpe", ctrl.Fans.Single(f => f.FanId == "cpu").TachName);
        Assert.Equal("Nicht erkannt: nct6798/fan9", ctrl.Fans.Single(f => f.FanId == "front").TachName);
    }

    /// <summary>
    /// The sensor the hardware pairs a fan with is named in its row and - once the daemon reports it - in every other
    /// row's „assign by hand" list, which stays open meanwhile.
    /// </summary>
    [Fact]
    public void TheHardwaresPairing_IsNamedInTheRow_AndInTheOtherRowsList()
    {
        MonitorSnapshot before = AirflowSnapshot();
        before = before with
        {
            Sensors = [.. before.Sensors, new SensorReading("nct6798/fan1", "Fan 1", SensorKind.FanRpm, "RPM", 1500)],
        };
        var ctrl = new CurveEditorController();
        ctrl.Initialize(before);
        ctrl.UpdateLive(before);
        FanAssignRow cpu = ctrl.Fans.Single(f => f.FanId == "cpu");
        FanAssignRow front = ctrl.Fans.Single(f => f.FanId == "front");
        front.ShowTachChoices = true;
        TachChoice choice = front.TachChoices.Single(c => c.Option.Id == "nct6798/fan1");
        Assert.False(choice.HasAutoAt); // a daemon that does not report it yet

        ctrl.UpdateLive(before with
        {
            Fans = before.Fans.Select(f => f.Id == "cpu" ? f with { Tachometer = "nct6798/fan1" } : f).ToList(),
        });

        Assert.Equal("Automatisch: Fan 1", cpu.TachName);
        Assert.True(choice.IsEnabled);
        Assert.Equal($"automatisch bei {cpu.DisplayName}", choice.AutoAtText);
    }

    /// <summary>A language switch reaches the texts the curve editor builds in code, not only on their next change.</summary>
    [Fact]
    public void ALanguageSwitch_RefreshesTheCurveEditorsComputedTexts()
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
            Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU" } },
        };
        config = config with
        {
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p1", Name = "Standard", Curves = config.Curves,
                    Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "c1") },
                },
            },
            ActiveProfileId = "p1",
        };
        var ctrl = new CurveEditorController();
        ctrl.Initialize(Snapshot(config));
        CurveEditRow curve = ctrl.Curves.Single();
        ctrl.SelectedCurve = curve;
        Assert.Equal("Ungruppiert", ctrl.SelectedCurveFanGroups.Single().Name);
        var raised = new List<string?>();
        curve.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var pointRaised = new List<string?>();
        curve.Points[0].PropertyChanged += (_, e) => pointRaised.Add(e.PropertyName);
        var profileRaised = new List<string?>();
        ctrl.Profiles.Single().PropertyChanged += (_, e) => profileRaised.Add(e.PropertyName);
        try
        {
            Localizer.Instance.SetLanguage(LanguageChoice.English);

            Assert.Contains(nameof(CurveEditRow.PointsLabel), raised);
            Assert.Contains(nameof(CurveEditRow.SmoothingHint), raised);
            Assert.Contains(nameof(CurveEditRow.ActivityHint), raised);
            Assert.Contains(nameof(PointRow.TemperatureName), pointRaised);
            Assert.Contains(nameof(ProfileRow.ActivityHint), profileRaised);
            Assert.Equal("Ungrouped", ctrl.SelectedCurveFanGroups.Single().Name);
        }
        finally
        {
            Localizer.Instance.SetLanguage(LanguageChoice.German); // restore the pinned test culture
            ctrl.Dispose();
        }
    }
}
