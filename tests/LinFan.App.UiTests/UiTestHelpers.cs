// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.UiTests;

/// <summary>
/// Fake-Monitor: liefert einen festen, „verbundenen" Snapshot und zeichnet die gesendeten Steuerbefehle
/// auf (ist zugleich <see cref="ICommandSink"/>) — kein Socket, keine Hardware.
/// </summary>
internal sealed class FakeLiveMonitor : ILiveMonitor, ICommandSink
{
    private volatile MonitorSnapshot _snapshot;
    private int _readCount;
    public FakeLiveMonitor(MonitorSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>Wie oft der Poll-Loop bisher gelesen hat (thread-sicher; Read läuft auf dem ThreadPool).</summary>
    public int ReadCount => Volatile.Read(ref _readCount);

    /// <summary>
    /// Der aktuell ausgelieferte Snapshot — im Test zwischen den Pump-Zyklen austauschbar, um Live-Updates
    /// (wechselnde Mess­werte über mehrere Ticks) zu simulieren. <c>volatile</c>, da der Poll-Loop auf dem
    /// ThreadPool liest.
    /// </summary>
    public MonitorSnapshot Current
    {
        get => _snapshot;
        set => _snapshot = value;
    }

    public MonitorSnapshot Read()
    {
        Interlocked.Increment(ref _readCount);
        return _snapshot;
    }

    public List<(string fanId, byte pwm)> ManualCalls { get; } = new();
    public List<string> AutoCalls { get; } = new();
    public List<string> CalibrateCalls { get; } = new();
    public List<string> IdentifyCalls { get; } = new();
    public int CancelCalls { get; private set; }
    public List<AppConfig> ConfigCalls { get; } = new();
    public List<string> ActiveProfileCalls { get; } = new();
    public List<(string curveId, bool enabled)> CurveEnabledCalls { get; } = new();
    public List<AppConfig> ReplaceCalls { get; } = new();
    public int ResetCalls { get; private set; }
    public List<string> TachMappingCalls { get; } = new();
    public int CancelTachMappingCalls { get; private set; }
    public List<(string fanId, string? sensorId)> SetTachometerCalls { get; } = new();

    public Task<bool> SendConfigAsync(AppConfig config) { ConfigCalls.Add(config); return Task.FromResult(true); }
    public Task<bool> SendReplaceConfigAsync(AppConfig config) { ReplaceCalls.Add(config); return Task.FromResult(true); }
    public Task<bool> SendResetConfigAsync() { ResetCalls++; return Task.FromResult(true); }
    public Task SendManualPwmAsync(string fanId, byte pwm) { ManualCalls.Add((fanId, pwm)); return Task.CompletedTask; }
    public Task SendFanAutoAsync(string fanId) { AutoCalls.Add(fanId); return Task.CompletedTask; }
    public Task SendStartCalibrationAsync(string fanId) { CalibrateCalls.Add(fanId); return Task.CompletedTask; }
    public Task SendCancelCalibrationAsync() { CancelCalls++; return Task.CompletedTask; }
    public Task SendIdentifyAsync(string fanId) { IdentifyCalls.Add(fanId); return Task.CompletedTask; }
    public Task SendStartTachMappingAsync(string fanId) { TachMappingCalls.Add(fanId); return Task.CompletedTask; }
    public Task SendCancelTachMappingAsync() { CancelTachMappingCalls++; return Task.CompletedTask; }
    public Task SendSetFanTachometerAsync(string fanId, string? sensorId) { SetTachometerCalls.Add((fanId, sensorId)); return Task.CompletedTask; }
    public Task SendActiveProfileAsync(string profileId) { ActiveProfileCalls.Add(profileId); return Task.CompletedTask; }
    public Task SendSetCurveEnabledAsync(string curveId, bool enabled) { CurveEnabledCalls.Add((curveId, enabled)); return Task.CompletedTask; }
}

internal static class UiTestHelpers
{
    /// <summary>Pumpt den Dispatcher (samt Hintergrund-Poll des MainController), bis <paramref name="until"/> gilt.</summary>
    public static void PumpUntil(Func<bool> until, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5); // Hintergrund-Read läuft auf dem ThreadPool; Wall-Clock abwarten, dann erneut pumpen
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Sucht im Visual-Tree (nicht Logical): so erscheint nur der aktuell gewählte Tab und sein Inhalt
    /// genau einmal — der Logical-Tree würde nicht gewählte Tabs mitführen und den gewählten doppeln
    /// (TabItem + ContentPresenter). Eingeklappte (`IsVisible=false`) Controls bleiben im Visual-Tree.
    /// </summary>
    public static IEnumerable<T> Find<T>(this Visual root) where T : class =>
        root.GetVisualDescendants().OfType<T>();

