// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class TachometerMappingServiceTests
{
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task Map_MatchesTargetTach_WhenResponseIsClear()
    {
        // Drei Lüfter mit je eigenem Tacho, kein Übersprechen: nur der angetriebene Tacho steigt.
        var hw = new CouplingHardware(temp: 40);
        hw.AddFan("pwm1", "fan1");
        hw.AddFan("pwm2", "fan2");
        hw.AddFan("pwm3", "fan3");
        var svc = new TachometerMappingService(hw, hw, NoDelay);

        TachMappingResult result = await svc.MapAsync(new FanId("pwm2"), new TachMappingOptions());

        Assert.Equal(TachMappingOutcome.Matched, result.Outcome);
        Assert.Equal(new SensorId("fan2"), result.Tachometer);
        Assert.True(result.RiseRpm > 0);
        Assert.True(hw.RestoreCount >= 1);              // Fail-Safe nach dem Antreiben
    }

    [Fact]
    public async Task Map_NoResponse_WhenTargetHasNoTach()
    {
        // Ziel-Lüfter ohne eigenen Tacho (z. B. AIO-Pumpe); die anderen reagieren nicht auf ihn.
        var hw = new CouplingHardware(temp: 40);
        hw.AddFan("pump", tachId: null);
        hw.AddFan("pwm1", "fan1");
        var svc = new TachometerMappingService(hw, hw, NoDelay);

        TachMappingResult result = await svc.MapAsync(new FanId("pump"), new TachMappingOptions());

        Assert.Equal(TachMappingOutcome.NoResponse, result.Outcome);
        Assert.Null(result.Tachometer);
        Assert.True(hw.RestoreCount >= 1);
    }

    [Fact]
    public async Task Map_Ambiguous_WhenCrossTalkDominates()
    {
        // Starkes Luft-Übersprechen: der Nachbar-Tacho steigt fast so stark mit → nicht eindeutig.
        var hw = new CouplingHardware(temp: 40) { CrossTalk = 0.7 };
        hw.AddFan("pwm1", "fan1");
        hw.AddFan("pwm2", "fan2");
        var svc = new TachometerMappingService(hw, hw, NoDelay);

        TachMappingResult result = await svc.MapAsync(new FanId("pwm1"), new TachMappingOptions());

        Assert.Equal(TachMappingOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Tachometer);
    }

    [Fact]
    public async Task Map_LowCrossTalk_StillMatches()
    {
        // Leichtes Übersprechen (10 %) bleibt unter dem Dominanz-Faktor → eindeutig zugeordnet.
        var hw = new CouplingHardware(temp: 40) { CrossTalk = 0.1 };
        hw.AddFan("pwm1", "fan1");
        hw.AddFan("pwm2", "fan2");
        var svc = new TachometerMappingService(hw, hw, NoDelay);

        TachMappingResult result = await svc.MapAsync(new FanId("pwm1"), new TachMappingOptions());

        Assert.Equal(TachMappingOutcome.Matched, result.Outcome);
        Assert.Equal(new SensorId("fan1"), result.Tachometer);
    }

    [Fact]
    public async Task Map_OverTemperature_Aborts_AndRestores()
    {
        var hw = new CouplingHardware(temp: 95);        // schon über Limit
        hw.AddFan("pwm1", "fan1");
        var svc = new TachometerMappingService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<OverTemperatureException>(() =>
            svc.MapAsync(new FanId("pwm1"), new TachMappingOptions { FailSafeTempC = 90 }));

        Assert.True(hw.RestoreCount >= 1);
    }

    [Fact]
    public async Task Map_NoReadableTemperature_Aborts_AndRestores()
    {
        var hw = new CouplingHardware(temp: double.NaN); // Temp durchgängig NaN → kein Watchdog
        hw.AddFan("pwm1", "fan1");
        var svc = new TachometerMappingService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<NoTemperatureReadingException>(() =>
            svc.MapAsync(new FanId("pwm1"), new TachMappingOptions()));

        Assert.True(hw.RestoreCount >= 1);
    }

    [Fact]
    public async Task Map_NotControllable_Throws()
    {
        var hw = new CouplingHardware(temp: 40);
        hw.AddFan("pwm1", "fan1", canControl: false);
        var svc = new TachometerMappingService(hw, hw, NoDelay);

        await Assert.ThrowsAsync<FanNotControllableException>(() =>
            svc.MapAsync(new FanId("pwm1"), new TachMappingOptions()));
    }

    /// <summary>
    /// Test-Backend, das die physische Kopplung modelliert: jeder Lüfter treibt genau seinen Tacho
    /// (RPM = 10·PWM); optionales <see cref="CrossTalk"/> lässt fremde Tachos anteilig mitdrehen
    /// (Luft-Übersprechen). Ein Temperatursensor speist den Watchdog.
    /// </summary>
    private sealed class CouplingHardware : ISensorBackend, IFanController
    {
        private readonly List<SensorDescriptor> _sensors = new();
        private readonly List<FanDescriptor> _fans = new();
        private readonly Dictionary<string, byte> _pwm = new();
        private readonly Dictionary<string, string> _fanByTach = new();   // Tach-Id → Lüfter-Id
        private readonly double _temp;

        public int RestoreCount { get; private set; }

        /// <summary>Anteil, mit dem der angetriebene Lüfter <em>fremde</em> Tachos mitdreht (0 = keiner).</summary>
        public double CrossTalk { get; init; }

        public CouplingHardware(double temp)
        {
            _temp = temp;
            _sensors.Add(new SensorDescriptor(new SensorId("t"), "temp", SensorKind.Temperature, "°C", "t"));
        }

        public void AddFan(string id, string? tachId = null, bool canControl = true)
        {
            var tach = tachId is null ? (SensorId?)null : new SensorId(tachId);
            _fans.Add(new FanDescriptor(new FanId(id), id, canControl, tach, id));
            if (tachId is not null)
            {
                _sensors.Add(new SensorDescriptor(new SensorId(tachId), tachId, SensorKind.FanRpm, "RPM", tachId));
                _fanByTach[tachId] = id;
            }
        }

        private static int Rpm(byte pwm) => pwm * 10;

        public IReadOnlyList<SensorDescriptor> DiscoverSensors() => _sensors;

        public double ReadValue(SensorId id)
        {
            if (id.Value == "t")
                return _temp;
            if (!_fanByTach.TryGetValue(id.Value, out string? ownFan))
                return double.NaN;

            double own = Rpm(_pwm.GetValueOrDefault(ownFan));
            double cross = 0;
            if (CrossTalk > 0)
                foreach ((string fanId, byte pwm) in _pwm)
                    if (fanId != ownFan)
                        cross += CrossTalk * Rpm(pwm);
            return own + cross;
        }

        public IReadOnlyList<FanDescriptor> DiscoverFans() => _fans;

        public bool CanControl(FanId id) => _fans.First(f => f.Id == id).CanControl;

        public FanMode GetMode(FanId id) => FanMode.Manual;

        public void SetMode(FanId id, FanMode mode) { }

        public byte GetPwm(FanId id) => _pwm.GetValueOrDefault(id.Value);

        public void SetPwm(FanId id, byte value)
        {
            if (!CanControl(id))
                throw new NotSupportedException($"{id} nicht steuerbar");
            _pwm[id.Value] = value;
        }

        public void RestoreDefaults()
        {
            RestoreCount++;
            _pwm.Clear(); // Firmware-Auto: Software-Stellwerte fallen weg
        }

        public void Dispose() { }
    }
}
