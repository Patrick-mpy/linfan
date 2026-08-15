// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Text.Json;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Schutznetz für den GUI⇄Daemon-Konfigurationsvertrag (Finding F2). Die Core-Modelle und die IPC-DTOs
/// werden über die Prozessgrenze von zwei getrennten Mappern übersetzt (<see cref="ConfigMapper"/> im
/// Daemon, <c>IpcLiveMonitor</c> in der App) - bewusste Duplizierung, weil die Grenze keine geteilte
/// Mapper-Klasse erlaubt. Genau deshalb ist „Feld ergänzt, ein Mapper/Typ vergessen" die reale
/// Fehlerquelle. Diese Tests fangen beide Spielarten:
/// <list type="bullet">
/// <item>strukturell - jedes editierbare Feld hat in beiden Typen ein Gegenstück (oder steht bewusst
///   auf einer Allowlist);</item>
/// <item>werthaltig - ein voll besetzter Round-Trip durch die echten Daemon-Mapper darf kein
///   editierbares Feld verlieren.</item>
/// </list>
/// </summary>
public class ConfigContractTests
{
    private sealed record Pair(
        Type Core, Type Ipc,
        Dictionary<string, string> Rename,
        HashSet<string> CoreOnly,
        HashSet<string> IpcOnly);

    // Core-Modell ↔ IPC-DTO. Rename: abweichend benannte Felder. CoreOnly: daemon-autoritativ, bewusst
    // nicht über IPC editierbar. IpcOnly: nur im DTO (Kompatibilität/abgeleitet).
    private static readonly Pair[] Pairs =
    {
        new(typeof(AppConfig), typeof(IpcConfig), new(),
            CoreOnly: new() { nameof(AppConfig.SchemaVersion), nameof(AppConfig.PollIntervalMs), nameof(AppConfig.FailSafeTempC) },
            IpcOnly: new()),
        new(typeof(FanConfig), typeof(IpcFanAssignment), new(),
            // Calibration wird read-only an die GUI gespiegelt (Badge nach Neustart), bleibt aber
            // daemon-autoritativ & nicht über IPC editierbar - die rohe Messreihe bleibt ganz im Daemon.
            CoreOnly: new() { nameof(FanConfig.Calibration) },
            IpcOnly: new()),
        new(typeof(SensorConfig), typeof(IpcSensorName),
            Rename: new() { [nameof(SensorConfig.SensorId)] = nameof(IpcSensorName.Id) },
            CoreOnly: new(), IpcOnly: new()),
        new(typeof(CurveConfig), typeof(IpcCurve), new(),
            // SourceSensorId (Legacy/Migration) lebt in beiden Typen - keine Ausnahme nötig.
            CoreOnly: new(), IpcOnly: new()),
        new(typeof(Profile), typeof(IpcProfile), new(), new(), new()),
        new(typeof(ProfileAssignment), typeof(IpcProfileAssignment), new(), new(), new()),
        new(typeof(CurvePoint), typeof(IpcCurvePoint), new(), new(), new()),
    };

