// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Tests für die Diagnose unbekannter Kurven-Quellen (<see cref="ControlLoopService.UnknownSourceIds"/>):
/// findet config-referenzierte Sensor-IDs, die das Backend nach hwmon-Neunummerierung nicht (mehr) kennt —
/// Grundlage der einmaligen Daemon-Warnung.
/// </summary>
public class ControlLoopServiceDiagnosticsTests
{
    private static SensorDescriptor Sensor(string id) =>
        new(new SensorId(id), id, SensorKind.Temperature, "°C", id);

    private static CurveConfig Curve(string id, params string[] sources) =>
        new() { Id = id, Name = id, SourceSensorIds = sources, Points = new[] { new CurvePoint(30, 20) } };

    [Fact]
    public void ReturnsConfigSourcesMissingFromDiscovery()
    {
        var config = new AppConfig { Curves = new[] { Curve("c1", "hwmon2/temp1", "hwmon7/temp1") } };
        var discovered = new[] { Sensor("hwmon2/temp1"), Sensor("hwmon6/temp1") };

        Assert.Equal(new[] { "hwmon7/temp1" }, ControlLoopService.UnknownSourceIds(config, discovered));
    }

    [Fact]
    public void AllKnown_ReturnsEmpty()
    {
        var config = new AppConfig { Curves = new[] { Curve("c1", "a", "b") } };

        Assert.Empty(ControlLoopService.UnknownSourceIds(config, new[] { Sensor("a"), Sensor("b") }));
    }

    [Fact]
    public void Deduplicates_AcrossCurves()
    {
        var config = new AppConfig { Curves = new[] { Curve("c1", "missing", "a"), Curve("c2", "missing") } };

        Assert.Equal(new[] { "missing" }, ControlLoopService.UnknownSourceIds(config, new[] { Sensor("a") }));
    }
}
