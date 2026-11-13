// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

// Serialisiert mit JsonConfigStoreExistsTests: beide mutieren die prozess-globale Env-Var
// LINFAN_CONFIG; ohne gemeinsame Collection liefen sie parallel und überschrieben sich gegenseitig.
[Collection("env-config")]
public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"linfan-test-{Guid.NewGuid():N}");
    private string Path_ => System.IO.Path.Combine(_dir, "config.json");

    [Fact]
    public void Load_MissingFile_ReturnsEmptyDefaults()
    {
        var config = new JsonConfigStore(Path_).Load();

        Assert.Empty(config.Fans);
        Assert.Empty(config.Curves);
        Assert.Equal(1000, config.PollIntervalMs);
        Assert.Equal(90, config.FailSafeTempC);
    }

    [Fact]
    public void Load_AFileFromBeforeSensorGroups_ReadsThemAsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """{ "schemaVersion": 3, "sensors": [ { "sensorId": "k10temp/temp1", "group": "CPU" } ] }""");

        AppConfig config = new JsonConfigStore(Path_).Load();

        Assert.NotNull(config.SensorGroups); // no schema bump: the default applies
        Assert.Empty(config.SensorGroups);
        Assert.Equal("CPU", Assert.Single(config.Sensors).Group);
    }

    [Fact]
    public void Load_AFileFromBeforeCaseTypes_ReadsATower_AndTheTypeSurvivesASave()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """{ "schemaVersion": 3, "fans": [ { "fanId": "nct/pwm1", "location": 4 } ] }""");
        var store = new JsonConfigStore(Path_);

        AppConfig config = store.Load();
        Assert.Equal(CaseType.Tower, config.CaseType); // no schema bump: the default applies
        Assert.Equal(FanLocation.CaseFrontIntake, Assert.Single(config.Fans).Location);

        store.Save(config with { CaseType = CaseType.Server, Fans = [config.Fans[0] with { Location = FanLocation.FanWall }] });
        AppConfig reloaded = store.Load();
        Assert.Equal(CaseType.Server, reloaded.CaseType);
        Assert.Equal(FanLocation.FanWall, Assert.Single(reloaded.Fans).Location);
    }

    [Fact]
    public void Load_AFileFromBeforeDevices_ReadsNone_AndARecordedDeviceSurvivesASave()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """{ "schemaVersion": 3, "fans": [ { "fanId": "amdgpu/pwm1" } ], "sensors": [ { "sensorId": "nvme/temp1" } ] }""");
        var store = new JsonConfigStore(Path_);

        AppConfig config = store.Load();
        Assert.Null(Assert.Single(config.Fans).Device); // no schema bump: the daemon records them at the next start
        Assert.Null(Assert.Single(config.Sensors).Device);

        var gpu = new HardwareDevice("amdgpu", "pci:1002:73bf:1849:5201");
        var nvme = new HardwareDevice("nvme", null);
        store.Save(config with
        {
            Fans = [config.Fans[0] with { Device = gpu }],
            Sensors = [config.Sensors[0] with { Device = nvme }],
        });
        AppConfig reloaded = store.Load();
        Assert.Equal(gpu, Assert.Single(reloaded.Fans).Device);
        Assert.Equal(nvme, Assert.Single(reloaded.Sensors).Device);
    }

    /// <summary>
    /// A hand-edited file can hold anything, and a recorded device reaches the log and every GUI: it is read through the
    /// type's constructor, so it comes out stripped, trimmed and bounded - within the config's limits, which would
    /// otherwise set the whole file aside - and a fingerprint that is empty once cleaned reads as unknown, not as
    /// another device.
    /// </summary>
    [Fact]
    public void Load_AHandEditedDevice_IsReadInTheFormTheTypeKeeps()
    {
        string overlong = new('f', ConfigLimits.MaxTextLength + 44);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, $$"""
            { "fans": [ { "fanId": "amdgpu/pwm1",
                          "device": { "name": " amd\u001b[2J\r\ngpu ", "fingerprint": "pci:1002‮:73bf\n" } } ],
              "sensors": [ { "sensorId": "amdgpu/temp1", "device": { "name": "amdgpu", "fingerprint": " \t\u0000 " } },
                           { "sensorId": "x/temp1", "device": { "name": "{{overlong}}", "fingerprint": "{{overlong}}" } },
                           { "sensorId": "y/temp1", "device": { "name": null } } ] }
            """);

        AppConfig config = new JsonConfigStore(Path_).Load();

        HardwareDevice fan = Assert.Single(config.Fans).Device!;
        Assert.Equal(("amd[2Jgpu", (string?)"pci:1002:73bf"), (fan.Name, fan.Fingerprint));
        Assert.Equal(("amdgpu", (string?)null), (config.Sensors[0].Device!.Name, config.Sensors[0].Device!.Fingerprint));
        Assert.Equal(HardwareDevice.MaxLength, config.Sensors[1].Device!.Name.Length);
        Assert.Equal(HardwareDevice.MaxLength, config.Sensors[1].Device!.Fingerprint!.Length);
        Assert.Equal(("", (string?)null), (config.Sensors[2].Device!.Name, config.Sensors[2].Device!.Fingerprint));
        Assert.Null(ConfigLimits.Check(config));
        SensorDescriptor detected = new(new SensorId("amdgpu/temp1"), "edge", SensorKind.Temperature, "°C", "amdgpu/temp1",
            new HardwareDevice("amdgpu", "pci:1002:73bf:1849:5201"));
        Assert.Empty(HardwareChanges.Detect(config with { Fans = [], Sensors = [config.Sensors[0]] }, [], [detected], dryRun: false));
    }

    [Fact]
    public void Load_ACalibrationFromBeforeItsSensor_ReadsNone_AndTheSensorAndSeriesSurviveASave()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            { "fans": [ { "fanId": "nct/pwm1", "calibration": { "startPwm": 64, "minRpm": 800, "maxRpm": 1200,
              "samples": [ { "pwm": 0, "rpm": 0 }, { "pwm": 64, "rpm": 800 } ] } } ] }
            """);
        var store = new JsonConfigStore(Path_);

        FanCalibration old = Assert.Single(store.Load().Fans).Calibration!;
        Assert.Null(old.Tachometer); // compared anyway, just never as measured with another sensor
        Assert.Equal([new(0, 0), new(64, 800)], old.Samples);

        store.Save(AppConfig.Empty with { Fans = [new FanConfig { FanId = "nct/pwm1", Calibration = old with { Tachometer = "nct/fan1" } }] });
        FanCalibration reloaded = Assert.Single(store.Load().Fans).Calibration!;
        Assert.Equal("nct/fan1", reloaded.Tachometer);
        Assert.Equal(old.Samples, reloaded.Samples);
    }

    [Fact]
    public void SetAside_MovesTheFileOutOfTheWay_SoTheNextLoadStartsFresh()
    {
        var store = new JsonConfigStore(Path_);
        store.Save(AppConfig.Empty with { FailSafeTempC = 72 });

        string aside = store.SetAside();

        Assert.False(store.Exists);
        Assert.Equal(90, store.Load().FailSafeTempC);
        Assert.Contains("72", File.ReadAllText(aside)); // kept for inspection
    }

    [Fact]
    public void SetAside_Again_KeepsTheFileSetAsideEarlier()
    {
        // The earlier one may be the only copy of the operator's configuration.
        var store = new JsonConfigStore(Path_);
        store.Save(AppConfig.Empty with { FailSafeTempC = 72 });
        string first = store.SetAside();
        store.Save(AppConfig.Empty with { FailSafeTempC = 80 });

        string second = store.SetAside();

        Assert.NotEqual(first, second);
        Assert.Contains("72", File.ReadAllText(first));
        Assert.Contains("80", File.ReadAllText(second));
    }

    [Fact]
    public void Save_PastTheSizeCap_Throws_AndKeepsThePreviousFile()
    {
        var store = new JsonConfigStore(Path_);
        store.Save(AppConfig.Empty with { FailSafeTempC = 72 });
        var huge = AppConfig.Empty with
        {
            Sensors = Enumerable.Range(0, 40_000)
                .Select(i => new SensorConfig { SensorId = $"s{i}", Name = new string('x', 120) })
                .ToArray(),
        };

        Assert.Throws<InvalidOperationException>(() => store.Save(huge));

        Assert.Equal(72, store.Load().FailSafeTempC);
    }

    [Fact]
    public void Load_PastTheLoadCap_Throws_WithoutReadingTheFile()
    {
        Directory.CreateDirectory(_dir);
        using (var file = new FileStream(Path_, FileMode.Create))
            file.SetLength(JsonConfigStore.MaxLoadBytes + 1L); // sparse: no real disk use

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonConfigStore(Path_).Load());
        Assert.Contains(JsonConfigStore.MaxLoadBytes.ToString(), ex.Message); // the size, not a parse error
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var store = new JsonConfigStore(Path_);
        var config = new AppConfig
        {
            PollIntervalMs = 1500,
            FailSafeTempC = 88,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Quiet", SourceSensorId = "hwmon6/temp1", HysteresisC = 3,
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[]
            {
                new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU", AssignedCurveId = "c1", MinPwm = 60 },
            },
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(1500, loaded.PollIntervalMs);
        Assert.Equal(88, loaded.FailSafeTempC);
        Assert.Equal("Quiet", Assert.Single(loaded.Curves).Name);
        Assert.Equal(2, loaded.Curves[0].Points.Count);
        Assert.Equal(3, loaded.Curves[0].HysteresisC);
        var fan = Assert.Single(loaded.Fans);
        Assert.Equal((byte)60, fan.MinPwm);
        Assert.Equal("c1", fan.AssignedCurveId);
    }

    [Fact]
    public void Save_IsAtomic_NoLeftoverTempFile()
    {
        var store = new JsonConfigStore(Path_);
        store.Save(AppConfig.Empty);
        Assert.True(File.Exists(Path_));
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(System.IO.Path.GetFileName));
    }

    [Fact]
    public void Save_DoesNotFollowAPlantedTempSymlink_AndReplacesALinkedConfigItself()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_dir);
        string victim = System.IO.Path.Combine(_dir, "victim");
        File.WriteAllText(victim, "untouched");
        File.CreateSymbolicLink(Path_ + ".tmp", victim);   // the name an older version always used
        File.CreateSymbolicLink(Path_, victim);

        new JsonConfigStore(Path_).Save(AppConfig.Empty);

        Assert.Equal("untouched", File.ReadAllText(victim));
        Assert.Null(new FileInfo(Path_).LinkTarget);         // config.json is a regular file now
        Assert.Equal(1000, new JsonConfigStore(Path_).Load().PollIntervalMs);
    }

    [Fact]
    public void Save_CreatesTheConfigOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        new JsonConfigStore(Path_).Save(AppConfig.Empty);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path_));
    }

    [Fact]
    public void Load_TurnsAWorldReadableConfigOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;
        var store = new JsonConfigStore(Path_);
        store.Save(AppConfig.Empty);
        File.SetUnixFileMode(Path_, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        store.Load();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path_));
    }

    [Fact]
    public void Load_NeverWidensAStricterMode()
    {
        if (OperatingSystem.IsWindows())
            return;
        var store = new JsonConfigStore(Path_);
        store.Save(AppConfig.Empty);
        File.SetUnixFileMode(Path_, UnixFileMode.UserRead | UnixFileMode.GroupRead);

        store.Load();

        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(Path_)); // the group bit goes, no write bit comes
    }

    [Fact]
    public void Load_LeavesTheModeOfAFileThatIsNoConfig()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ kein gültiges json");
        const UnixFileMode worldReadable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead;
        File.SetUnixFileMode(Path_, worldReadable);

        Assert.Throws<InvalidOperationException>(() => new JsonConfigStore(Path_).Load());

        Assert.Equal(worldReadable, File.GetUnixFileMode(Path_)); // whatever is linked there is not the config's to change
    }

    [Fact]
    public void Load_AUtf16File_StillLoads()
    {
        // A config a Windows editor saved as "Unicode" (UTF-16 with a byte order mark) loaded before the read went
        // through a stream; it must not be set aside as corrupt.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ \"failSafeTempC\": 72 }", System.Text.Encoding.Unicode);

        Assert.Equal(72, new JsonConfigStore(Path_).Load().FailSafeTempC);
    }

    [Fact]
    public void Load_ThroughALink_LeavesTheTargetsMode()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_dir);
        string target = System.IO.Path.Combine(_dir, "elsewhere.json");
        File.WriteAllText(target, "{}");
        const UnixFileMode worldReadable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead;
        File.SetUnixFileMode(target, worldReadable);
        File.CreateSymbolicLink(Path_, target);

        new JsonConfigStore(Path_).Load();

        Assert.Equal(worldReadable, File.GetUnixFileMode(target)); // a root daemon with a user's HOME must not change it
    }

    [Fact]
    public void Save_WithAFilePlantedUnderTheOldTempName_LeavesItAlone()
    {
        // Audit W2: a fixed temp name was opened with "create or truncate", so a file another account planted there
        // was adopted - and on Windows it kept its owner through the rename onto config.json.
        Directory.CreateDirectory(_dir);
        string planted = Path_ + ".tmp";
        File.WriteAllText(planted, "planted");

        new JsonConfigStore(Path_).Save(AppConfig.Empty with { FailSafeTempC = 72 });

        Assert.Equal("planted", File.ReadAllText(planted));
        Assert.Equal(72, new JsonConfigStore(Path_).Load().FailSafeTempC);
    }

    [Fact]
    public void Save_BareFilenameWithoutDirectory_WritesInCurrentDirectory()
    {
        // Path.GetDirectoryName("config.json") liefert "" (nicht null) - früher warf Save darüber in
        // Directory.CreateDirectory(""). Ein reiner Dateiname landet im aktuellen Arbeitsverzeichnis.
        Directory.CreateDirectory(_dir);
        string previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_dir);
            var store = new JsonConfigStore("config.json");

            var ex = Record.Exception(() => store.Save(AppConfig.Empty));

            Assert.Null(ex);
            Assert.True(File.Exists(System.IO.Path.Combine(_dir, "config.json")));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSmoothing()
    {
        var store = new JsonConfigStore(Path_);
        store.Save(new AppConfig
        {
            Curves = new[] { new CurveConfig { Id = "c1", Name = "Quiet", SmoothingSeconds = 7 } },
        });

        Assert.Equal(7, Assert.Single(store.Load().Curves).SmoothingSeconds);
    }

    [Fact]
    public void Load_ConfigWithoutSmoothingField_UsesDefault()
    {
        // Bestandsdatei von vor der Glättung: das fehlende Feld muss auf den Default deserialisieren,
        // nicht auf 0 - sonst bekäme genau der Bestand, für den die Glättung gebaut wurde, sie nicht.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            {
              "schemaVersion": 3,
              "curves": [ { "id": "c1", "name": "Quiet", "hysteresisC": 2 } ]
            }
            """);

        CurveConfig curve = Assert.Single(new JsonConfigStore(Path_).Load().Curves);
        Assert.Equal(CurveConfig.DefaultSmoothingSeconds, curve.SmoothingSeconds);
    }

    [Fact]
    public void Load_CorruptFile_Throws()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ kein gültiges json");
        Assert.Throws<InvalidOperationException>(() => new JsonConfigStore(Path_).Load());
    }

    [Fact]
    public void DefaultPath_HonorsLinfanConfigEnv()
    {
        string? previous = Environment.GetEnvironmentVariable("LINFAN_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("LINFAN_CONFIG", Path_);

            Assert.Equal(Path_, JsonConfigStore.DefaultPath());
            Assert.Equal(Path_, new JsonConfigStore().ConfigPath); // ohne expliziten Pfad → Override greift
        }
        finally
        {
            Environment.SetEnvironmentVariable("LINFAN_CONFIG", previous);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
