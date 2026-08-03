// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class SensorAggregatorTests
{
    private static FakeHardware Hw(params (string Id, double Value)[] sensors)
    {
        var hw = new FakeHardware();
        foreach ((string id, double value) in sensors)
            hw.AddTempSensor(id, value);
        return hw;
    }

    [Fact]
    public void Max_ReturnsHottestSensor()
    {
        var hw = Hw(("a", 40), ("b", 70), ("c", 55));

        double result = SensorAggregator.Aggregate(new[] { "a", "b", "c" }, hw, SensorAggregation.Max);

        Assert.Equal(70, result);
    }

    [Fact]
    public void Avg_ReturnsMeanOfReadableSensors()
    {
        var hw = Hw(("a", 40), ("b", 60));

        double result = SensorAggregator.Aggregate(new[] { "a", "b" }, hw, SensorAggregation.Avg);

        Assert.Equal(50, result);
    }

    [Fact]
    public void Max_IgnoresNaNSensors()
    {
        var hw = Hw(("a", 40), ("b", double.NaN), ("c", 65));

        double result = SensorAggregator.Aggregate(new[] { "a", "b", "c" }, hw, SensorAggregation.Max);

        Assert.Equal(65, result);
    }

    [Fact]
    public void Avg_IgnoresNaNSensors_AveragesOnlyReadable()
    {
        var hw = Hw(("a", 40), ("b", double.NaN), ("c", 60)); // Mittel aus 40 und 60 = 50

        double result = SensorAggregator.Aggregate(new[] { "a", "b", "c" }, hw, SensorAggregation.Avg);

        Assert.Equal(50, result);
    }

    [Fact]
    public void EmptyList_ReturnsNaN()
    {
        var hw = Hw(("a", 40));

        Assert.True(double.IsNaN(SensorAggregator.Aggregate(Array.Empty<string>(), hw, SensorAggregation.Max)));
        Assert.True(double.IsNaN(SensorAggregator.Aggregate(Array.Empty<string>(), hw, SensorAggregation.Avg)));
    }

    [Fact]
    public void AllNaN_ReturnsNaN()
    {
        var hw = Hw(("a", double.NaN), ("b", double.NaN));

        Assert.True(double.IsNaN(SensorAggregator.Aggregate(new[] { "a", "b" }, hw, SensorAggregation.Max)));
        Assert.True(double.IsNaN(SensorAggregator.Aggregate(new[] { "a", "b" }, hw, SensorAggregation.Avg)));
    }

    [Fact]
    public void UnknownSensorId_TreatedAsNaN()
    {
        var hw = Hw(("a", 50));

        // "missing" gibt es nicht → wie NaN; nur "a" zählt.
        Assert.Equal(50, SensorAggregator.Aggregate(new[] { "a", "missing" }, hw, SensorAggregation.Max));
        Assert.Equal(50, SensorAggregator.Aggregate(new[] { "a", "missing" }, hw, SensorAggregation.Avg));
    }

    [Fact]
    public void SingleSensor_BothModesReturnItsValue()
    {
        var hw = Hw(("a", 47));

        Assert.Equal(47, SensorAggregator.Aggregate(new[] { "a" }, hw, SensorAggregation.Max));
        Assert.Equal(47, SensorAggregator.Aggregate(new[] { "a" }, hw, SensorAggregation.Avg));
    }

    // Regression: ein Backend, das für unbekannte IDs WIRFT (wie LinuxHwmonBackend / die Conformance-Referenz),
    // darf den Aggregator — und damit den ganzen Regel-Tick — nicht abreißen. Reproduziert den Praxis-Crash,
    // wenn die Config auf einen Sensor zeigt, dessen hwmon-Nummer sich seit dem Speichern geändert hat.
    [Fact]
    public void ThrowingBackend_UnknownSensorId_TreatedAsNaN_DoesNotThrow()
    {
        var hw = new ThrowingBackend(("a", 50));

        Assert.Equal(50, SensorAggregator.Aggregate(new[] { "a", "missing" }, hw, SensorAggregation.Max));
        Assert.Equal(50, SensorAggregator.Aggregate(new[] { "missing", "a" }, hw, SensorAggregation.Avg));
        Assert.True(double.IsNaN(SensorAggregator.Aggregate(new[] { "missing" }, hw, SensorAggregation.Max)));
    }

    // Regression (verschärft): NICHT nur KeyNotFoundException — AUCH jede andere Backend-Exception
    // (z. B. IOException/EIO, unerwartete Fehler) muss wie „nicht lesbar" zählen, statt den ganzen
    // Regel-Tick (inkl. Übertemp-Watchdog) abzureißen. Der Aggregator fängt daher breit.
    [Fact]
    public void Backend_ThrowingUnexpectedError_TreatedAsNaN_DoesNotThrow()
    {
        var hw = new AlwaysThrowingBackend("a");

        Assert.True(double.IsNaN(SensorAggregator.Aggregate(new[] { "a" }, hw, SensorAggregation.Max)));
        Assert.True(double.IsNaN(SensorAggregator.Aggregate(new[] { "a" }, hw, SensorAggregation.Avg)));
    }

    // ── Hottest(): geteilter Fail-Safe-Watchdog (Regel-Loop + Kalibrierung) ──────────────────────

    [Fact]
    public void Hottest_ReturnsMaxOverTemperatureSensors_IgnoresFanChannels()
    {
        var hw = new FakeHardware();
        hw.AddTempSensor("a", 40);
        hw.AddTempSensor("b", 70);
        hw.AddFanSensor("rpm", 3000); // RPM-Kanal zählt nicht als Temperatur

        Assert.Equal(70, SensorAggregator.Hottest(hw));
    }

    [Fact]
    public void Hottest_NoTemperatureSensors_ReturnsNaN()
    {
        var hw = new FakeHardware();
        hw.AddFanSensor("rpm", 3000);

        Assert.True(double.IsNaN(SensorAggregator.Hottest(hw)));
    }

    [Fact]
    public void Hottest_ThrowingDiscovery_ReturnsNaN_DoesNotThrow()
    {
        // Kaputte Discovery darf den Watchdog-Tick nicht abreißen → NaN (Blind-Tick-Fail-Safe greift).
        Assert.True(double.IsNaN(SensorAggregator.Hottest(new ThrowingDiscoveryBackend())));
    }

    [Fact]
    public void Hottest_SkipsThrowingSensor_ReturnsReadableMax()
    {
        // Ein werfender Einzel-Kanal (EIO) wird übersprungen, der lesbare bleibt maßgeblich — kein Abriss.
        Assert.Equal(62, SensorAggregator.Hottest(new OneGoodOneThrowingBackend()));
    }

    /// <summary>Backend, dessen Discovery wirft (z. B. hwmon-Verzeichnis unlesbar).</summary>
    private sealed class ThrowingDiscoveryBackend : ISensorBackend
    {
        public IReadOnlyList<SensorDescriptor> DiscoverSensors() =>
            throw new InvalidOperationException("Discovery kaputt");
        public double ReadValue(SensorId id) => 0;
        public void Dispose() { }
    }

    /// <summary>Zwei Temp-Kanäle: einer liefert einen Wert, der andere wirft beim Lesen (EIO).</summary>
    private sealed class OneGoodOneThrowingBackend : ISensorBackend
    {
        public IReadOnlyList<SensorDescriptor> DiscoverSensors() => new[]
        {
            new SensorDescriptor(new SensorId("good"), "good", SensorKind.Temperature, "°C", "good"),
            new SensorDescriptor(new SensorId("bad"), "bad", SensorKind.Temperature, "°C", "bad"),
        };
        public double ReadValue(SensorId id) =>
            id.Value == "good" ? 62.0 : throw new InvalidOperationException("EIO");
        public void Dispose() { }
    }

    /// <summary>Minimal-Backend, das — wie der echte Linux-Backend — für unbekannte Sensor-IDs wirft.</summary>
    private sealed class ThrowingBackend : ISensorBackend
    {
        private readonly Dictionary<string, double> _values;

        public ThrowingBackend(params (string Id, double Value)[] sensors) =>
            _values = sensors.ToDictionary(s => s.Id, s => s.Value);

        public IReadOnlyList<SensorDescriptor> DiscoverSensors() =>
            _values.Keys.Select(id => new SensorDescriptor(new SensorId(id), id, SensorKind.Temperature, "°C", id)).ToList();

        public double ReadValue(SensorId id) =>
            _values.TryGetValue(id.Value, out double v) ? v : throw new KeyNotFoundException($"Unbekannter Sensor: {id}");

        public void Dispose() { }
    }

    /// <summary>Backend, dessen <see cref="ReadValue"/> immer wirft (kein KeyNotFound) — z. B. EIO/Treiberfehler.</summary>
    private sealed class AlwaysThrowingBackend : ISensorBackend
    {
        private readonly string _id;
        public AlwaysThrowingBackend(string id) => _id = id;

        public IReadOnlyList<SensorDescriptor> DiscoverSensors() =>
            new[] { new SensorDescriptor(new SensorId(_id), _id, SensorKind.Temperature, "°C", _id) };

        public double ReadValue(SensorId id) => throw new InvalidOperationException("Backend-Fehler (z. B. EIO)");

        public void Dispose() { }
    }
}