    /// <summary>
    /// Text-Beschriftung eines Buttons — egal ob der Content ein reiner String ist oder (seit den
    /// Icon+Text-Buttons) ein Layout mit einem TextBlock. Hält die Button-Suchen stabil gegen PathIcons.
    /// </summary>
    public static string? ButtonLabel(Button button) => button.Content switch
    {
        string s => s,
        TextBlock t => t.Text,
        Panel p => p.Children.OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(),
        _ => null,
    };

    /// <summary>Ein „verbundener" Snapshot mit kleiner, vollständiger Config (Sensor/Lüfter/Kurve/Profil).</summary>
    public static MonitorSnapshot SampleSnapshot()
    {
        var curve = new CurveConfig
        {
            Id = "c1",
            Name = "Quiet",
            SourceSensorIds = new[] { "hwmon0/temp1" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var config = new AppConfig
        {
            Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU" } },
            Fans = new[] { new FanConfig { FanId = "hwmon0/pwm1", Name = "CPU Fan", AssignedCurveId = "c1" } },
            Curves = new[] { curve },
            Profiles = new[]
            {
                new Profile { Id = "p1", Name = "Standard", Curves = new[] { curve }, Assignments = new[] { new ProfileAssignment("hwmon0/pwm1", "c1") } },
            },
            ActiveProfileId = "p1",
        };

        return new MonitorSnapshot(
            "Verbunden",
            new[]
            {
                new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0),
                new SensorReading("hwmon0/fan1", "CPU Fan", SensorKind.FanRpm, "RPM", 1200),
            },
            new[] { new FanReading("hwmon0/pwm1", "CPU Fan", 1200, 120, FanMode.Auto, CanControl: true) },
            config,
            Connected: true);
    }

    /// <summary>
    /// „Verbundener" Snapshot mit <b>identischer Config</b> wie <see cref="SampleSnapshot"/>, aber frei
    /// wählbaren Live-Werten (Temp/RPM/PWM/Modus). Für Tier-4-Live-Update-Tests, die denselben Sensor/Lüfter
    /// über mehrere Ticks fortschreiben: gleiche IDs ⇒ <c>MainController.Apply</c> aktualisiert die Rows,
    /// statt sie neu zu bauen. Der Lüfter-Tacho läuft als separater RPM-Sensor mit (Dashboard-Verlauf).
    /// </summary>
    public static MonitorSnapshot LiveSnapshot(
        double tempC, double rpm, byte pwm = 120, FanMode mode = FanMode.Auto) =>
        SampleSnapshot() with
        {
            Sensors = new[]
            {
                new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", tempC),
                new SensorReading("hwmon0/fan1", "CPU Fan", SensorKind.FanRpm, "RPM", rpm),
            },
            Fans = new[] { new FanReading("hwmon0/pwm1", "CPU Fan", rpm, pwm, mode, CanControl: true) },
        };

    /// <summary>„Verbundener" Snapshot mit beliebigen Lüfter-Readings (für CanControl-Sichtbarkeit u. Ä.).</summary>
    public static MonitorSnapshot SnapshotWithFans(params FanReading[] fans) =>
        new(
            "Verbunden",
            new[] { new SensorReading("hwmon0/temp1", "CPU", SensorKind.Temperature, "°C", 45.0) },
            fans,
            new AppConfig { Sensors = new[] { new SensorConfig { SensorId = "hwmon0/temp1", Name = "CPU" } } },
            Connected: true);
}
