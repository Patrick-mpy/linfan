// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Hardware.Mac.Tests;

/// <summary>
/// Kuratierte Temperatur-Discovery über dem Fake-SMC: Apple-Silicon-Familien-Erkennung (M1/M2/M3),
/// die daraus folgenden Labels bei Key-Kollisionen (<c>Tp09</c>, <c>Tp0P</c>) und die stabile
/// Gruppen-Reihenfolge der Deskriptoren (CPU → GPU → SoC/Board → Storage → Battery → Sonstiges).
/// Fehlende Keys stören nie - nur vorhandene, plausible Keys werden zu Sensoren.
/// </summary>
public sealed class MacTemperatureDiscoveryTests
{
    private static MacSmcBackend Backend(FakeSmc smc) =>
        new(smc, new MacSmcBackend.ControlCapability(true, null));

    private static string[] TempNames(MacSmcBackend b) => b.DiscoverSensors()
        .Where(s => s.Kind == SensorKind.Temperature)
        .Select(s => s.Name)
        .ToArray();

    [Fact]
    public void M2Family_LabelsClusters_AndOrdersGroups()
    {
        var smc = new FakeSmc();
        // M2-Familie (4 lesbare Cluster-Keys): Tp09 muss hier P-Core 3 sein - auf M1 wäre derselbe
        // Key E-Core 1. Tp1t ist vorhanden, aber implausibel (0 °C): zählt für die Familien-
        // Erkennung, wird aber nicht als Sensor exponiert.
        smc.SetFloat("Tp1h", 38f);
        smc.SetFloat("Tp1t", 0f);
        smc.SetFloat("Tp01", 52f);
        smc.SetFloat("Tp09", 54f);
        smc.SetFloat("Tg0f", 47f);
        // Plattformübergreifende Keys aus der flachen Liste (Battery vor Sonstiges).
        smc.SetFloat("TB0T", 31f);
        smc.SetFloat("TW0P", 44f);

        using var b = Backend(smc);

        Assert.Equal(
            new[]
            {
                "CPU Efficiency Core 1",
                "CPU Performance Core 1",
                "CPU Performance Core 3",
                "GPU 1",
                "Battery",
                "Airport / Wi-Fi",
            },
            TempNames(b));
    }

    [Fact]
    public void M1Family_Tp09_IsEfficiencyCore()
    {
        var smc = new FakeSmc();
        // Tp0T/Tp0H gibt es nur in der M1-Tabelle → M1 (4 Treffer) schlägt M2 (2 Treffer).
        smc.SetFloat("Tp09", 39f);
        smc.SetFloat("Tp0T", 40f);
        smc.SetFloat("Tp01", 50f);
        smc.SetFloat("Tp0H", 51f);

        using var b = Backend(smc);

        Assert.Equal(
            new[]
            {
                "CPU Efficiency Core 1",   // Tp09 - M1-Bedeutung
                "CPU Efficiency Core 2",   // Tp0T
                "CPU Performance Core 1",  // Tp01
                "CPU Performance Core 4",  // Tp0H
            },
            TempNames(b));
    }

    [Fact]
    public void M3Family_PartialKeys_AreLabeled()
    {
        var smc = new FakeSmc();
        smc.SetFloat("Te05", 35f);
        smc.SetFloat("Tf04", 48f);
        smc.SetFloat("Tf14", 42f);

        using var b = Backend(smc);

        Assert.Equal(
            new[] { "CPU Efficiency Core 1", "CPU Performance Core 1", "GPU 1" },
            TempNames(b));
    }

    [Fact]
    public void Intel_WithoutFamily_UsesFlatLabels_InGroupOrder()
    {
        var smc = new FakeSmc();
        // Tp0P allein ist EIN M1-Tabellen-Treffer - unter der Familien-Schwelle bleibt er das
        // Intel-Netzteil, kein "CPU Performance Core 6".
        smc.SetFloat("Tp0P", 45f);
        smc.SetFloat("TC0P", 50f);
        smc.SetFloat("TG0D", 60f);
        smc.SetFloat("TM0P", 41f);
        smc.SetFloat("TH0P", 38f);
        smc.SetFloat("TB0T", 30f);
        smc.SetFloat("TA0P", 28f);

        using var b = Backend(smc);

        Assert.Equal(
            new[]
            {
                "CPU Proximity",
                "GPU Die",
                "Memory Proximity",
                "Power Supply Proximity",
                "Drive Bay 1",
                "Battery",
                "Ambient",
            },
            TempNames(b));
    }

    [Fact]
    public void NewCuratedKeys_AreDiscovered_WhenPresent()
    {
        var smc = new FakeSmc();
        smc.SetFloat("TCSA", 55f);
        smc.SetFloat("TH0x", 40f);
        smc.SetFloat("TB2T", 32f);
        smc.SetFloat("TaLP", 35f);
        smc.SetFloat("TaRF", 36f);

        using var b = Backend(smc);

        Assert.Equal(
            new[] { "CPU System Agent", "SSD (NAND)", "Battery 2", "Airflow Left", "Airflow Right" },
            TempNames(b));
    }

    [Fact]
    public void FamilyBelowThreshold_ExposesNoClusterSensors()
    {
        var smc = new FakeSmc();
        // Nur 2 Familien-Treffer (< Schwelle 3): lieber kein Sensor als ein falsch beschrifteter.
        smc.SetFloat("Tp01", 50f);
        smc.SetFloat("Tp05", 51f);

        using var b = Backend(smc);

        Assert.Empty(TempNames(b));
    }

    [Fact]
    public void Discovery_IsDeterministic_AcrossInstances()
    {
        static FakeSmc Board()
        {
            var smc = new FakeSmc();
            smc.SetUi8("FNum", 2);
            smc.SetFloat("F0Ac", 1500f);
            smc.SetFloat("F1Ac", 1300f);
            smc.SetFloat("Tp1h", 38f);
            smc.SetFloat("Tp01", 52f);
            smc.SetFloat("Tg0f", 47f);
            smc.SetFloat("TB0T", 31f);
            return smc;
        }

        using var a = Backend(Board());
        using var b = Backend(Board());

        Assert.Equal(
            a.DiscoverSensors().Select(s => (s.Id, s.Name, s.Kind)),
            b.DiscoverSensors().Select(s => (s.Id, s.Name, s.Kind)));

        // Tachos hängen hinter den Temperaturen und folgen dem Lüfter-Index.
        Assert.Equal(
            new[] { "Fan 1", "Fan 2" },
            a.DiscoverSensors().Where(s => s.Kind == SensorKind.FanRpm).Select(s => s.Name));
    }
}