    private static IEnumerable<string> PropNames(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

    [Fact]
    public void EveryEditableCoreField_HasAnIpcCounterpart()
    {
        var missing = new List<string>();
        foreach (Pair p in Pairs)
        {
            var ipcNames = PropNames(p.Ipc).ToHashSet();
            foreach (string core in PropNames(p.Core))
            {
                if (p.CoreOnly.Contains(core)) continue;
                string expected = p.Rename.GetValueOrDefault(core, core);
                if (!ipcNames.Contains(expected))
                    missing.Add($"{p.Core.Name}.{core} → {p.Ipc.Name}.{expected}");
            }
        }

        Assert.True(missing.Count == 0,
            "Core-Felder ohne IPC-Gegenstück (DTO-Feld ergänzen oder bewusst als CoreOnly allowlisten):\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void EveryIpcField_HasACoreCounterpart()
    {
        var missing = new List<string>();
        foreach (Pair p in Pairs)
        {
            var reverse = p.Rename.ToDictionary(kv => kv.Value, kv => kv.Key);
            var coreNames = PropNames(p.Core).ToHashSet();
            foreach (string ipc in PropNames(p.Ipc))
            {
                if (p.IpcOnly.Contains(ipc)) continue;
                string expected = reverse.GetValueOrDefault(ipc, ipc);
                if (!coreNames.Contains(expected))
                    missing.Add($"{p.Ipc.Name}.{ipc} → {p.Core.Name}.{expected}");
            }
        }

        Assert.True(missing.Count == 0,
            "IPC-Felder ohne Core-Gegenstück (Core-Feld ergänzen oder bewusst als IpcOnly allowlisten):\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void Merge_CarriesEveryEditableField_FromIncoming_OverStaleCurrent()
    {
        AppConfig desired = FullConfig();

        // „Veralteter" Daemon-Stand: gleiche Ids (Lüfter werden per Id gematcht), aber jedes editierbare
        // Feld anders besetzt. Die daemon-autoritativen Felder (SchemaVersion/Poll/FailSafe/Calibration)
        // bleiben identisch - sie stammen beim Merge aus `current` und dürfen keinen Diff erzeugen.
        AppConfig stale = desired with
        {
            Sensors = new[] { new SensorConfig { SensorId = "s1", Name = "ALT", Group = "ALTG", Hidden = false } },
            Fans = new[]
            {
                new FanConfig
                {
                    FanId = "f1", Name = "ALT", MinPwm = 0, MaxPwm = 255, AssignedCurveId = null,
                    Location = FanLocation.Unspecified, Hidden = false, Calibration = null,
                },
            },
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "ALT", SourceSensorIds = new[] { "sX" }, Aggregation = SensorAggregation.Max,
                    HysteresisC = 9.0, InterpolationMode = InterpolationMode.Linear,
                    Points = new[] { new CurvePoint(1, 1) },
                },
            },
            Profiles = Array.Empty<Profile>(),
            ActiveProfileId = "ALT",
            OnboardingCompleted = false,
        };

        AppConfig result = ConfigMapper.Merge(stale, ConfigMapper.ToIpc(desired));

        // Verliert ToIpc oder Merge ein editierbares Feld, fällt der Wert auf den (abweichenden) stale-Stand
        // bzw. den Default zurück → der JSON-Vergleich schlägt an.
        Assert.Equal(Serialize(desired), Serialize(result));
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string Serialize(AppConfig c) => JsonSerializer.Serialize(c, Json);

    /// <summary>Voll besetzte Config in moderner Form (Schema-2-Quellen); jedes editierbare Feld nicht-default.</summary>
    private static AppConfig FullConfig()
    {
        var curve = new CurveConfig
        {
            Id = "c1",
            Name = "Quiet",
            SourceSensorIds = new[] { "s1" },
            Aggregation = SensorAggregation.Avg,
            HysteresisC = 3.5,
            InterpolationMode = InterpolationMode.Spline,
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        return new AppConfig
        {
            SchemaVersion = 2,
            PollIntervalMs = 1500,
            FailSafeTempC = 88,
            Sensors = new[] { new SensorConfig { SensorId = "s1", Name = "CPU", Group = "Zone A", Hidden = true } },
            Fans = new[]
            {
                new FanConfig
                {
                    FanId = "f1", Name = "Front", MinPwm = 40, MaxPwm = 220, AssignedCurveId = "c1",
                    Location = FanLocation.CaseFrontIntake, Hidden = true, Calibration = null,
                },
            },
            Curves = new[] { curve },
            Profiles = new[]
            {
                new Profile
                {
                    Id = "p1", Name = "Balanced", Curves = new[] { curve },
                    Assignments = new[] { new ProfileAssignment("f1", "c1") },
                },
            },
            ActiveProfileId = "p1",
            OnboardingCompleted = true,
        };
    }
}
